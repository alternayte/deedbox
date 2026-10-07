using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>
/// Runs every async projection, subscription and rebuild catch-up, one loop each, plus the jobs loop.
/// Loops on several instances share the work: a loop only runs a batch while it holds its checkpoint row.
/// </summary>
internal sealed partial class AsyncRunner(DeedboxRuntime runtime, IServiceProvider services, ILogger<AsyncRunner> logger) : BackgroundService
{
    private readonly ILogger _logger = logger;
    private readonly WakeSignal _wake = new();

    public IReadOnlyList<ConsumerLoop> Loops { get; private set; } = [];

    public void Wake() => _wake.Set();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.Options.Runner.Enabled)
            return;

        var consumers = Consumers(runtime, services.GetRequiredService<ProjectionSet>());
        Loops = consumers.Select(c => new ConsumerLoop(c, runtime, services, _wake, _logger)).ToList();
        var jobs = new JobLoop(runtime, services, this, _wake, _logger);

        var tasks = Loops.Select(l => l.Run(stoppingToken)).Append(jobs.Run(stoppingToken)).Append(Listen(stoppingToken)).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // A driver often reports a command cancelled by the stop as its own exception type. That is a normal stop.
        }
    }

    public static List<Consumer> Consumers(DeedboxRuntime runtime, ProjectionSet set) =>
    [
        .. set.All.Select(p => (Consumer)new ProjectionConsumer(p, runtime.Registry.StoredNamesOf(p.Instance.HandledTypes), Handles.Of(runtime.Registry, p.Instance.HandledTypes))),
        .. set.Subscriptions.Select(s => (Consumer)new SubscriptionConsumer(s, runtime.Registry.StoredNamesOf(s.Instance.HandledTypes), Handles.Of(runtime.Registry, s.Instance.HandledTypes))),
    ];

    /// <summary>Wakes the loops on push notifications, and reconnects with backoff when the listener fails.</summary>
    private async Task Listen(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await runtime.Provider.Listen(_wake.Set, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogListenFailed(ex, delay);
                await Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            }
        }
    }

    internal static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "Deedbox lost its notification listener; polling continues, reconnecting in {Delay}.")]
    private partial void LogListenFailed(Exception exception, TimeSpan delay);
}

/// <summary>Releases every waiter at once; a waiter also wakes after its timeout.</summary>
internal sealed class WakeSignal
{
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set() => Interlocked.Exchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public async Task Wait(TimeSpan timeout, CancellationToken ct)
    {
        var signal = Volatile.Read(ref _signal).Task;
        try
        {
            await signal.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }
}

internal enum CutOverResult
{
    /// <summary>A live instance can still append the projection's events without running it.</summary>
    Waiting,

    /// <summary>The projection is inline again.</summary>
    Done,

    /// <summary>The hold on the position counter ran out; the events applied so far are kept.</summary>
    OutOfTime,
}

internal enum Outcome
{
    Progress,
    Idle,
    Failed,

    /// <summary>The database failed in passing; the batch runs again after a backoff and counts no attempt.</summary>
    Transient,
}

/// <summary>One consumer's loop: batch after batch while there is work, backoff while idle, retries then a stall on a poison event.</summary>
internal sealed partial class ConsumerLoop(Consumer consumer, DeedboxRuntime runtime, IServiceProvider services, WakeSignal wake, ILogger logger)
{
    private readonly ILogger _logger = logger;
    private long? _singleStepUntil;
    private long _failedPosition = -1;
    private int _attempts;
    private long _lastPosition;
    private bool _retryStalled = true;
    private bool _stepped;
    private readonly DateTimeOffset _startedAt = runtime.Clock.GetUtcNow();
    private string? _claimedRetry;
    private long _rebuildGap = long.MaxValue;
    private int _gapNotShrinking;
    private string _waitingFor = "";

    public Consumer Consumer => consumer;

    public long Lag { get; private set; }

    public double LagSeconds { get; private set; }

    public int StatusCode { get; private set; }

    private DeedboxProvider Provider => runtime.Provider;

    private RunnerOptions Options => runtime.Options.Runner;

    public async Task Run(CancellationToken ct)
    {
        DeedboxDiagnostics.Loops[this] = runtime.Provider.Schema;
        try
        {
            await Loop(ct);
        }
        finally
        {
            DeedboxDiagnostics.Loops.TryRemove(this, out _);
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        var delay = Options.MinPollDelay;
        while (!ct.IsCancellationRequested)
        {
            // Nothing leaves the loop while the host runs: a loop that ended would stop its consumer without a sign.
            try
            {
                switch (await Tick(ct))
                {
                    case Outcome.Progress:
                        delay = Options.MinPollDelay;
                        break;
                    case Outcome.Failed:
                        await AsyncRunner.Delay(RetryDelay(), ct);
                        break;
                    case Outcome.Transient:
                        await AsyncRunner.Delay(TransientDelay(), ct);
                        break;
                    default:
                        await wake.Wait(delay, ct);
                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.MaxPollDelay.Ticks));
                        break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogTickFailed(ex, consumer.Name);
                await wake.Wait(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.MaxPollDelay.Ticks));
            }
        }
    }

    public async Task<Outcome> Tick(CancellationToken ct)
    {
        await using var connection = Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var row = await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Batch, ct);
        if (row is not null)
        {
            StatusCode = row.Status switch { CheckpointStatus.Rebuilding => 1, CheckpointStatus.Stalled => 2, _ => 0 };

            // Only a stall that exists when this loop starts gets the immediate round of retries. Another instance may
            // hold the row during this loop's first ticks, so the stall's own time decides, not the first read.
            if (row.Status != CheckpointStatus.Stalled || !(StallAt(row.Error) < _startedAt))
                _retryStalled = false;
            if (_claimedRetry is not null && (row.Status != CheckpointStatus.Stalled || StallField(row.Error, "retryAt") != _claimedRetry))
                _claimedRetry = null;

            if (RetryDue(row))
            {
                // The claim commits before the attempt, so the instances make one attempt per interval between them.
                _claimedRetry = Stamp(runtime.Clock.GetUtcNow() + Options.MaxRetryDelay);
                var error = JsonNode.Parse(row.Error!)!.AsObject();
                error["attempts"] = Attempts(row.Error) + 1;
                error["retryAt"] = _claimedRetry;
                await Provider.UpdateCheckpoint(connection, transaction, row with { Error = error.ToJsonString() }, ct);
                await transaction.CommitAsync(ct);
                return Outcome.Progress;
            }
        }

        if (row is null || !ShouldRun(row))
            return Outcome.Idle;

        if (row.Position < _lastPosition)
            ResetFailureState(); // A rebuild moved the checkpoint back.
        _lastPosition = row.Position;
        _stepped = false;

        try
        {
            // An inline rebuild switches over once the rest fits in one batch. If appends outpace the catch-up so the gap
            // stops shrinking, it switches over anyway: appends then wait while the rest is applied under the counter lock.
            // It also waits while a live instance can append its events without running it: such an instance's appends
            // would skip it once it is inline. Meanwhile the catch-up goes on, so it stays current by position.
            // After a handler failure the loop steps one event at a time to find the failing event. It does not cut over
            // then: the cut-over would meet the same event under the counter lock, and a stall must name that event's position.
            if (consumer.IsInline && row.Status == CheckpointStatus.Rebuilding && _singleStepUntil is null && ShouldCutOver(await Provider.ReadHead(connection, transaction, ct) - row.Position)
                && await Skipping(connection, transaction, row, ct) is [])
            {
                var cutOver = await CutOver(connection, transaction, row, ct);
                if (cutOver == CutOverResult.Waiting)
                {
                    // The transaction holds the gate and the position counter now. It ends here, so no append waits
                    // while the next tick goes on with the catch-up.
                    await transaction.RollbackAsync(CancellationToken.None);
                    return Outcome.Progress;
                }

                await transaction.CommitAsync(ct);
                if (cutOver == CutOverResult.Done)
                {
                    (_forcedOutOfTime, _forceAgainAt) = (0, null);
                    LogRebuilt(consumer.Name);
                }
                else
                {
                    _forceAgainAt = runtime.Clock.GetUtcNow() + (Options.CutOverHold > Options.MinPollDelay ? Options.CutOverHold : Options.MinPollDelay);
                }

                return Outcome.Progress;
            }

            // A stalled consumer tries its one event first, so a retry touches nothing after it.
            var limit = _singleStepUntil is null && row.Status != CheckpointStatus.Stalled ? Options.BatchSize : 1;
            _stepped = limit == 1;
            var stored = await Provider.ReadEventsAfter(connection, transaction, row.Position, limit, consumer.PayloadTypes, ct);
            if (stored.Count == 0)
            {
                (Lag, LagSeconds) = (0, 0);
                if (row.Status != CheckpointStatus.Rebuilding || consumer.IsInline)
                    return Outcome.Idle;

                await Provider.UpdateCheckpoint(connection, transaction, row with { Status = CheckpointStatus.Running, Mode = consumer.Mode, Error = null }, ct);
                await transaction.CommitAsync(ct);
                LogRebuilt(consumer.Name);
                return Outcome.Progress;
            }

            using var activity = DeedboxDiagnostics.Source.StartActivity("deedbox.batch");
            activity?.SetTag("deedbox.consumer", consumer.Name);
            activity?.SetTag("deedbox.from_position", stored[0].GlobalPosition);
            activity?.SetTag("deedbox.to_position", stored[^1].GlobalPosition);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var envelopes = await Decode(connection, transaction, stored, ct);
            var last = stored[^1].GlobalPosition;
            if (consumer.Transactional)
            {
                await consumer.Process(envelopes, connection, transaction, services, Options.HandlerLimits, ct);
                await Provider.UpdateCheckpoint(connection, transaction, Advanced(row, last, stored.Count < limit), ct);
                await transaction.CommitAsync(ct);
            }
            else if (!await Deliver(connection, transaction, row, envelopes, last, ct))
            {
                // Another instance took the subscription between two events; it goes on from the checkpoint.
                return Outcome.Progress;
            }

            if (row.Status == CheckpointStatus.Rebuilding && !consumer.IsInline && stored.Count < limit)
                LogRebuilt(consumer.Name);
            if (row.Status == CheckpointStatus.Stalled)
            {
                _claimedRetry = null;
                LogResumed(consumer.Name, stored[0].GlobalPosition);
            }

            _lastPosition = last;
            _transientFailures = 0;
            _transientAt = -1;
            DeedboxDiagnostics.BatchDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
            if (stored.Count < limit)
            {
                (Lag, LagSeconds) = (0, 0);
            }
            else
            {
                await using var headConnection = Provider.CreateConnection();
                await headConnection.OpenAsync(ct);
                Lag = Math.Max(0, await Provider.ReadHead(headConnection, null, ct) - last);
                LagSeconds = Lag == 0 ? 0 : (runtime.Clock.GetUtcNow() - stored[^1].OccurredAt).TotalSeconds;
            }

            if (_singleStepUntil is { } until && last >= until)
                ResetFailureState();
            return Outcome.Progress;
        }
        catch (HandlerFailure failure) when (!ct.IsCancellationRequested)
        {
            // A projection's writes roll back with the batch. A subscription committed each event it delivered.
            await Abandon(connection, transaction);
            await OnFailure(failure, ct);
            return Outcome.Failed;
        }
        catch (TransientFailure failure) when (!ct.IsCancellationRequested)
        {
            // A failover or a lost connection is not a fault of the event, so it counts no attempt. An error that the
            // driver calls transient but that returns at one event for longer than StallAfter, such as a query that
            // always times out, is a fault of the event after all: it then fails like any other, so it can stall and
            // be skipped.
            await Abandon(connection, transaction);
            var now = runtime.Clock.GetUtcNow();
            if (_transientAt != failure.Envelope.GlobalPosition)
                (_transientAt, _transientSince) = (failure.Envelope.GlobalPosition, now);
            if (now - _transientSince >= Options.StallAfter)
            {
                await OnFailure(new HandlerFailure(failure.Envelope, failure.RetryUntil, failure.InnerException!), ct);
                return Outcome.Failed;
            }

            _transientFailures++;
            LogTransient(failure.InnerException, consumer.Name, TransientDelay());
            return Outcome.Transient;
        }
    }

    /// <summary>
    /// The checkpoint after a batch. A stalled consumer that got past its event, or an async rebuild that read to the
    /// end, is running again. An inline projection never finishes here: it stalled in catch-up and goes back to it, and
    /// only the cut-over, under the counter lock, may flip it to running.
    /// </summary>
    private CheckpointRow Advanced(CheckpointRow row, long position, bool readToEnd)
    {
        var status = row.Status switch
        {
            CheckpointStatus.Stalled => consumer.IsInline ? CheckpointStatus.Rebuilding : CheckpointStatus.Running,
            CheckpointStatus.Rebuilding when !consumer.IsInline && readToEnd => CheckpointStatus.Running,
            _ => row.Status,
        };
        return row with { Position = position, Status = status, Mode = consumer.Mode, Error = status == row.Status ? row.Error : null };
    }

    /// <summary>
    /// Delivers a subscription's events one at a time and commits the checkpoint after each. A side effect cannot be
    /// rolled back, so a crash must repeat one event, not a batch. Each commit releases the checkpoint row; the loop
    /// takes it again for the next event, and stops when another instance has it. Returns false in that case.
    /// </summary>
    private async Task<bool> Deliver(DbConnection connection, DbTransaction first, CheckpointRow row, List<EventEnvelope> envelopes, long last, CancellationToken ct)
    {
        var transaction = first;
        var current = row;
        try
        {
            for (var i = 0; ; i++)
            {
                if (i < envelopes.Count)
                    await consumer.Process([envelopes[i]], connection, transaction, services, Options.HandlerLimits, ct);

                // The scan may end with events of other types; the last commit moves past them too.
                var delivered = i >= envelopes.Count - 1;
                var position = delivered ? last : envelopes[i].GlobalPosition;
                await Provider.UpdateCheckpoint(connection, transaction, Advanced(current, position, false), ct);
                await transaction.CommitAsync(ct);
                if (delivered)
                    return true;

                if (!ReferenceEquals(transaction, first))
                    await transaction.DisposeAsync();
                transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var again = await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Batch, ct);
                if (again is not { Status: CheckpointStatus.Running } || again.Position != position)
                    return false;
                current = again;
            }
        }
        finally
        {
            if (!ReferenceEquals(transaction, first))
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Rolls the batch back, so the checkpoint row is free before the failure is recorded on it. A handler can leave the
    /// connection unable to roll back, such as with a reader it did not close. The connection is closed then, which ends
    /// the transaction on the server. No handler still runs on it: a projection's handler is never abandoned.
    /// </summary>
    private static async Task Abandon(DbConnection connection, DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
        {
        }

        try
        {
            await connection.CloseAsync();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
        {
        }
    }

    private int _transientFailures;
    private long _transientAt = -1;
    private DateTimeOffset _transientSince;

    // Never without a wait, even when RetryDelay is zero: the database is failing, and a hot loop makes that worse.
    private TimeSpan TransientDelay() => Backoff(Options.RetryDelay > Options.MinPollDelay ? Options.RetryDelay : Options.MinPollDelay, _transientFailures);

    private bool ShouldCutOver(long gap)
    {
        _gapNotShrinking = gap < _rebuildGap ? 0 : _gapNotShrinking + 1;
        _rebuildGap = Math.Min(_rebuildGap, gap);
        if (gap <= Options.BatchSize)
        {
            (_rebuildGap, _gapNotShrinking) = (long.MaxValue, 0);
            return true;
        }

        // A forced cut-over that ran out of time is tried again after appends had as long as it held them. The holds
        // and the catch-up between them then outrun the appends, unless the handlers are too slow at any rate. Without
        // this, each attempt would wait for the gap to stop shrinking again, and a busy store might never finish.
        var again = _forceAgainAt is { } at && runtime.Clock.GetUtcNow() >= at;
        if (!again && (_forceAgainAt is not null || _gapNotShrinking < 20))
            return false;

        LogForcedCutOver(consumer.Name, gap);
        (_rebuildGap, _gapNotShrinking) = (long.MaxValue, 0);
        return true;
    }

    private DateTimeOffset? _forceAgainAt;

    private bool ShouldRun(CheckpointRow row)
    {
        if (row.Status == CheckpointStatus.Retired)
            return false;
        // A checkpoint belongs to one run mode; a rebuild sets it. An instance that registers the projection in the
        // other mode leaves it alone, or the same events are applied inline and by position.
        if (row.Mode != consumer.Mode)
            return false;

        // An inline projection runs here only in catch-up, or to retry the poison event that its catch-up stalled on.
        if (consumer.IsInline && row.Status == CheckpointStatus.Running)
            return false;
        if (row.Status != CheckpointStatus.Stalled)
            return true;

        // A poison stall runs when this loop starts, in case the code was fixed, and for each retry this instance claims.
        return StallReason(row.Error) == "poison" && (_retryStalled || _claimedRetry is not null);
    }

    /// <summary>True when a poison stall's retry time has passed and this instance has no retry of its own pending.</summary>
    private bool RetryDue(CheckpointRow row) =>
        row.Status == CheckpointStatus.Stalled && !_retryStalled && _claimedRetry is null
        && StallReason(row.Error) == "poison"
        && (StallField(row.Error, "retryAt") is not { } at
            || DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= runtime.Clock.GetUtcNow());

    /// <summary>
    /// Ends an inline projection's rebuild. It takes the projection's gate exclusively, which waits for open appends
    /// that read the old status, then locks the counter, so no append commits while the last events are applied and
    /// the status flips back to running. Appends that follow see running and apply the projection inline again.
    /// </summary>
    internal async Task<CutOverResult> CutOver(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct)
    {
        await Provider.LockInlineGate(connection, transaction, consumer.Name, ct);
        var head = await Provider.LockCounter(connection, transaction, ct);
        var held = System.Diagnostics.Stopwatch.GetTimestamp();

        // Checked again under the locks: an instance that joined since then moved the projection back itself only
        // after this commits, so it must not be missed here.
        var instances = await Provider.ReadInstances(connection, transaction, Options.HeartbeatInterval * Instances.LiveIntervals, ct);
        var handles = consumer.Handles.With(Handles.FromJson(row.Handles));
        var skipping = instances.Where(i => Instances.Skips(i, consumer.Name, handles)).ToList();
        if (skipping.Any(Blocks))
            return CutOverResult.Waiting;

        // An instance whose heartbeat is late may only be paused. Its row goes, so its next append finds none and
        // fails instead of skipping the projection. The counter lock orders that append after this decision.
        if (skipping.Count > 0)
        {
            await Provider.EvictInstances(connection, transaction, [.. skipping.Select(i => i.Id)], ct);
            LogEvicted(consumer.Name, Instances.Describe(skipping));
        }

        // Every append in the store waits while this holds the counter, so the hold has a limit, checked between batches.
        // When the rest does not fit in it, the events applied so far are kept and the projection stays in catch-up,
        // with a smaller gap. At least one batch is applied, so each attempt moves on.
        var position = row.Position;
        while (position < head)
        {
            if (position > row.Position && System.Diagnostics.Stopwatch.GetElapsedTime(held) >= Options.CutOverHold)
            {
                await Provider.UpdateCheckpoint(connection, transaction, row with { Position = position, Error = SlowCatchUp(head - position) }, ct);
                return CutOverResult.OutOfTime;
            }

            var stored = await Provider.ReadEventsAfter(connection, transaction, position, Options.BatchSize, consumer.PayloadTypes, ct);
            if (stored.Count == 0)
                break;
            // A handler here runs while every append in the store waits, so its limit is the hold, not the handler
            // timeout. A handler that needs longer fails this cut-over and is applied by the catch-up instead.
            var limits = Options.HandlerLimits;
            var hold = TimeSpan.FromTicks(Math.Clamp(Options.CutOverHold.Ticks, TimeSpan.FromSeconds(1).Ticks, limits.Timeout.Ticks));
            await consumer.Process(await Decode(connection, transaction, stored, ct), connection, transaction, services, limits with { Timeout = hold }, ct);
            position = stored[^1].GlobalPosition;
        }

        await Provider.UpdateCheckpoint(connection, transaction,
            row with { Position = Math.Max(position, head), Status = CheckpointStatus.Running, Mode = consumer.Mode, Error = null }, ct);
        return CutOverResult.Done;
    }

    /// <summary>
    /// The note a catch-up keeps on its checkpoint after its forced cut-overs ran out of time several times in a row.
    /// The health check reports it as degraded: the handlers are too slow for the append rate.
    /// </summary>
    private string? SlowCatchUp(long gap)
    {
        _forcedOutOfTime++;
        if (_forcedOutOfTime < SlowAfterAttempts)
            return null;
        if (_forcedOutOfTime == SlowAfterAttempts)
            LogSlowCatchUp(consumer.Name, _forcedOutOfTime, gap);
        return new JsonObject { ["reason"] = SlowCatchUpReason, ["attempts"] = _forcedOutOfTime, ["gap"] = gap }.ToJsonString();
    }

    internal const string SlowCatchUpReason = "slow_catch_up";
    private const int SlowAfterAttempts = 5;
    private int _forcedOutOfTime;

    /// <summary>The live instances that can append this projection's events without running it inline.</summary>
    private async Task<List<InstanceRow>> Skipping(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct)
    {
        var instances = await Provider.ReadInstances(connection, transaction, Options.HeartbeatInterval * Instances.LiveIntervals, ct);
        var handles = consumer.Handles.With(Handles.FromJson(row.Handles));
        var skipping = instances.Where(i => Blocks(i) && Instances.Skips(i, consumer.Name, handles)).ToList();
        var waitingFor = Instances.Describe(skipping);
        if (skipping.Count > 0 && waitingFor != _waitingFor)
            LogWaitingForInstances(consumer.Name, waitingFor);
        _waitingFor = waitingFor;
        return skipping;
    }

    /// <summary>
    /// True when a cut-over must wait for the instance. A live instance always counts. One from a version that does not
    /// check its heartbeat row when it appends counts as long as it has a row at all: evicting it would not stop its
    /// appends, so only its row ageing out ends the wait.
    /// </summary>
    private static bool Blocks(InstanceRow instance) => instance.Live || instance.Formats < Crypto.Formats;

    private async Task<List<EventEnvelope>> Decode(DbConnection connection, DbTransaction transaction, List<StoredEvent> stored, CancellationToken ct)
    {
        List<DecodedEvent> decoded;
        try
        {
            decoded = await EventDecoding.Decode(runtime, connection, transaction, stored, ct);
        }
        catch (DecodeFailure failure)
        {
            // An event that cannot be decoded is poison like a failing handler: retried, then the consumer stalls on it.
            throw new HandlerFailure(Envelope(failure.Stored, failure.InnerException!, []), failure.Stored.GlobalPosition, failure.InnerException!);
        }

        return decoded.Select(d => Envelope(d.Stored, d.Event, d.ErasedSubjects)).ToList();
    }

    private EventEnvelope Envelope(StoredEvent e, object decoded, IReadOnlyList<string> erasedSubjects)
    {
        var registration = runtime.Registry.FindStoredName(e.EventType);
        return new EventEnvelope(e.EventId, e.TenantId, e.StreamId, e.StreamType, e.Version, e.GlobalPosition,
            registration?.Name ?? e.EventType, registration?.Version ?? e.EventVersion, decoded, EventMetadata.FromJson(e.Metadata ?? "{}"), e.OccurredAt, erasedSubjects);
    }

    private async Task OnFailure(HandlerFailure failure, CancellationToken ct)
    {
        var position = failure.Envelope.GlobalPosition;
        if (_claimedRetry is { } retryAt)
        {
            // A claimed retry of a stalled consumer is one attempt; the claim already set the next retry time.
            _claimedRetry = null;
            ResetFailureState();
            DeedboxDiagnostics.HandlerFailures.Add(1, DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
            var tried = await RecordStall(failure, stalled => (Attempts(stalled.Error), retryAt), ct);
            LogRetryFailed(failure.InnerException, consumer.Name, position, tried, retryAt);
            return;
        }

        // A stall is recorded only for an event that failed on its own, in a tick that read one event, so the
        // checkpoint is directly before it and a skip finds it. A failure in a batch says which event failed, but the
        // checkpoint is still before the whole batch. So a failure in a batch only starts the single steps.
        var alone = _stepped;
        _attempts = position == _failedPosition ? _attempts + 1 : 1;
        _failedPosition = position;
        _singleStepUntil = Math.Max(failure.RetryUntil, position);
        DeedboxDiagnostics.HandlerFailures.Add(1, DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
        LogHandlerFailed(failure.InnerException, consumer.Name, position, _attempts, Options.HandlerRetries + 1);

        if (_attempts <= Options.HandlerRetries || !alone)
            return;

        // A round after a restart adds to the attempts of the stall it retried.
        var round = _attempts;
        var attempts = await RecordStall(failure, row =>
            (round + (row.Status == CheckpointStatus.Stalled && StallField(row.Error, "eventId") == failure.Envelope.EventId.ToString("D") ? Attempts(row.Error) : 0),
             Stamp(runtime.Clock.GetUtcNow() + Options.MaxRetryDelay)), ct);
        if (attempts > 0)
        {
            DeedboxDiagnostics.Stalls.Add(1, DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
            LogStalled(consumer.Name, failure.Envelope.StreamId, failure.Envelope.Version, failure.Envelope.EventType, position, Options.MaxRetryDelay);
        }

        _retryStalled = false;
        ResetFailureState();
    }

    /// <summary>Writes the stall with the attempts and the next retry time; returns the attempts, or 0 when another runner holds the row.</summary>
    private async Task<int> RecordStall(HandlerFailure failure, Func<CheckpointRow, (int Attempts, string RetryAt)> schedule, CancellationToken ct)
    {
        await using var connection = Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Batch, ct);
        if (row is null)
            return 0;

        // A skip or a rebuild that waited for the row may have moved it since the handler failed. The stall belongs to
        // the checkpoint the failure was seen at, so a row that is past the event, or back before this tick, is left.
        if (row.Position >= failure.Envelope.GlobalPosition || row.Position < _lastPosition || row.Status == CheckpointStatus.Retired)
            return 0;

        var (attempts, retryAt) = schedule(row);
        await Provider.UpdateCheckpoint(connection, transaction,
            row with { Status = CheckpointStatus.Stalled, Error = PoisonError(failure, attempts, retryAt, runtime.Clock.GetUtcNow()) }, ct);
        await transaction.CommitAsync(ct);
        return attempts;
    }

    private TimeSpan RetryDelay() => Backoff(Options.RetryDelay, _attempts);

    /// <summary>The first delay doubled for each further failure, up to the longest retry delay.</summary>
    private TimeSpan Backoff(TimeSpan first, int failures)
    {
        var factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        return TimeSpan.FromTicks((long)Math.Min(first.Ticks * factor, Options.MaxRetryDelay.Ticks));
    }

    private static DateTimeOffset? StallAt(string? error) =>
        DateTimeOffset.TryParse(StallField(error, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static int Attempts(string? error) => int.TryParse(StallField(error, "attempts"), CultureInfo.InvariantCulture, out var n) ? n : 0;

    private void ResetFailureState()
    {
        _singleStepUntil = null;
        _failedPosition = -1;
        _attempts = 0;
    }

    internal static string? StallReason(string? error) => StallField(error, "reason");

    /// <summary>One field of a stall's JSON as text, or null.</summary>
    private static string? StallField(string? error, string name)
    {
        if (error is null)
            return null;
        try
        {
            return JsonNode.Parse(error)?[name]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string PoisonError(HandlerFailure failure, int attempts, string retryAt, DateTimeOffset now)
    {
        var e = failure.Envelope;
        var ex = failure.InnerException!;
        return new JsonObject
        {
            ["reason"] = "poison",
            ["eventId"] = e.EventId.ToString("D"),
            ["globalPosition"] = e.GlobalPosition,
            ["tenantId"] = e.TenantId,
            ["streamId"] = e.StreamId,
            ["version"] = e.Version,
            ["eventType"] = e.EventType,
            ["exception"] = ex.GetType().FullName,
            ["stack"] = Failure.Stack(ex),
            ["attempts"] = attempts,
            ["at"] = Stamp(now),
            ["retryAt"] = retryAt,
        }.ToJsonString();
    }

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Deedbox consumer '{Consumer}' failed a batch; retrying.")]
    private partial void LogTickFailed(Exception exception, string consumer);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Deedbox consumer '{Consumer}' failed on the event at position {Position} (attempt {Attempt} of {Attempts}).")]
    private partial void LogHandlerFailed(Exception? exception, string consumer, long position, int attempt, int attempts);

    [LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "Deedbox consumer '{Consumer}' met a transient database error; it runs the batch again in {Delay}. This counts no attempt against the event.")]
    private partial void LogTransient(Exception? exception, string consumer, TimeSpan delay);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Deedbox consumer '{Consumer}' stalled on stream '{StreamId}' version {Version} ({EventType}, position {Position}). It retries the event every {Interval}; fix the cause, or skip the event.")]
    private partial void LogStalled(string consumer, string streamId, long version, string eventType, long position, TimeSpan interval);

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Deedbox consumer '{Consumer}' is still stalled: attempt {Attempts} at the event at position {Position} failed. The next retry is at {RetryAt}.")]
    private partial void LogRetryFailed(Exception? exception, string consumer, long position, int attempts, string retryAt);

    [LoggerMessage(EventId = 28, Level = LogLevel.Information, Message = "Deedbox consumer '{Consumer}' got past the event at position {Position} that it stalled on, and runs again.")]
    private partial void LogResumed(string consumer, long position);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Deedbox projection '{Consumer}' is not catching up with appends; appends wait, for a limited time, while it applies the last {Gap} events.")]
    private partial void LogForcedCutOver(string consumer, long gap);

    [LoggerMessage(EventId = 44, Level = LogLevel.Warning, Message = "Deedbox projection '{Consumer}' went inline without {Instances}: their heartbeat was late. Their rows are removed, so their next append fails with DBX038 and they join again.")]
    private partial void LogEvicted(string consumer, string instances);

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "Deedbox projection '{Consumer}' cannot finish its catch-up: {Attempts} forced cut-overs in a row ran out of time, with {Gap} events left. Its handlers are too slow for the append rate; it stays in catch-up and keeps trying.")]
    private partial void LogSlowCatchUp(string consumer, int attempts, long gap);

    [LoggerMessage(EventId = 26, Level = LogLevel.Information, Message = "Deedbox projection '{Consumer}' stays in catch-up: {Instances} can append its events without running it inline.")]
    private partial void LogWaitingForInstances(string consumer, string instances);

        [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Deedbox projection '{Consumer}' finished rebuilding.")]
    private partial void LogRebuilt(string consumer);
}
