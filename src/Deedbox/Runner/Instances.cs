using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deedbox;

/// <summary>
/// One live app instance, as its heartbeat row records it: what it runs, and what it can append. Event types are
/// written as <c>streamType/eventType</c>, with current stored names.
/// </summary>
internal sealed record InstanceRow(
    Guid Id, string Host, string App, IReadOnlyList<string> Consumers, IReadOnlyList<string> Inline, IReadOnlyList<string> Events, DateTimeOffset SeenAt,
    bool Live = true, int Formats = Crypto.Formats);

/// <summary>
/// The events a projection handles, stored with its checkpoint so that an instance without the projection's code can
/// tell whether its own appends would skip it. Events are <c>streamType/eventType</c>, current names and aliases.
/// </summary>
internal sealed record Handles(IReadOnlyList<string> Events, IReadOnlyList<string> BuiltIns)
{
    public static Handles Of(EventRegistry registry, IEnumerable<Type> types)
    {
        var registrations = types.Select(registry.ForEvent).ToList();
        return new Handles(
            [.. registrations.Where(e => !e.IsBuiltIn).SelectMany(e => e.Aliases.Prepend(e.Name).Select(n => $"{e.Stream!.Name}/{n}")).Distinct(StringComparer.Ordinal)],
            [.. registrations.Where(e => e.IsBuiltIn).Select(e => e.Name)]);
    }

    /// <summary>
    /// Both sets together. A checkpoint records every event that any version of its projection handled since its last
    /// rebuild, so an instance that knows only the stored set never judges itself safe too early. The set only grows:
    /// a version that handles less must not hide what a newer version handles.
    /// </summary>
    public Handles With(Handles? other) => other is null ? this : new Handles(
        [.. Events.Union(other.Events, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        [.. BuiltIns.Union(other.BuiltIns, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    public bool Covers(Handles? other) => other is null
        || (other.Events.All(e => Events.Contains(e, StringComparer.Ordinal)) && other.BuiltIns.All(b => BuiltIns.Contains(b, StringComparer.Ordinal)));

    public string ToJson() => JsonSerializer.Serialize(this, InstanceJson.Default.Handles);

    public static Handles? FromJson(string? json) => json is null ? null : JsonSerializer.Deserialize(json, InstanceJson.Default.Handles);
}

internal static class Instances
{
    /// <summary>An instance counts as live while its heartbeat is younger than this many intervals.</summary>
    public const int LiveIntervals = 3;

    /// <summary>The liveness window the CLI uses; apps derive theirs from <see cref="RunnerOptions.HeartbeatInterval"/>.</summary>
    public static readonly TimeSpan DefaultLiveFor = TimeSpan.FromSeconds(30);

    public static InstanceRow Self(DeedboxRuntime runtime, ProjectionSet projections, string app) => new(
        runtime.InstanceId,
        Environment.MachineName,
        app,
        [.. projections.All.Select(p => p.Name).Concat(projections.Subscriptions.Select(s => s.Name))],
        [.. projections.Inline.Select(p => p.Name)],
        [.. runtime.Registry.Streams.SelectMany(s => s.Events.Select(e => $"{s.Name}/{e.Name}"))],
        DateTimeOffset.MinValue);

    /// <summary>
    /// True when <paramref name="instance"/> can append an event that <paramref name="projection"/> handles, but does not
    /// run the projection inline, so its appends would skip it. Built-in events go on streams of the instance's own
    /// stream types, so they count only where those types meet the projection's.
    /// </summary>
    public static bool Skips(InstanceRow instance, string projection, Handles handles)
    {
        if (instance.Inline.Contains(projection, StringComparer.Ordinal))
            return false;
        if (handles.Events.Intersect(instance.Events, StringComparer.Ordinal).Any())
            return true;
        if (handles.BuiltIns.Count == 0)
            return false;

        var projectionStreams = handles.Events.Select(StreamOf).ToHashSet(StringComparer.Ordinal);
        var instanceStreams = instance.Events.Select(StreamOf).ToList();
        return projectionStreams.Count == 0 ? instanceStreams.Count > 0 : instanceStreams.Any(projectionStreams.Contains);
    }

    public static string Describe(IEnumerable<InstanceRow> instances) =>
        string.Join(", ", instances.Select(i => $"{i.App} on {i.Host} ({i.Id:D})"));

    public static string ListJson(IReadOnlyList<string> names) => JsonSerializer.Serialize(names.ToArray(), InstanceJson.Default.StringArray);

    public static IReadOnlyList<string> ReadList(string json) => JsonSerializer.Deserialize(json, InstanceJson.Default.StringArray) ?? [];

    public static string StreamOf(string pair) => pair[..pair.IndexOf('/', StringComparison.Ordinal)];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Handles))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class InstanceJson : JsonSerializerContext;

/// <summary>An append found no heartbeat row for this instance: a cut-over evicted it, or it never joined.</summary>
internal sealed class InstanceEvicted(bool replayable) : Exception("This instance has no heartbeat row.")
{
    /// <summary>True when Deedbox can run the whole write again after the instance joined.</summary>
    public bool Replayable { get; } = replayable;
}

/// <summary>
/// This instance's place among the instances of the store. An instance appends only while it has a heartbeat row, and
/// it gets one only together with the move that makes its appends safe: every inline projection that it would skip
/// goes back to catch-up first. A cut-over deletes the row of an instance that is not live, so an instance that was
/// paused past the liveness window cannot append past a projection that went inline without it.
/// </summary>
internal sealed partial class Membership(DeedboxRuntime runtime, IServiceProvider services)
{
    private const int JoinAttempts = 3;
    private readonly ILogger _logger = services.GetService<ILoggerFactory>()?.CreateLogger<Membership>() ?? NullLogger<Membership>.Instance;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _joined;
    private volatile bool _everJoined;
    private bool _checkpoints;
    private InstanceRow? _self;

    public InstanceRow Self => _self ??= Instances.Self(runtime, services.GetRequiredService<ProjectionSet>(),
        services.GetService<IHostEnvironment>()?.ApplicationName ?? "app");

    public TimeSpan LiveFor => runtime.Options.Runner.HeartbeatInterval * Instances.LiveIntervals;

    /// <summary>
    /// Runs before a write. An instance that never joined, such as a process that appends without a started host, joins
    /// here. After an eviction only a write in a transaction that Deedbox owns waits for the join: a caller's transaction
    /// can hold the position counter, which the join needs, so that write fails with DBX038 until the join is done.
    /// </summary>
    public ValueTask Ensure(bool owned, CancellationToken ct)
    {
        if (!_joined)
            return !owned && _everJoined ? ValueTask.CompletedTask : new ValueTask(Join(ct));

        // A process with no started host has no heartbeat loop. Its writes refresh the row instead, so it is not
        // evicted over and over while it works.
        return runtime.Clock.GetUtcNow() - new DateTimeOffset(Volatile.Read(ref _beatAt), TimeSpan.Zero) < runtime.Options.Runner.HeartbeatInterval * 2
            ? ValueTask.CompletedTask
            : new ValueTask(Refresh(owned, ct));
    }

    private long _beatAt;

    private async Task Refresh(bool owned, CancellationToken ct)
    {
        if (await BeatOnce(ct))
            return;

        // The row is gone. A caller's transaction may hold the position counter, which the join needs, so only a
        // write in a transaction of Deedbox's own waits for the join.
        if (owned)
            await Join(ct);
        else
            Evicted();
    }

    /// <summary>
    /// Writes this instance's heartbeat row and, in the same transaction, moves each running inline projection that this
    /// instance would skip back to catch-up at the head. It holds those projections' gates and the position counter, as
    /// a cut-over does. So every append up to the head applied the projection inline, the runner applies what follows by
    /// position, and no cut-over can decide without seeing this instance.
    /// </summary>
    public async Task Join(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_joined)
                return;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await JoinOnce(ct);

                    // The first join also creates this instance's checkpoints. A process that appends without a
                    // started host comes here too, so an inline projection never runs without a checkpoint row.
                    if (!_checkpoints)
                    {
                        await new CheckpointSetup(runtime, services, _logger).Ensure(Self, LiveFor, ct);
                        _checkpoints = true;
                    }

                    break;
                }
                catch (DbException) when (attempt < JoinAttempts && !ct.IsCancellationRequested)
                {
                    // The database chose this transaction to end a lock cycle with a rebuild or a cut-over.
                }
            }

            _joined = true;
            _everJoined = true;
            Volatile.Write(ref _beatAt, runtime.Clock.GetUtcNow().UtcTicks);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task JoinOnce(CancellationToken ct)
    {
        var provider = runtime.Provider;
        var self = Self;
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);

        // A projection that this instance registers in another run mode is not moved: start-up stalls it as mode_changed.
        bool Skipped(CheckpointRow row) => row.Mode == CheckpointMode.Inline && row.Status != CheckpointStatus.Retired
            && !self.Consumers.Contains(row.Name, StringComparer.Ordinal)
            && Handles.FromJson(row.Handles) is { } handles && Instances.Skips(self, row.Name, handles);

        // The gates come before the counter, as in an append, so the projections to gate are chosen before any lock.
        // An inline checkpoint is only created, and only made running, under the counter. So the choice is checked
        // again under the counter, and when a projection appeared meanwhile the join starts over with its gate too.
        var gated = (await provider.ReadCheckpoints(connection, ct)).Where(Skipped).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        for (var round = 0; ; round++)
        {
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            foreach (var name in provider.GateOrder(gated))
                await provider.LockInlineGate(connection, transaction, name, ct);
            var head = await provider.LockCounter(connection, transaction, ct);

            // The rows are read, not locked: a catch-up batch holds its row while it waits for the gate. Under the
            // gates and the counter, no rebuild, cut-over or other join changes what is read here.
            var skipped = (await provider.ReadCheckpoints(connection, transaction, ct)).Where(Skipped).ToList();
            if (skipped.Any(r => !gated.Contains(r.Name)) && round < 5)
            {
                gated.UnionWith(skipped.Select(r => r.Name));
                await transaction.RollbackAsync(CancellationToken.None);
                continue;
            }

            await provider.WriteInstance(connection, transaction, self, ct);
            var moved = new List<string>();
            foreach (var row in skipped.Where(r => r.Status == CheckpointStatus.Running && gated.Contains(r.Name)))
            {
                await provider.UpdateCheckpoint(connection, transaction, row with { Position = head, Status = CheckpointStatus.Rebuilding, Error = null }, ct);
                moved.Add(row.Name);
            }

            if (skipped.Any(r => r.Status == CheckpointStatus.Running && !gated.Contains(r.Name)))
                throw new InvalidOperationException("Inline projections kept appearing while this instance joined the store.");

            await transaction.CommitAsync(CancellationToken.None);
            foreach (var name in moved)
                LogCatchingUp(name, head);
            break;
        }

        await ReadFormats(connection, ct);
    }

    /// <summary>
    /// Sets the storage format that appends write to the lowest one among the heartbeat rows. An older version that
    /// still runs during a rolling deploy cannot read a newer format, so nobody writes it until that version is gone.
    /// </summary>
    private async Task ReadFormats(DbConnection connection, CancellationToken ct)
    {
        var instances = await runtime.Provider.ReadInstances(connection, null, LiveFor, ct);
        runtime.UseFormats(instances.Select(i => i.Formats).DefaultIfEmpty(Crypto.Formats).Min());
    }

    private int _rejoining;

    /// <summary>An append found no heartbeat row. The instance joins again in the background, one join at a time.</summary>
    public void Evicted()
    {
        if (_joined)
            LogEvicted();
        _joined = false;
        if (Interlocked.Exchange(ref _rejoining, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Join(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The next heartbeat or the next evicted write tries again.
                LogJoinFailed(ex);
            }
            finally
            {
                Volatile.Write(ref _rejoining, 0);
            }
        });
    }

    /// <summary>Refreshes the heartbeat. A row that is gone means an eviction, so the instance joins again.</summary>
    public async Task Beat(CancellationToken ct)
    {
        if (!await BeatOnce(ct))
            await Join(ct);
    }

    private async Task<bool> BeatOnce(CancellationToken ct)
    {
        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        var (found, formats) = await runtime.Provider.Beat(connection, Self, LiveFor, ct);
        if (found)
        {
            runtime.UseFormats(formats);
            Volatile.Write(ref _beatAt, runtime.Clock.GetUtcNow().UtcTicks);
        }
        else
        {
            _joined = false;
        }

        return found;
    }

    public async Task Leave(CancellationToken ct)
    {
        _joined = false;
        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await runtime.Provider.Leave(connection, runtime.InstanceId, ct);
    }

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Deedbox projection '{Name}' runs inline elsewhere, but this instance appends its events without running it. It catches up from position {Head} until no such instance is live.")]
    private partial void LogCatchingUp(string name, long head);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "Deedbox did not count this instance as live, because its heartbeat was late, and refused an append that would have skipped an inline projection. The instance joins again.")]
    private partial void LogEvicted();

    [LoggerMessage(EventId = 43, Level = LogLevel.Error, Message = "Deedbox could not join the store's instances again; it retries. Appends fail with DBX038 until it does.")]
    private partial void LogJoinFailed(Exception ex);
}

/// <summary>
/// Keeps this instance's heartbeat row current, and deletes it on a clean stop. Start-up joins first, before anything
/// else looks at the other instances.
/// </summary>
internal sealed partial class InstanceHeartbeat(DeedboxRuntime runtime, Membership membership, ILogger<InstanceHeartbeat> logger) : BackgroundService
{
    private readonly ILogger _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await AsyncRunner.Delay(runtime.Options.Runner.HeartbeatInterval, stoppingToken);
            try
            {
                await membership.Beat(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogBeatFailed(ex);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await membership.Leave(cancellationToken);
        }
        catch (Exception ex)
        {
            // The row ages out instead.
            LogLeaveFailed(ex);
        }
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Deedbox could not write this instance's heartbeat; it retries.")]
    private partial void LogBeatFailed(Exception ex);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Deedbox could not remove this instance's heartbeat; it ages out.")]
    private partial void LogLeaveFailed(Exception ex);
}
