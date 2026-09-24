using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>
/// Runs when the host starts: checks or applies the schema, then checks that every stored event name maps
/// to a registered event, so a rename fails here instead of on the first read.
/// </summary>
internal sealed partial class DeedboxStartup(DeedboxRuntime runtime, IServiceProvider services, ILogger<DeedboxStartup> logger) : IHostedService
{
    private readonly ILogger _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Creating the projections checks that each handles only registered events.
        _ = services.GetRequiredService<ProjectionSet>();

        if (runtime.Options.ApplySchemaOnStartup)
        {
            var (from, to) = await SchemaManager.Apply(runtime.Provider, cancellationToken);
            if (from != to)
                LogApplied(runtime.Provider.Schema, from, to);
        }
        else
        {
            await SchemaManager.Verify(runtime.Provider, cancellationToken);
        }

        await StoredNames.Verify(runtime, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Deedbox schema '{Schema}' migrated from version {From} to {To}.")]
    private partial void LogApplied(string schema, int from, int to);
}

internal static class StoredNames
{
    /// <summary>Checks every stored event type against the registry, and remembers the ones already recorded.</summary>
    public static async Task Verify(DeedboxRuntime runtime, CancellationToken ct)
    {
        List<EventTypeRow> stored;
        await using (var connection = runtime.Provider.CreateConnection())
        {
            await connection.OpenAsync(ct);
            stored = await runtime.Provider.ReadEventTypes(connection, ct);
        }

        Check(runtime.Registry, stored);
        foreach (var row in stored)
            runtime.KnownEventTypes.TryAdd(row, true);
    }

    public static void Check(EventRegistry registry, IReadOnlyList<EventTypeRow> stored)
    {
        var problems = new List<(string Code, string Message)>();
        var storedNames = stored.Select(r => r.EventType).ToHashSet(StringComparer.Ordinal);

        foreach (var row in stored)
        {
            var registration = registry.FindStoredName(row.EventType);
            if (registration is null)
            {
                problems.Add((Errors.UnmappedStoredEvent, $"Stored events '{row.EventType}' v{row.EventVersion} have no mapping. {RenameHint(registry, row, storedNames)}"));
            }
            else if (!string.Equals(registration.Stream.Name, row.StreamType, StringComparison.Ordinal))
            {
                problems.Add((Errors.StoredStreamTypeMismatch,
                    $"Stored events '{row.EventType}' belong to stream type '{row.StreamType}', but {registration.ClrType.Name} is registered under '{registration.Stream.Name}'. Register it in the '{row.StreamType}' stream."));
            }
            else if (row.EventVersion > registration.Version)
            {
                problems.Add((Errors.StoredVersionAhead, EventRegistry.VersionAhead(row.EventType, row.EventVersion, registration).Message));
            }
        }

        if (problems.Count == 1)
            throw new DeedboxException(problems[0].Code, problems[0].Message);
        if (problems.Count > 1)
        {
            throw new DeedboxException(problems[0].Code,
                $"{problems.Count} stored event types do not match the registrations:{Environment.NewLine}" +
                string.Join(Environment.NewLine, problems.Select(p => $"- {p.Code}: {p.Message}")) + Environment.NewLine);
        }
    }

    /// <summary>Names the likely renamed class: the one event of that stream type with no stored rows under any of its names.</summary>
    private static string RenameHint(EventRegistry registry, EventTypeRow row, HashSet<string> storedNames)
    {
        var candidates = registry.Streams
            .Where(s => string.Equals(s.Name, row.StreamType, StringComparison.Ordinal))
            .SelectMany(s => s.Events)
            .Where(e => !e.Aliases.Prepend(e.Name).Any(storedNames.Contains))
            .ToList();

        return candidates.Count == 1
            ? $"Did you rename {candidates[0].ClrType.Name}? Add .Alias(\"{row.EventType}\") to it."
            : $"If you renamed its event class, add .Alias(\"{row.EventType}\") to that event's registration.";
    }
}
