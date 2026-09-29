using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Deedbox;

/// <summary>
/// Turns a real identity, such as <c>github:alice</c>, into a stable pseudonymous subject ID, such as
/// <c>person:k7q2m9x4…</c>, and erases a person by identity. The identity is never stored, logged or traced.
/// Each tenant and period has its own secret, wrapped by the master key; reads and rebuilds never need it.
/// </summary>
public interface IPseudonyms
{
    /// <summary>
    /// The subject ID of <paramref name="identity"/> in <paramref name="periodId"/>, in the scope's tenant: the
    /// configured prefix plus 26 base32 characters of a keyed hash. The same identity and period give the same ID on
    /// every instance. The period's secret is created on first use.
    /// </summary>
    /// <param name="identity">
    /// A canonical, namespaced identity, such as <c>email:alice@example.com</c> or <c>github:alice</c>. Deedbox does
    /// not normalize it: normalize case and aliases before the call.
    /// </param>
    /// <param name="periodId">
    /// The period, such as <c>2026-Q3</c> from <see cref="PseudonymPeriod.Quarter"/>. Pass one fixed ID for subject
    /// IDs that never change.
    /// </param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">
    /// DBX036 when the period's secret was destroyed; DBX037 when the period was created with another prefix.
    /// </exception>
    Task<string> SubjectForAsync(string identity, string periodId, CancellationToken ct = default);

    /// <summary>
    /// Erases <paramref name="identity"/> in the scope's tenant: computes its subject ID in every period whose secret
    /// still exists, deletes those subjects' keys at once, and queues one erasure job per period.
    /// </summary>
    /// <param name="identity">The identity, as passed to <see cref="SubjectForAsync"/>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The erasure job IDs, one per period, in period order.</returns>
    Task<IReadOnlyList<Guid>> EraseIdentityAsync(string identity, CancellationToken ct = default);
}

/// <summary>Builds calendar period IDs for <see cref="IPseudonyms.SubjectForAsync"/>, in UTC.</summary>
public static partial class PseudonymPeriod
{
    internal const int MaxLength = 64;

    /// <summary>The UTC quarter of <paramref name="at"/>, such as <c>2026-Q3</c>.</summary>
    /// <param name="at">The time, such as the time of the write.</param>
    public static string Quarter(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{utc.Year:D4}-Q{(utc.Month - 1) / 3 + 1}");
    }

    /// <summary>The UTC month of <paramref name="at"/>, such as <c>2026-09</c>.</summary>
    /// <param name="at">The time, such as the time of the write.</param>
    public static string Month(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{utc.Year:D4}-{utc.Month:D2}");
    }

    internal static string Validate(string periodId)
    {
        ArgumentNullException.ThrowIfNull(periodId);
        if (!Pattern().IsMatch(periodId))
        {
            throw new ArgumentException(
                $"Period ID '{periodId}' is not valid. Use 1 to {MaxLength} letters, digits, '.', '_', ':' or '-', starting with a letter or digit, such as 2026-Q3.",
                nameof(periodId));
        }

        return periodId;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// Computes subject IDs from identities. Each call reads the period's row, so a destroy or a shred on another
/// instance applies at the next call. The unwrapped secret is kept only while the row's wrapped bytes are unchanged.
/// Nothing here stores, logs or throws with the identity.
/// </summary>
internal sealed class Pseudonymizer(DeedboxProvider provider, IMasterKeyProvider master)
{
    public const string DefaultPrefix = "person:";

    /// <summary>A subject ID is at most 100 characters; the token takes 26.</summary>
    public const int MaxPrefixLength = 74;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly ConcurrentDictionary<(string TenantId, string PeriodId), (byte[] Wrapped, byte[] Secret)> _secrets = new();

    public async Task<string> SubjectFor(string tenantId, string identity, string periodId, string prefix, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        var row = await provider.ReadPseudonymKey(connection, null, tenantId, periodId, ct);
        if (row is null)
        {
            // Created in its own committed transaction: a rolled-back append must never leave a secret in memory only.
            await provider.InsertPseudonymKey(connection, null,
                new PseudonymKeyRow(tenantId, periodId, prefix, await master.WrapAsync(Crypto.NewKey(), ct), master.KeyVersion), ct);
            row = await provider.ReadPseudonymKey(connection, null, tenantId, periodId, ct)
                ?? throw new InvalidOperationException($"The pseudonym secret of period '{periodId}' in tenant '{tenantId}' vanished while it was created.");
        }

        if (row.Destroyed)
        {
            _secrets.TryRemove((tenantId, periodId), out _);
            throw new DeedboxException(Errors.PseudonymPeriodDestroyed,
                $"The pseudonym secret of period '{periodId}' in tenant '{tenantId}' was destroyed, so no subject ID can be computed for that period again. Use a current period.");
        }

        if (row.Prefix != prefix)
        {
            throw new DeedboxException(Errors.PseudonymPrefixChanged,
                $"Period '{periodId}' in tenant '{tenantId}' was created with prefix '{row.Prefix}', but the app sets '{prefix}'. " +
                "A period keeps its prefix, so each person keeps one subject ID in it. Set the prefix back, or change it when a new period starts.");
        }

        return row.Prefix + Token(await Secret(row, ct), identity);
    }

    /// <summary>The identity's subject ID in every period of the tenant whose secret still exists, in period order.</summary>
    public async Task<List<string>> SubjectsInEveryPeriod(string tenantId, string identity, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        var subjects = new List<string>();
        foreach (var row in await provider.ReadPseudonymKeys(connection, null, tenantId, ct))
        {
            if (!row.Destroyed)
                subjects.Add(row.Prefix + Token(await Secret(row, ct), identity));
        }

        return subjects;
    }

    /// <summary>Drops a period's secret from memory, after this instance destroyed it.</summary>
    public void Forget(string tenantId, string periodId) => _secrets.TryRemove((tenantId, periodId), out _);

    /// <summary>The first 128 bits of HMAC-SHA256(secret, UTF-8 identity), as 26 lower-case base32 characters.</summary>
    public static string Token(byte[] secret, string identity)
    {
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(identity), mac);
        return Base32(mac[..16]);
    }

    /// <summary>RFC 4648 base32 in lower case, without padding.</summary>
    public static string Base32(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[(bytes.Length * 8 + 4) / 5];
        int buffer = 0, bits = 0, i = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[i++] = Alphabet[(buffer >> bits) & 31];
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
            chars[i] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(chars);
    }

    /// <summary>Rejects an identity Deedbox would hash differently from what the caller meant. The message never holds it.</summary>
    public static void ValidateIdentity(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Length == 0 || char.IsWhiteSpace(identity[0]) || char.IsWhiteSpace(identity[^1]))
        {
            throw new ArgumentException(
                "The identity is empty or has leading or trailing white space. Pass a canonical identity, such as email:alice@example.com; Deedbox does not normalize identities.",
                nameof(identity));
        }
    }

    public static string ValidatePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Length > MaxPrefixLength || prefix.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"Pseudonym prefix '{prefix}' is not valid. Use at most {MaxPrefixLength} characters with no white space, such as person:.", nameof(prefix));
        }

        return prefix;
    }

    private async Task<byte[]> Secret(PseudonymKeyRow row, CancellationToken ct)
    {
        var key = (row.TenantId, row.PeriodId);
        if (_secrets.TryGetValue(key, out var cached) && cached.Wrapped.AsSpan().SequenceEqual(row.WrappedKey))
            return cached.Secret;

        byte[] secret;
        try
        {
            secret = await master.UnwrapAsync(row.WrappedKey, row.WrappedBy, ct);
        }
        catch (Exception ex) when (ex is CryptographicException or DeedboxException)
        {
            throw new DeedboxException(Errors.MasterKeyUnusable,
                $"The master key ({master.KeyVersion}) cannot unwrap the pseudonym secret of period '{row.PeriodId}' in tenant '{row.TenantId}', which {row.WrappedBy} wrapped. " +
                "Configure the master key that wrapped it, or add its version to the key ring.", ex);
        }

        _secrets[key] = (row.WrappedKey, secret);
        return secret;
    }
}

internal sealed class Pseudonyms(DeedboxRuntime runtime, DeedboxContext context) : IPseudonyms
{
    public Task<string> SubjectForAsync(string identity, string periodId, CancellationToken ct = default)
    {
        Pseudonymizer.ValidateIdentity(identity);
        PseudonymPeriod.Validate(periodId);
        var tenantId = DeedboxContext.ValidTenant(context.TenantId);
        return runtime.RequirePseudonyms().SubjectFor(tenantId, identity, periodId, runtime.Options.PseudonymPrefix, ct);
    }

    public Task<IReadOnlyList<Guid>> EraseIdentityAsync(string identity, CancellationToken ct = default)
    {
        Pseudonymizer.ValidateIdentity(identity);
        var tenantId = DeedboxContext.ValidTenant(context.TenantId);
        return Admin.EraseIdentity(runtime.Provider, runtime.RequirePseudonyms(), runtime.Clock, tenantId, identity, ct);
    }
}
