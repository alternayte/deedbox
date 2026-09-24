using System.Security.Cryptography;

namespace Deedbox.Testing;

/// <summary>
/// The checks every <see cref="IMasterKeyProvider"/> must pass: a wrapped key unwraps to the same bytes, altered
/// bytes and unknown versions fail loudly, and the key version names the provider. Run it against each provider.
/// </summary>
public static class KeyProviderCompliance
{
    /// <summary>Runs every check. A failure throws <see cref="KeyProviderComplianceException"/> naming the check.</summary>
    /// <param name="provider">The provider under test.</param>
    /// <param name="ct">Cancels the checks.</param>
    public static async Task VerifyAsync(IMasterKeyProvider provider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = await provider.WrapAsync(key, ct);
        var version = provider.KeyVersion;

        Check(!string.IsNullOrWhiteSpace(version) && version.Contains(':', StringComparison.Ordinal),
            $"KeyVersion '{version}' must name the provider and the key, such as env:v1.");
        Check(!wrapped.AsSpan().SequenceEqual(key), "WrapAsync returned the key unwrapped.");
        Check((await provider.UnwrapAsync(wrapped, version, ct)).AsSpan().SequenceEqual(key), "UnwrapAsync did not return the wrapped key.");

        var again = await provider.WrapAsync(key, ct);
        Check((await provider.UnwrapAsync(again, provider.KeyVersion, ct)).AsSpan().SequenceEqual(key), "A second wrap of the same key did not unwrap.");

        var altered = (byte[])wrapped.Clone();
        altered[altered.Length / 2] ^= 0x01;
        await CheckThrows(() => provider.UnwrapAsync(altered, version, ct), "UnwrapAsync accepted altered bytes. It must verify what it unwraps.");
        await CheckThrows(() => provider.UnwrapAsync(wrapped, version + "-unknown", ct), "UnwrapAsync accepted an unknown key version.");
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
            throw new KeyProviderComplianceException(failure);
    }

    private static async Task CheckThrows(Func<Task> action, string failure)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // Any exception passes: the provider refused.
        catch (Exception ex) when (ex is not KeyProviderComplianceException)
#pragma warning restore CA1031
        {
            return;
        }

        throw new KeyProviderComplianceException(failure);
    }
}

/// <summary>A master key provider failed a compliance check.</summary>
public sealed class KeyProviderComplianceException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The check that failed.</param>
    public KeyProviderComplianceException(string message)
        : base(message)
    {
    }
}
