namespace Deedbox;

/// <summary>A ring of AES master keys from configuration: <c>v2:&lt;base64&gt;,v1:&lt;base64&gt;</c>, current first.</summary>
internal sealed class KeyRingMasterKey : IMasterKeyProvider
{
    private const string Prefix = "env:";
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public KeyRingMasterKey(string ring)
    {
        foreach (var entry in ring.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            byte[] key;
            try
            {
                key = colon > 0 ? Convert.FromBase64String(entry[(colon + 1)..]) : [];
            }
            catch (FormatException)
            {
                key = [];
            }

            var version = colon > 0 ? entry[..colon] : "";
            if (key.Length != Crypto.KeySize || version.Length == 0 || !_keys.TryAdd(Prefix + version, key))
            {
                throw new DeedboxException(Errors.MasterKeyUnusable,
                    "The master key ring is not valid. Use unique versions and 32-byte base64 keys: v2:<base64>,v1:<base64>, current first.");
            }

            if (_keys.Count == 1)
                KeyVersion = Prefix + version;
        }

        if (_keys.Count == 0)
            throw new DeedboxException(Errors.MasterKeyUnusable, "The master key ring is empty.");
    }

    public string KeyVersion { get; } = "";

    public Task<WrappedKey> WrapAsync(byte[] key, CancellationToken ct) =>
        Task.FromResult(new WrappedKey { Bytes = Crypto.Seal(_keys[KeyVersion], key, "deedbox:master:" + KeyVersion), KeyVersion = KeyVersion });

    public Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        if (!_keys.TryGetValue(keyVersion, out var master))
            throw new DeedboxException(Errors.MasterKeyUnusable, $"The master key ring has no key {keyVersion}. Add it to the ring as an older version.");
        return Task.FromResult(Crypto.Open(master, wrappedKey, "deedbox:master:" + keyVersion));
    }
}

/// <summary>
/// The master key stored in the Deedbox database itself, as master_keys row (empty tenant, version 0). It is created
/// on the first wrap. It gives erasure but no protection for backups or database-only leaks.
/// </summary>
internal sealed class DatabaseMasterKey(DeedboxProvider provider) : IMasterKeyProvider
{
    public const string Version = "database:0";

    public string KeyVersion => Version;

    private byte[]? _key;

    public async Task<WrappedKey> WrapAsync(byte[] key, CancellationToken ct) =>
        new() { Bytes = Crypto.Seal(await Key(create: true, ct), key, "deedbox:master:" + Version), KeyVersion = Version };

    public async Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        if (keyVersion != Version)
            throw new DeedboxException(Errors.MasterKeyUnusable, $"Key {keyVersion} was not wrapped by the database master key. Configure the key mode that wrapped it.");
        return Crypto.Open(_key ?? await Key(create: false, ct), wrappedKey, "deedbox:master:" + Version);
    }

    /// <summary>
    /// The key from its row. A wrap reads the row every time: a re-wrap to another mode deletes it, and an instance
    /// that wrapped with a key kept in memory would write keys that nothing can unwrap after its next start. A wrap
    /// creates the row when it is missing, so what it wraps stays readable. An unwrap never creates it: a row that is
    /// gone must fail, not become a new key.
    /// </summary>
    private async Task<byte[]> Key(bool create, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        var row = await Read();
        if (row is null && create)
        {
            await provider.InsertMasterKey(connection, null, new MasterKeyRow("", 0, Crypto.NewKey(), "plain"), ct);
            row = await Read();
        }

        if (row is null)
        {
            throw new DeedboxException(Errors.MasterKeyUnusable,
                "The database holds no master key: 'deedbox keys rewrap' moved the keys to another key mode. Configure that key mode in AddDeedbox.");
        }

        async Task<MasterKeyRow?> Read() =>
            (await provider.ReadMasterKeys(connection, null, ct)).FirstOrDefault(r => r.TenantId.Length == 0 && r.KeyVersion == 0);

        return _key = row.WrappedKey;
    }
}

/// <summary>
/// A master key that wraps, plus master keys that only unwrap. A change of key mode on a running app needs both: every
/// instance must read rows under the old key and under the new key while the rows are re-wrapped.
/// </summary>
internal sealed class MasterKeys(IMasterKeyProvider current, IReadOnlyList<IMasterKeyProvider> others) : IMasterKeyProvider
{
    public string KeyVersion => current.KeyVersion;

    public Task<WrappedKey> WrapAsync(byte[] key, CancellationToken ct) => current.WrapAsync(key, ct);

    public async Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        // Each provider refuses a version that is not its own, so the first one that unwraps is the one that wrapped.
        var failures = new List<Exception>();
        foreach (var provider in others.Prepend(current))
        {
            try
            {
                return await provider.UnwrapAsync(wrappedKey, keyVersion, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }

        throw new DeedboxException(Errors.MasterKeyUnusable,
            $"None of the {failures.Count} configured master keys can unwrap a key that {keyVersion} wrapped.", new AggregateException(failures));
    }
}
