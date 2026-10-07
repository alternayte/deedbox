namespace Deedbox;

/// <summary>Chooses where the master key lives, inside <c>Keys(...)</c>. Choosing is required when any event has personal data.</summary>
public sealed class KeysBuilder
{
    internal KeysBuilder()
    {
    }

    private readonly List<Func<DeedboxProvider, IMasterKeyProvider>> _unwrapOnly = [];
    private Func<DeedboxProvider, IMasterKeyProvider>? _current;

    internal Func<DeedboxProvider, IMasterKeyProvider>? Factory => _current is not { } current
        ? null
        : _unwrapOnly.Count == 0 ? current : provider => new MasterKeys(current(provider), [.. _unwrapOnly.Select(create => create(provider))]);

    internal string? Placeholder { get; private set; }

    /// <summary>
    /// Keeps the master key in the Deedbox database. Erasure works, but anyone with the database or a backup can read
    /// personal data. Use it to start; move to another mode with <c>deedbox keys rewrap</c>.
    /// </summary>
    public KeysBuilder StoreInDatabase() => Set(provider => new DatabaseMasterKey(provider));

    /// <summary>
    /// Reads the master key ring from an environment variable, such as one set from a Kubernetes Secret. The ring is
    /// <c>v2:&lt;base64 key&gt;,v1:&lt;base64 key&gt;</c>: 32-byte keys, current first; older versions stay for unwrapping.
    /// Losing every copy of the key loses all personal data.
    /// </summary>
    /// <param name="variable">The environment variable.</param>
    public KeysBuilder FromEnvironment(string variable = "DEEDBOX_MASTER_KEY")
    {
        ArgumentNullException.ThrowIfNull(variable);
        var ring = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(ring))
        {
            throw new DeedboxException(Errors.MasterKeyUnusable,
                $"Environment variable {variable} is not set, so Deedbox has no master key. Set it to a key ring such as v1:<base64 of 32 random bytes>.");
        }

        return FromKeyRing(ring);
    }

    /// <summary>Uses a master key ring from configuration, in the same format as <see cref="FromEnvironment"/>.</summary>
    /// <param name="keyRing">The key ring.</param>
    public KeysBuilder FromKeyRing(string keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        var provider = new KeyRingMasterKey(keyRing);
        return Set(_ => provider);
    }

    /// <summary>Uses a key service, such as Azure Key Vault through Deedbox.Keys.AzureKeyVault.</summary>
    /// <param name="provider">The master key provider.</param>
    public KeysBuilder Use(IMasterKeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Set(_ => provider);
    }

    /// <summary>Reads erased string properties as <paramref name="placeholder"/> instead of null.</summary>
    /// <param name="placeholder">The text, such as <c>[erased]</c>.</param>
    public KeysBuilder RedactWith(string placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        Placeholder = placeholder;
        return this;
    }

    /// <summary>
    /// Adds a master key that only unwraps. Use it to change the key mode, or the key, while the app runs: first deploy
    /// every instance with the new key here, then deploy with the new key as the mode and the old key here, then run
    /// <c>deedbox keys rewrap</c>, then remove the old key. At each step every instance can read every row.
    /// </summary>
    /// <param name="other">Chooses the other key with the same methods as the key mode, such as <c>k => k.StoreInDatabase()</c>.</param>
    public KeysBuilder AlsoUnwrapWith(Action<KeysBuilder> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var builder = new KeysBuilder();
        other(builder);
        _unwrapOnly.Add(builder.Factory ?? throw new ArgumentException("Choose a key inside AlsoUnwrapWith, such as k => k.StoreInDatabase().", nameof(other)));
        return this;
    }

    private KeysBuilder Set(Func<DeedboxProvider, IMasterKeyProvider> factory)
    {
        _current = factory;
        return this;
    }
}
