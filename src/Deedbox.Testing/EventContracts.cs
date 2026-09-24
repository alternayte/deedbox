using System.Runtime.CompilerServices;
using Deedbox.Testing.Contracts;

namespace Deedbox.Testing;

/// <summary>
/// Checks registered event contracts against a committed lockfile, so a rename without an alias, a removed
/// event, or an incompatible shape change without an upcaster fails a test instead of production reads.
/// </summary>
public static class EventContracts
{
    /// <summary>
    /// Compares the registrations with the lockfile. A breaking change throws. A compatible change, such as a
    /// new event, a new alias, a new version or a new nullable property, rewrites the lockfile, except under CI
    /// (the <c>CI</c> environment variable is <c>true</c>), where any difference throws. A missing lockfile is
    /// written and the check throws once, so the new file gets reviewed and committed.
    /// </summary>
    /// <param name="configure">The same configuration the app passes to <c>AddDeedbox</c>. No database is used.</param>
    /// <param name="lockfile">The lockfile path, relative to the calling source file's folder.</param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <exception cref="EventContractException">A breaking change, or an out-of-date lockfile under CI.</exception>
    public static void Verify(Action<DeedboxBuilder> configure, string lockfile = "events.lock", [CallerFilePath] string callerFile = "")
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(lockfile);

        var path = Path.IsPathRooted(lockfile) ? lockfile : Path.Combine(CallerDirectory(callerFile), lockfile);
        var onCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        Verify(configure, path, onCi);
    }

    /// <summary>
    /// The calling source file's folder. Deterministic CI builds record source paths under a mapped root such as
    /// <c>/_/</c>; the folder is then found by walking up from the test's working directory.
    /// </summary>
    internal static string CallerDirectory(string callerFile, string? searchFrom = null)
    {
        var directory = Path.GetDirectoryName(callerFile) ?? "";
        if (Directory.Exists(directory))
            return directory;

        var relative = directory.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("_/", StringComparison.Ordinal))
            relative = relative[2..];
        for (var root = new DirectoryInfo(searchFrom ?? Directory.GetCurrentDirectory()); root is not null; root = root.Parent)
        {
            var candidate = Path.Combine(root.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new EventContractException($"Cannot find the folder of {callerFile}. Pass an absolute lockfile path to EventContracts.Verify.");
    }

    internal static void Verify(Action<DeedboxBuilder> configure, string path, bool onCi)
    {
        var builder = new DeedboxBuilder();
        configure(builder);
        var (registry, json) = builder.BuildRegistry();
        var current = Lockfile.From(registry, json);
        var text = current.ToString();

        if (!File.Exists(path))
        {
            if (!onCi)
                File.WriteAllText(path, text);
            throw new EventContractException(onCi
                ? $"No event contract lockfile at {path}. Run the tests locally to create it, then commit it."
                : $"Created the event contract lockfile at {path}. Review it and commit it; the check passes from the next run.");
        }

        var existing = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var breaks = Compatibility.Breaks(Lockfile.Parse(existing), current);
        if (breaks.Count > 0)
        {
            throw new EventContractException(
                $"Event contracts break stored events ({path}):{Environment.NewLine}" +
                string.Join(Environment.NewLine, breaks.Select(b => "- " + b)));
        }

        if (existing == text)
            return;
        if (onCi)
            throw new EventContractException($"The event contract lockfile {path} is out of date. Run the tests locally and commit the updated file.");
        File.WriteAllText(path, text);
    }
}
