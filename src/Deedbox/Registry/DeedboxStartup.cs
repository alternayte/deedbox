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
        var projections = services.GetRequiredService<ProjectionSet>();

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
        if (runtime.Keys is { } keys)
        {
            await keys.LoadAll(cancellationToken);
            if (keys.Master is DatabaseMasterKey)
                LogDatabaseMasterKey();
        }
        await EnsureCheckpoints(projections, cancellationToken);
    }

    /// <summary>
    /// Adds a checkpoint row for each new projection and subscription. A projection whose run mode changed stalls
    /// until it is rebuilt: its stored progress belongs to the other mode, so running it either way could skip or
    /// repeat events.
    /// </summary>
    private async Task EnsureCheckpoints(ProjectionSet projections, CancellationToken ct)
    {
        var wanted = AsyncRunner.Consumers(runtime, projections).Select(c => (c.Name, c.Mode)).ToList();
        if (wanted.Count == 0)
            return;

        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await runtime.Provider.EnsureCheckpoints(connection, wanted, ct);

        var stored = (await runtime.Provider.ReadCheckpoints(connection, ct)).ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var (name, mode) in wanted)
        {
            var row = stored[name];
            if (row.Mode == mode || row.Status == CheckpointStatus.Rebuilding || ConsumerLoop.StallReason(row.Error) == "mode_changed")
                continue;

            await using var transaction = await connection.BeginTransactionAsync(ct);
            if (mode == CheckpointMode.Inline || row.Mode == CheckpointMode.Inline)
                await runtime.Provider.LockInlineGate(connection, transaction, name, ct);
            var locked = await runtime.Provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct);
            var error = new System.Text.Json.Nodes.JsonObject { ["reason"] = "mode_changed", ["from"] = row.Mode, ["to"] = mode }.ToJsonString();
            await runtime.Provider.UpdateCheckpoint(connection, transaction, locked! with { Status = CheckpointStatus.Stalled, Error = error }, ct);
            await transaction.CommitAsync(ct);
            LogModeChanged(name, row.Mode, mode);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Deedbox keeps its master key in the database. Erasure works, but a database copy or backup exposes personal data. Move the key out with deedbox keys rewrap.")]
    private partial void LogDatabaseMasterKey();

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Deedbox projection '{Name}' changed from {From} to {To}; it is stalled until you rebuild it.")]
    private partial void LogModeChanged(string name, string from, string to);

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
            else if (registration.Stream is { } stream && !string.Equals(stream.Name, row.StreamType, StringComparison.Ordinal))
            {
                problems.Add((Errors.StoredStreamTypeMismatch,
                    $"Stored events '{row.EventType}' belong to stream type '{row.StreamType}', but {registration.ClrType.Name} is registered under '{stream.Name}'. Register it in the '{row.StreamType}' stream."));
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
