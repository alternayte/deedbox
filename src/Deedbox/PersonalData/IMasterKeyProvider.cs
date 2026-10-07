namespace Deedbox;

/// <summary>
/// Wraps and unwraps Deedbox's per-tenant intermediate keys and pseudonym secrets with a master key. Deedbox calls it
/// once per tenant at start-up and when it creates or re-wraps a key or a secret; reads and rebuilds never call it.
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// Identifies the master key that <see cref="WrapAsync"/> uses now, including the provider, such as <c>env:v2</c>.
    /// Deedbox shows it in messages and skips a re-wrap of rows that it already names. The version that Deedbox stores
    /// with a wrapped key is the one <see cref="WrapAsync"/> returns, never this property.
    /// </summary>
    string KeyVersion { get; }

    /// <summary>Wraps a 32-byte key with the current master key.</summary>
    /// <param name="key">The key to wrap.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>
    /// The wrapped bytes and the version of the master key that wrapped them, from the same call. A key service can
    /// rotate its key between two calls, so the version must not be read afterwards.
    /// </returns>
    Task<WrappedKey> WrapAsync(byte[] key, CancellationToken ct);

    /// <summary>Unwraps a key. Throws when the version is unknown or the wrapped bytes do not verify.</summary>
    /// <param name="wrappedKey">The bytes that <see cref="WrapAsync"/> returned.</param>
    /// <param name="keyVersion">The version that <see cref="WrapAsync"/> returned with them.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct);
}

/// <summary>A key wrapped by a master key, and the version of the master key that wrapped it.</summary>
public sealed record WrappedKey
{
    /// <summary>The wrapped bytes.</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>
    /// The master key that wrapped them, including the provider, such as <c>env:v2</c>. It is stored with the bytes and
    /// passed back to <see cref="IMasterKeyProvider.UnwrapAsync"/>. Its prefix also names the wrap format, so a provider
    /// that changes its algorithm uses a new prefix.
    /// </summary>
    public required string KeyVersion { get; init; }
}
