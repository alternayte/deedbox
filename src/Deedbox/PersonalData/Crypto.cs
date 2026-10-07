using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Deedbox;

/// <summary>
/// AES-256-GCM with a random 12-byte nonce and a 16-byte tag. Every use passes associated data that names what the
/// bytes are, so a wrapped key, a field or a stored state cannot be moved to another place and still verify.
/// </summary>
/// <remarks>
/// Every stored blob names its format, so a later format has a way in. Fields and stored state start with "v1:" or
/// "v2:". A wrapped subject key starts with a marker byte in format 2. A wrapped tenant key or pseudonym secret is
/// named by its row's wrapped_by column, which the master key provider chooses. Format 1 stays readable for ever.
/// Format 2 is written only when every instance of the store can read it (<see cref="Formats"/>).
/// </remarks>
internal static class Crypto
{
    /// <summary>The newest format this build writes and reads. Each instance records it with its heartbeat.</summary>
    public const int Formats = 2;

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

    // ---- Subject keys, wrapped by a tenant's intermediate key ----
    // Format 1: [intermediate version, 4 bytes big-endian] | sealed.
    // Format 2: [0xD2] | [intermediate version, 4 bytes big-endian] | sealed. A format 1 blob starts with 0x00 for any
    // version below 2^24, so the first byte tells the two apart.

    private const byte SubjectKeyFormat2 = 0xD2;

    public static byte[] WrapSubjectKey(byte[] intermediate, int intermediateVersion, byte[] subjectKey, string tenantId, string keyId, int format)
    {
        var header = format >= 2 ? 5 : 4;
        var sealedKey = Seal(intermediate, subjectKey, SubjectKeyContext(tenantId, keyId, format >= 2 ? 2 : 1));
        var output = new byte[header + sealedKey.Length];
        if (format >= 2)
            output[0] = SubjectKeyFormat2;
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(header - 4), intermediateVersion);
        sealedKey.CopyTo(output, header);
        return output;
    }

    private static int SubjectKeyHeader(byte[] wrapped) => wrapped[0] switch
    {
        0x00 => 4,
        SubjectKeyFormat2 => 5,
        _ => throw new CryptographicException("The wrapped subject key has a format that this version of Deedbox does not know."),
    };

    public static int IntermediateVersionOf(byte[] wrappedSubjectKey) =>
        BinaryPrimitives.ReadInt32BigEndian(wrappedSubjectKey.AsSpan(SubjectKeyHeader(wrappedSubjectKey) - 4));

    public static byte[] UnwrapSubjectKey(byte[] intermediate, byte[] wrapped, string tenantId, string keyId)
    {
        var header = SubjectKeyHeader(wrapped);
        return Open(intermediate, wrapped.AsSpan(header), SubjectKeyContext(tenantId, keyId, header == 5 ? 2 : 1));
    }

    private static string SubjectKeyContext(string tenantId, string keyId, int format) =>
        format == 1 ? $"deedbox:subject-key:{tenantId}\0{keyId}" : $"deedbox:subject-key:v2:{tenantId}\0{keyId}";

    // ---- Personal-data fields: {"$enc": "<format>:<keyId>:<nonce>:<ciphertext and tag>"}, base64url ----
    // Format 1 binds the key ID only. Format 2 also binds the event ID and the field's JSON name, so a field copied to
    // another event or another property of the same subject does not verify.

    public const string Marker = "$enc";

    public static string EncryptField(byte[] subjectKey, string keyId, string json, Guid eventId, string field, int format)
    {
        var version = format >= 2 ? 2 : 1;
        var sealedBytes = Seal(subjectKey, Encoding.UTF8.GetBytes(json), FieldContext(version, keyId, eventId, field));
        return $"v{version}:{keyId}:{Base64Url(sealedBytes.AsSpan(0, NonceSize))}:{Base64Url(sealedBytes.AsSpan(NonceSize))}";
    }

    /// <summary>The key ID of a field marker, or null when the text is not a marker.</summary>
    public static string? FieldKeyId(string marker)
    {
        var parts = marker.Split(':');
        return parts.Length == 4 && parts[0] is ['v', >= '1' and <= '9'] && parts[1].Length == 32 ? parts[1] : null;
    }

    public static string DecryptField(byte[] subjectKey, string marker, Guid eventId, string? field)
    {
        var parts = marker.Split(':');
        var version = parts[0] switch
        {
            "v1" => 1,
            "v2" => 2,
            _ => throw new CryptographicException($"The field has format {parts[0]}, which this version of Deedbox does not know. A newer version wrote it."),
        };
        var nonce = FromBase64Url(parts[2]);
        var body = FromBase64Url(parts[3]);
        var sealedBytes = new byte[nonce.Length + body.Length];
        nonce.CopyTo(sealedBytes, 0);
        body.CopyTo(sealedBytes, nonce.Length);
        return Encoding.UTF8.GetString(Open(subjectKey, sealedBytes, FieldContext(version, parts[1], eventId, field ?? "")));
    }

    private static string FieldContext(int version, string keyId, Guid eventId, string field) =>
        version == 1 ? $"deedbox:field:v1:{keyId}" : $"deedbox:field:v2:{keyId}\0{eventId:D}\0{field}";

    // ---- Stored state of streams with personal data: {"$state": "<format>:<tenant key version>:<sealed, base64url>"} ----
    // Format 1 seals with the tenant key. A tenant key lives for the life of the tenant and sealed every stored state,
    // so a busy tenant could pass the number of messages that one AES-GCM key may seal with random nonces (2^32).
    // Format 2 seals with a key derived from the tenant key for one stream, so the bound applies per stream.

    public const string StateMarker = "$state";

    public static string SealState(byte[] tenantKey, int tenantKeyVersion, string tenantId, string streamId, string json, int format)
    {
        var version = format >= 2 ? 2 : 1;
        var key = version == 2 ? StateKey(tenantKey, tenantId, streamId) : tenantKey;
        var sealedBytes = Seal(key, Encoding.UTF8.GetBytes(json), StateContext(version, tenantId, streamId));
        return $"v{version}:{tenantKeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{Base64Url(sealedBytes)}";
    }

    /// <summary>
    /// The format and tenant key version of a state marker, or null when a newer version of Deedbox wrote it. Such a
    /// state is not an error: the caller rebuilds the state from the events.
    /// </summary>
    public static (int Format, int KeyVersion)? StateHeader(string marker)
    {
        var parts = marker.Split(':');
        if (parts.Length != 3 || parts[0] is not ("v1" or "v2")
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var keyVersion))
        {
            return null;
        }

        return (parts[0] == "v2" ? 2 : 1, keyVersion);
    }

    public static string OpenState(byte[] tenantKey, int format, string tenantId, string streamId, string marker)
    {
        var key = format == 2 ? StateKey(tenantKey, tenantId, streamId) : tenantKey;
        return Encoding.UTF8.GetString(Open(key, FromBase64Url(marker.Split(':')[2]), StateContext(format, tenantId, streamId)));
    }

    private static byte[] StateKey(byte[] tenantKey, string tenantId, string streamId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, tenantKey, KeySize, salt: null, info: Encoding.UTF8.GetBytes($"deedbox:state-key:v2:{tenantId}\0{streamId}"));

    private static string StateContext(int format, string tenantId, string streamId) => $"deedbox:state:v{format}:{tenantId}\0{streamId}";

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
    }
}
