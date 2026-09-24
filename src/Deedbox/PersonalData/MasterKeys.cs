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

    public Task<byte[]> WrapAsync(byte[] key, CancellationToken ct) => Task.FromResult(Crypto.Seal(_keys[KeyVersion], key, "deedbox:master:" + KeyVersion));

    public Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        if (!_keys.TryGetValue(keyVersion, out var master))
            throw new DeedboxException(Errors.MasterKeyUnusable, $"The master key ring has no key {keyVersion}. Add it to the ring as an older version.");
        return Task.FromResult(Crypto.Open(master, wrappedKey, "deedbox:master:" + keyVersion));
    }
}

/// <summary>
/// The master key stored in the Deedbox database itself, as master_keys row (empty tenant, version 0). It is created
/// on first use. It gives erasure but no protection for backups or database-only leaks.
/// </summary>
internal sealed class DatabaseMasterKey(DeedboxProvider provider) : IMasterKeyProvider
{
    public const string Version = "database:0";
    private readonly SemaphoreSlim _load = new(1, 1);
    private byte[]? _key;

    public string KeyVersion => Version;

    public async Task<byte[]> WrapAsync(byte[] key, CancellationToken ct) => Crypto.Seal(await Key(ct), key, "deedbox:master:" + Version);

    public async Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        if (keyVersion != Version)
            throw new DeedboxException(Errors.MasterKeyUnusable, $"Key {keyVersion} was not wrapped by the database master key. Configure the key mode that wrapped it.");
        return Crypto.Open(await Key(ct), wrappedKey, "deedbox:master:" + Version);
    }

    private async Task<byte[]> Key(CancellationToken ct)
    {
        if (_key is not null)
            return _key;

        await _load.WaitAsync(ct);
        try
        {
            if (_key is not null)
                return _key;

            await using var connection = provider.CreateConnection();
            await connection.OpenAsync(ct);
            await provider.InsertMasterKey(connection, null, new MasterKeyRow("", 0, Crypto.NewKey(), "plain"), ct);
            var row = (await provider.ReadMasterKeys(connection, null, ct)).Single(r => r.TenantId.Length == 0 && r.KeyVersion == 0);
            return _key = row.WrappedKey;
        }
        finally
        {
            _load.Release();
        }
    }
}
