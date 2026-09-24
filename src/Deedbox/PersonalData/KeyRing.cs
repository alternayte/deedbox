using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;

namespace Deedbox;

/// <summary>
/// Each tenant's intermediate keys, unwrapped once with the master key and kept for the life of the process. Subject
/// keys are never cached here; <see cref="SubjectKeys"/> caches them for one operation only, so an erasure on another
/// instance takes effect at once.
/// </summary>
internal sealed class KeyRing(DeedboxProvider provider, IMasterKeyProvider master)
{
    private readonly ConcurrentDictionary<string, SortedDictionary<int, byte[]>> _tenants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IMasterKeyProvider Master => master;

    /// <summary>Unwraps every tenant key. A key the master key cannot unwrap fails start-up; it never reads as erased.</summary>
    public async Task LoadAll(CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await Load(await provider.ReadMasterKeys(connection, null, ct), ct);
    }

    /// <summary>The tenant's newest intermediate key, created and committed on first use.</summary>
    public async Task<(int Version, byte[] Key)> Current(string tenantId, CancellationToken ct)
    {
        if (_tenants.TryGetValue(tenantId, out var keys))
            return Newest(keys);

        await _lock.WaitAsync(ct);
        try
        {
            await using var connection = provider.CreateConnection();
            await connection.OpenAsync(ct);
            await Load(await provider.ReadMasterKeys(connection, null, ct), ct);
            if (_tenants.TryGetValue(tenantId, out keys))
                return Newest(keys);

            // Created in its own committed transaction: an append that rolls back must never leave a key in memory only.
            var wrapped = await master.WrapAsync(Crypto.NewKey(), ct);
            await provider.InsertMasterKey(connection, null, new MasterKeyRow(tenantId, 1, wrapped, master.KeyVersion), ct);
            await Load(await provider.ReadMasterKeys(connection, null, ct), ct);
            return Newest(_tenants[tenantId]);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A tenant key by version, or null when the tenant's keys were deleted (the tenant was shredded).</summary>
    public async Task<byte[]?> Find(string tenantId, int version, CancellationToken ct)
    {
        if (_tenants.TryGetValue(tenantId, out var keys) && keys.TryGetValue(version, out var key))
            return key;

        await _lock.WaitAsync(ct);
        try
        {
            await using var connection = provider.CreateConnection();
            await connection.OpenAsync(ct);
            await Load(await provider.ReadMasterKeys(connection, null, ct), ct);
            return _tenants.TryGetValue(tenantId, out keys) && keys.TryGetValue(version, out key) ? key : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Re-wraps every tenant key with <paramref name="target"/>, in one transaction. Events and subject keys are not
    /// touched. Leaving database mode also deletes the master key stored in the database.
    /// </summary>
    public async Task<int> Rewrap(IMasterKeyProvider target, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = await provider.ReadMasterKeys(connection, transaction, ct);
        var count = 0;
        foreach (var row in rows.Where(r => r.KeyVersion > 0))
        {
            if (row.WrappedBy == target.KeyVersion)
                continue;
            var key = await master.UnwrapAsync(row.WrappedKey, row.WrappedBy, ct);
            await provider.UpdateMasterKey(connection, transaction, row with { WrappedKey = await target.WrapAsync(key, ct), WrappedBy = target.KeyVersion }, ct);
            count++;
        }

        if (target is not DatabaseMasterKey && rows.Any(r => r.KeyVersion == 0))
            await provider.DeleteMasterKey(connection, transaction, "", 0, ct);

        await transaction.CommitAsync(ct);
        return count;
    }

    private async Task Load(List<MasterKeyRow> rows, CancellationToken ct)
    {
        foreach (var row in rows.Where(r => r.KeyVersion > 0))
        {
            var keys = _tenants.GetOrAdd(row.TenantId, _ => []);
            lock (keys)
            {
                if (keys.ContainsKey(row.KeyVersion))
                    continue;
            }

            byte[] key;
            try
            {
                key = await master.UnwrapAsync(row.WrappedKey, row.WrappedBy, ct);
            }
            catch (Exception ex) when (ex is CryptographicException or DeedboxException)
            {
                throw new DeedboxException(Errors.MasterKeyUnusable,
                    $"The master key ({master.KeyVersion}) cannot unwrap tenant '{row.TenantId}' key version {row.KeyVersion}, which {row.WrappedBy} wrapped. " +
                    "Configure the master key that wrapped it, or add its version to the key ring. Deedbox will not start rather than read personal data as erased.", ex);
            }

            lock (keys)
                keys.TryAdd(row.KeyVersion, key);
        }
    }

    private static (int, byte[]) Newest(SortedDictionary<int, byte[]> keys)
    {
        lock (keys)
            return (keys.Keys.Max(), keys[keys.Keys.Max()]);
    }
}

/// <summary>Subject keys for one operation: an append, a load, or a runner batch. Never shared across operations.</summary>
internal sealed class SubjectKeys(KeyRing ring, DeedboxProvider provider, DbConnection connection, DbTransaction? transaction, string tenantId)
{
    private readonly Dictionary<string, byte[]?> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string KeyId, byte[] Key)> _bySubject = new(StringComparer.Ordinal);

    /// <summary>The subject's key, created in the append's transaction when it has none. The row stays share-locked until commit.</summary>
    public async Task<(string KeyId, byte[] Key)> ForSubject(string subjectId, CancellationToken ct)
    {
        if (_bySubject.TryGetValue(subjectId, out var cached))
            return cached;

        var write = transaction ?? throw new InvalidOperationException("Creating a subject key needs a transaction.");
        var row = await provider.ReadSubjectKey(connection, write, tenantId, subjectId, ct);
        if (row is null)
        {
            var (version, intermediate) = await ring.Current(tenantId, ct);
            var keyId = Crypto.NewKeyId();
            await provider.InsertSubjectKey(connection, write,
                new SubjectKeyRow(tenantId, subjectId, keyId, Crypto.WrapSubjectKey(intermediate, version, Crypto.NewKey(), tenantId, keyId)), ct);
            row = await provider.ReadSubjectKey(connection, write, tenantId, subjectId, ct)
                ?? throw new InvalidOperationException($"The key for subject '{subjectId}' vanished while it was created.");
        }

        var key = await Unwrap(row.KeyId, row.WrappedKey, ct)
            ?? throw new DeedboxException(Errors.KeyMaterialCorrupt, $"Tenant '{tenantId}' has no key to unwrap subject keys with; its keys were deleted.");
        _bySubject[subjectId] = (row.KeyId, key);
        _byId[row.KeyId] = key;
        return (row.KeyId, key);
    }

    /// <summary>Loads every listed key in one query. Keys with no row were erased and read as null.</summary>
    public async Task Prefetch(IEnumerable<string> keyIds, CancellationToken ct)
    {
        var missing = keyIds.Where(id => !_byId.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
            return;

        var wrapped = await provider.ReadSubjectKeysById(connection, transaction, tenantId, missing, ct);
        foreach (var id in missing)
            _byId[id] = wrapped.TryGetValue(id, out var bytes) ? await Unwrap(id, bytes, ct) : null;
    }

    /// <summary>A prefetched key, or null for an erased subject.</summary>
    public byte[]? ById(string keyId) => _byId.GetValueOrDefault(keyId);

    private async Task<byte[]?> Unwrap(string keyId, byte[] wrapped, CancellationToken ct)
    {
        var intermediate = await ring.Find(tenantId, Crypto.IntermediateVersionOf(wrapped), ct);
        if (intermediate is null)
            return null; // The tenant's keys were deleted: everything under them reads as erased.

        try
        {
            return Crypto.UnwrapSubjectKey(intermediate, wrapped, tenantId, keyId);
        }
        catch (CryptographicException ex)
        {
            throw new DeedboxException(Errors.KeyMaterialCorrupt,
                $"Subject key {keyId} of tenant '{tenantId}' does not verify. The key row was altered or copied; Deedbox will not read it as erased.", ex);
        }
    }
}
