namespace Deedbox;

/// <summary>
/// Wraps and unwraps Deedbox's per-tenant intermediate keys with a master key. Deedbox calls it once per tenant at
/// start-up and when it creates or re-wraps a tenant key; reads and rebuilds never call it.
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// Identifies the master key that <see cref="WrapAsync"/> uses, including the provider, such as <c>env:v2</c>.
    /// Deedbox stores it with each wrapped key and passes it back to <see cref="UnwrapAsync"/>.
    /// </summary>
    string KeyVersion { get; }

    /// <summary>Wraps a 32-byte key with the current master key.</summary>
    /// <param name="key">The key to wrap.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<byte[]> WrapAsync(byte[] key, CancellationToken ct);

    /// <summary>Unwraps a key. Throws when the version is unknown or the wrapped bytes do not verify.</summary>
    /// <param name="wrappedKey">What <see cref="WrapAsync"/> returned.</param>
    /// <param name="keyVersion">The <see cref="KeyVersion"/> that wrapped it.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct);
}
