using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace Deedbox;

/// <summary>Keeps the Deedbox master key in Azure Key Vault or Managed HSM.</summary>
public static class DeedboxAzureKeyVaultExtensions
{
    /// <summary>
    /// Wraps tenant keys with a Key Vault key. Deedbox calls Key Vault once per tenant at start-up and when it creates
    /// or re-wraps a tenant key; reads never call it. Keys wrapped by an older version of the key stay readable.
    /// </summary>
    /// <param name="keys">The keys builder.</param>
    /// <param name="keyId">The key's identifier, with or without a version; the current version wraps new keys.</param>
    /// <param name="credential">The credential, such as a managed identity.</param>
    public static KeysBuilder UseAzureKeyVault(this KeysBuilder keys, Uri keyId, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(credential);
        return keys.Use(new AzureKeyVaultMasterKey(new CryptographyClient(keyId, credential), id => new CryptographyClient(id, credential), KeyWrapAlgorithm.RsaOaep256));
    }

    /// <summary>
    /// Wraps tenant keys with a <see cref="CryptographyClient"/> you build, such as one with custom retry options or one
    /// for a Managed HSM AES key with <see cref="KeyWrapAlgorithm.A256KW"/>.
    /// </summary>
    /// <param name="keys">The keys builder.</param>
    /// <param name="client">A client for the current key version.</param>
    /// <param name="versionClient">Builds a client for another key version, to unwrap keys it wrapped; null allows only the client's version.</param>
    /// <param name="algorithm">The wrap algorithm; RSA-OAEP-256 by default.</param>
    public static KeysBuilder UseAzureKeyVault(this KeysBuilder keys, CryptographyClient client, Func<Uri, CryptographyClient>? versionClient = null, KeyWrapAlgorithm? algorithm = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(client);
        return keys.Use(new AzureKeyVaultMasterKey(client, versionClient, algorithm ?? KeyWrapAlgorithm.RsaOaep256));
    }
}

/// <summary>A Key Vault key as the Deedbox master key. Its version string is <c>azure:</c> plus the versioned key ID.</summary>
internal sealed class AzureKeyVaultMasterKey(CryptographyClient client, Func<Uri, CryptographyClient>? versionClient, KeyWrapAlgorithm algorithm) : IMasterKeyProvider
{
    private const string Prefix = "azure:";
    private string? _keyVersion;

    /// <summary>The versioned key ID after the first wrap; before it, the configured key ID.</summary>
    public string KeyVersion => _keyVersion ?? Prefix + client.KeyId;

    public async Task<byte[]> WrapAsync(byte[] key, CancellationToken ct)
    {
        var result = await client.WrapKeyAsync(algorithm, key, ct);
        _keyVersion = Prefix + result.KeyId;
        return result.EncryptedKey;
    }

    public async Task<byte[]> UnwrapAsync(byte[] wrappedKey, string keyVersion, CancellationToken ct)
    {
        if (!keyVersion.StartsWith(Prefix, StringComparison.Ordinal))
            throw new DeedboxException(Errors.MasterKeyUnusable, $"Key {keyVersion} was not wrapped by Azure Key Vault. Configure the key mode that wrapped it.");

        var id = keyVersion[Prefix.Length..];
        CryptographyClient unwrapper;
        if (keyVersion == KeyVersion)
            unwrapper = client;
        else if (versionClient is not null)
            unwrapper = versionClient(new Uri(id));
        else
            throw new DeedboxException(Errors.MasterKeyUnusable, $"Key {keyVersion} was wrapped by another Key Vault key version, and no client for other versions is configured.");
        var result = await unwrapper.UnwrapKeyAsync(algorithm, wrappedKey, ct);
        return result.Key;
    }
}
