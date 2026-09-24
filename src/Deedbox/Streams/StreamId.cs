using System.Security.Cryptography;
using System.Text;

namespace Deedbox;

/// <summary>Helpers that turn GUIDs and natural keys into stream IDs. Stream IDs are strings.</summary>
public static class StreamId
{
    /// <summary>The GUID in its standard lower-case form, such as <c>0f8fad5b-d9cb-469f-a165-70867728950e</c>.</summary>
    /// <param name="id">The GUID.</param>
    public static string From(Guid id) => id.ToString("D");

    /// <summary>
    /// A name-based UUID (version 5, RFC 9562) for <paramref name="parts"/> joined with <c>:</c>, in its standard form.
    /// The same namespace and parts always give the same ID.
    /// </summary>
    /// <param name="namespaceId">A GUID that scopes the IDs, one per stream type.</param>
    /// <param name="parts">The natural key, such as a user ID and a title ID.</param>
    public static string Deterministic(Guid namespaceId, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return From(Uuid5(namespaceId, string.Join(':', parts)));
    }

    internal static Guid Uuid5(Guid namespaceId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

#pragma warning disable CA5350 // RFC 9562 defines version 5 with SHA-1; it is an identifier, not a security boundary.
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

internal static class Uuid7
{
#if NET9_0_OR_GREATER
    public static Guid New() => Guid.CreateVersion7();
#else
    public static Guid New()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)(ms >> (8 * (5 - i)));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
#endif
}
