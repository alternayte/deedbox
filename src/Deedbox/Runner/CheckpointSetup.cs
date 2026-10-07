using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>
/// Creates the checkpoints of the projections and subscriptions that this instance registers, when it joins the store.
/// </summary>
internal sealed partial class CheckpointSetup(DeedboxRuntime runtime, IServiceProvider services, ILogger logger)
{
    private const int Attempts = 3;
    private readonly ILogger _logger = logger;

    private DeedboxProvider Provider => runtime.Provider;

    public async Task Ensure(InstanceRow self, TimeSpan liveFor, CancellationToken ct)
    {
        var consumers = AsyncRunner.Consumers(runtime, services.GetRequiredService<ProjectionSet>());
        if (consumers.Count == 0)
            return;

        await using var connection = Provider.CreateConnection();
        await connection.OpenAsync(ct);
        var known = (await Provider.ReadCheckpoints(connection, ct)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var missing = consumers.Where(c => !known.Contains(c.Name)).ToList();
        if (missing.Count > 0)
            await Create(connection, missing, liveFor, ct);

        var stored = (await Provider.ReadCheckpoints(connection, ct)).ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var consumer in consumers.Where(c => !Covered(c, stored[c.Name])))
            await Widen(consumer, liveFor, ct);

        foreach (var consumer in consumers)
            await StallOnModeChange(connection, consumer, stored[consumer.Name], ct);

        static bool Covered(Consumer consumer, CheckpointRow row) =>
            Handles.FromJson(row.Handles) is { } handles && handles.Covers(consumer.Handles);
    }

    /// <summary>
    /// Creates the missing checkpoints under the position counter. A join holds the counter too, so each instance either
    /// has its heartbeat row before this reads the instances, or joins afterwards and sees the new checkpoints. A new
    /// inline projection starts in catch-up while any instance with a row can append its events without running it; a
    /// row that is not live counts, because only a cut-over evicts it.
    /// </summary>
    private async Task Create(System.Data.Common.DbConnection connection, List<Consumer> missing, TimeSpan liveFor, CancellationToken ct)
    {
        long head;
        await using (var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct))
        {
            head = await Provider.LockCounter(connection, transaction, ct);
            var instances = await Provider.ReadInstances(connection, transaction, liveFor, ct);
            var seeds = missing.Select(c => new CheckpointSeed(c.Name, c.Mode, c.Handles.ToJson(),
                c.IsInline && instances.Any(i => Instances.Skips(i, c.Name, c.Handles)), c.StartsAtHead)).ToList();
            await Provider.EnsureCheckpoints(connection, transaction, seeds, ct);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // A new subscription that starts at the first event runs its side effects for every stored event. That is the
        // default, because it skips nothing, so the operator is told before the replay runs far.
        var created = (await Provider.ReadCheckpoints(connection, ct)).ToDictionary(r => r.Name, StringComparer.Ordinal);
        foreach (var consumer in missing.Where(c => c.Mode == CheckpointMode.Subscription && !c.StartsAtHead))
        {
            if (head > 0 && created[consumer.Name].Position == 0)
                LogSubscriptionReplay(consumer.Name, head);
        }
    }

    /// <summary>
    /// Records that this version of a projection handles more than its checkpoint knew. For a running inline projection
    /// the new events are a new exposure: an instance that appends one of them without running the projection would
    /// skip it. So under the gate and the counter, as in a join, the projection goes back to catch-up at the head when
    /// such an instance has a heartbeat row.
    /// </summary>
    private async Task Widen(Consumer consumer, TimeSpan liveFor, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = Provider.CreateConnection();
                await connection.OpenAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
                await Provider.LockInlineGate(connection, transaction, consumer.Name, ct);
                var row = (await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Exclusive, ct))!;
                var handles = consumer.Handles.With(Handles.FromJson(row.Handles));
                var head = await Provider.LockCounter(connection, transaction, ct);
                await Provider.UpdateHandles(connection, transaction, consumer.Name, handles.ToJson(), ct);

                var moved = false;
                if (row is { Mode: CheckpointMode.Inline, Status: CheckpointStatus.Running })
                {
                    var instances = await Provider.ReadInstances(connection, transaction, liveFor, ct);
                    if (instances.Any(i => Instances.Skips(i, consumer.Name, handles)))
                    {
                        await Provider.UpdateCheckpoint(connection, transaction, row with { Position = head, Status = CheckpointStatus.Rebuilding, Error = null }, ct);
                        moved = true;
                    }
                }

                await transaction.CommitAsync(CancellationToken.None);
                if (moved)
                    LogWidened(consumer.Name, head);
                return;
            }
            catch (System.Data.Common.DbException) when (attempt < Attempts && !ct.IsCancellationRequested)
            {
                // The database ended a lock cycle with a catch-up batch, which holds the row and waits for the gate.
            }
        }
    }

    /// <summary>
    /// A projection whose run mode changed stalls until it is rebuilt: its stored progress belongs to the other mode, so
    /// running it either way could skip or repeat events. A checkpoint in catch-up is left alone: a rebuild set its mode,
    /// and an instance with the other mode does not run it.
    /// </summary>
    private async Task StallOnModeChange(System.Data.Common.DbConnection connection, Consumer consumer, CheckpointRow row, CancellationToken ct)
    {
        if (row.Mode == consumer.Mode || row.Status is CheckpointStatus.Rebuilding or CheckpointStatus.Retired || ConsumerLoop.StallReason(row.Error) == "mode_changed")
            return;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (consumer.Mode == CheckpointMode.Inline || row.Mode == CheckpointMode.Inline)
            await Provider.LockInlineGate(connection, transaction, consumer.Name, ct);
        var locked = await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Exclusive, ct);
        var error = new System.Text.Json.Nodes.JsonObject { ["reason"] = "mode_changed", ["from"] = row.Mode, ["to"] = consumer.Mode }.ToJsonString();
        await Provider.UpdateCheckpoint(connection, transaction, locked! with { Status = CheckpointStatus.Stalled, Error = error }, ct);
        await transaction.CommitAsync(ct);
        LogModeChanged(consumer.Name, row.Mode, consumer.Mode);
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Deedbox projection '{Name}' changed from {From} to {To}; it is stalled until you rebuild it.")]
    private partial void LogModeChanged(string name, string from, string to);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Deedbox subscription '{Name}' is new and starts at the first event, so it handles the {Events} events the store already holds. If its side effects must not run for them, stop the app now, register the subscription under a new name with SubscriptionStart.Now, and retire this name.")]
    private partial void LogSubscriptionReplay(string name, long events);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Deedbox projection '{Name}' now handles events that another instance appends without running it. It catches up from position {Head} until no such instance has a heartbeat.")]
    private partial void LogWidened(string name, long head);
}
