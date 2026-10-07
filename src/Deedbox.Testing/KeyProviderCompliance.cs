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
        var version = wrapped.KeyVersion;

        Check(!string.IsNullOrWhiteSpace(version) && version.Contains(':', StringComparison.Ordinal),
            $"The key version '{version}' must name the provider and the key, such as env:v1.");
        Check(!wrapped.Bytes.AsSpan().SequenceEqual(key), "WrapAsync returned the key unwrapped.");
        Check((await provider.UnwrapAsync(wrapped.Bytes, version, ct)).AsSpan().SequenceEqual(key), "UnwrapAsync did not return the wrapped key.");

        // Wraps that overlap must each unwrap with the version their own call returned.
        var overlapping = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.WrapAsync(key, ct)));
        foreach (var again in overlapping)
        {
            Check((await provider.UnwrapAsync(again.Bytes, again.KeyVersion, ct)).AsSpan().SequenceEqual(key),
                "A wrap that overlapped another did not unwrap with the key version it returned.");
        }

        var altered = (byte[])wrapped.Bytes.Clone();
        altered[altered.Length / 2] ^= 0x01;
        await CheckThrows(() => provider.UnwrapAsync(altered, version, ct), "UnwrapAsync accepted altered bytes. It must verify what it unwraps.");
        await CheckThrows(() => provider.UnwrapAsync(wrapped.Bytes, version + "-unknown", ct), "UnwrapAsync accepted an unknown key version.");
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
