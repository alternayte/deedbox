using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Deedbox;

/// <summary>
/// AES-256-GCM with a random 12-byte nonce and a 16-byte tag. Every use passes associated data that names what the
/// bytes are, so a wrapped key or a field cannot be moved to another row or field and still verify.
/// </summary>
internal static class Crypto
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>A random key ID: 32 lower-case hex characters, so no collation can confuse two IDs.</summary>
    public static string NewKeyId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>nonce | ciphertext | tag.</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, string associatedData)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), Encoding.UTF8.GetBytes(associatedData));
        return output;
    }

    /// <summary>Opens <see cref="Seal"/>'s output. Throws <see cref="CryptographicException"/> when anything was altered.</summary>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedBytes, string associatedData)
    {
        if (sealedBytes.Length < NonceSize + TagSize)
            throw new CryptographicException("The sealed data is too short.");
        var length = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBytes[..NonceSize], sealedBytes.Slice(NonceSize, length), sealedBytes[(NonceSize + length)..], plaintext, Encoding.UTF8.GetBytes(associatedData));
        return plaintext;
    }

    // ---- Subject keys, wrapped by a tenant's intermediate key: [intermediate version, 4 bytes big-endian] | sealed ----

    public static byte[] WrapSubjectKey(byte[] intermediate, int intermediateVersion, byte[] subjectKey, string tenantId, string keyId)
    {
        var sealedKey = Seal(intermediate, subjectKey, SubjectKeyContext(tenantId, keyId));
        var output = new byte[4 + sealedKey.Length];
        BinaryPrimitives.WriteInt32BigEndian(output, intermediateVersion);
        sealedKey.CopyTo(output, 4);
        return output;
    }

    public static int IntermediateVersionOf(byte[] wrappedSubjectKey) => BinaryPrimitives.ReadInt32BigEndian(wrappedSubjectKey);

    public static byte[] UnwrapSubjectKey(byte[] intermediate, byte[] wrapped, string tenantId, string keyId) =>
        Open(intermediate, wrapped.AsSpan(4), SubjectKeyContext(tenantId, keyId));

    private static string SubjectKeyContext(string tenantId, string keyId) => $"deedbox:subject-key:{tenantId}\0{keyId}";

    // ---- Personal-data fields: {"$enc": "v1:<keyId>:<nonce>:<ciphertext and tag>"}, base64url ----

    public const string Marker = "$enc";

    public static string EncryptField(byte[] subjectKey, string keyId, string json)
    {
        var sealedBytes = Seal(subjectKey, Encoding.UTF8.GetBytes(json), FieldContext(keyId));
        return $"v1:{keyId}:{Base64Url(sealedBytes.AsSpan(0, NonceSize))}:{Base64Url(sealedBytes.AsSpan(NonceSize))}";
    }

    /// <summary>The key ID of a field marker, or null when the text is not a v1 marker.</summary>
    public static string? FieldKeyId(string marker)
    {
        var parts = marker.Split(':');
        return parts.Length == 4 && parts[0] == "v1" ? parts[1] : null;
    }

    public static string DecryptField(byte[] subjectKey, string marker)
    {
        var parts = marker.Split(':');
        var nonce = FromBase64Url(parts[2]);
        var body = FromBase64Url(parts[3]);
        var sealedBytes = new byte[nonce.Length + body.Length];
        nonce.CopyTo(sealedBytes, 0);
        body.CopyTo(sealedBytes, nonce.Length);
        return Encoding.UTF8.GetString(Open(subjectKey, sealedBytes, FieldContext(parts[1])));
    }

    private static string FieldContext(string keyId) => $"deedbox:field:v1:{keyId}";

    // ---- Stored state of streams with personal data: {"$state": "v1:<tenant key version>:<sealed, base64url>"} ----

    public const string StateMarker = "$state";

    public static string SealState(byte[] tenantKey, int tenantKeyVersion, string tenantId, string streamId, string json)
    {
        var sealedBytes = Seal(tenantKey, Encoding.UTF8.GetBytes(json), StateContext(tenantId, streamId));
        return $"v1:{tenantKeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Base64Url(sealedBytes)}";
    }

    public static int StateKeyVersion(string marker) => int.Parse(marker.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);

    public static string OpenState(byte[] tenantKey, string tenantId, string streamId, string marker) =>
        Encoding.UTF8.GetString(Open(tenantKey, FromBase64Url(marker.Split(':')[2]), StateContext(tenantId, streamId)));

    private static string StateContext(string tenantId, string streamId) => $"deedbox:state:v1:{tenantId}\0{streamId}";

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
    }
}
