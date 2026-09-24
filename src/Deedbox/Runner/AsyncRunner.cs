using System.Data;
using System.Data.Common;
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
        await Task.WhenAll(tasks);
    }

    public static List<Consumer> Consumers(DeedboxRuntime runtime, ProjectionSet set) =>
    [
        .. set.All.Select(p => (Consumer)new ProjectionConsumer(p, runtime.Registry.StoredNamesOf(p.Instance.HandledTypes))),
        .. set.Subscriptions.Select(s => (Consumer)new SubscriptionConsumer(s, runtime.Registry.StoredNamesOf(s.Instance.HandledTypes))),
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

internal enum Outcome
{
    Progress,
    Idle,
    Failed,
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
    private long _rebuildGap = long.MaxValue;
    private int _gapNotShrinking;

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
            Outcome outcome;
            try
            {
                outcome = await Tick(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogTickFailed(ex, consumer.Name);
                outcome = Outcome.Idle;
            }

            switch (outcome)
            {
                case Outcome.Progress:
                    delay = Options.MinPollDelay;
                    break;
                case Outcome.Failed:
                    await AsyncRunner.Delay(RetryDelay(), ct);
                    break;
                default:
                    await wake.Wait(delay, ct);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.MaxPollDelay.Ticks));
                    break;
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
        }

        if (row is null || !ShouldRun(row))
            return Outcome.Idle;

        if (row.Position < _lastPosition)
            ResetFailureState(); // A rebuild moved the checkpoint back.
        _lastPosition = row.Position;

        try
        {
            // An inline rebuild switches over once the rest fits in one batch. If appends outpace the catch-up so the gap
            // stops shrinking, it switches over anyway: appends then wait while the rest is applied under the counter lock.
            if (consumer.IsInline && ShouldCutOver(await Provider.ReadHead(connection, transaction, ct) - row.Position))
            {
                await CutOver(connection, transaction, row, ct);
                await transaction.CommitAsync(ct);
                LogRebuilt(consumer.Name);
                return Outcome.Progress;
            }

            var limit = _singleStepUntil is null ? Options.BatchSize : 1;
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

            await consumer.Process(await Decode(connection, transaction, stored, ct), connection, transaction, services, ct);

            var last = stored[^1].GlobalPosition;

            // A stalled consumer that got past its event, or an async rebuild that read to the end, is running again.
            // An inline rebuild never finishes here: only the cut-over, under the counter lock, may flip it back.
            var status = row.Status == CheckpointStatus.Stalled || (row.Status == CheckpointStatus.Rebuilding && !consumer.IsInline && stored.Count < limit)
                ? CheckpointStatus.Running
                : row.Status;
            await Provider.UpdateCheckpoint(connection, transaction, row with { Position = last, Status = status, Mode = consumer.Mode, Error = status == row.Status ? row.Error : null }, ct);
            await transaction.CommitAsync(ct);
            if (row.Status == CheckpointStatus.Rebuilding && status == CheckpointStatus.Running)
                LogRebuilt(consumer.Name);

            _lastPosition = last;
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
            // A subscription keeps what it delivered before the failure; a projection's writes roll back with the batch.
            if (!consumer.Transactional && failure.Envelope.GlobalPosition - 1 > row.Position)
            {
                await Provider.UpdateCheckpoint(connection, transaction, row with { Position = failure.Envelope.GlobalPosition - 1 }, ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            await OnFailure(failure, ct);
            return Outcome.Failed;
        }
    }

    private bool ShouldCutOver(long gap)
    {
        _gapNotShrinking = gap < _rebuildGap ? 0 : _gapNotShrinking + 1;
        _rebuildGap = Math.Min(_rebuildGap, gap);
        if (gap > Options.BatchSize && _gapNotShrinking < 20)
            return false;

        if (gap > Options.BatchSize)
            LogForcedCutOver(consumer.Name, gap);
        _rebuildGap = long.MaxValue;
        _gapNotShrinking = 0;
        return true;
    }

    private bool ShouldRun(CheckpointRow row)
    {
        if (consumer.IsInline)
            return row.Status == CheckpointStatus.Rebuilding;
        if (row.Status != CheckpointStatus.Stalled)
            return true;

        // After a restart, a poison stall gets one more round of retries, in case the code was fixed.
        return _retryStalled && StallReason(row.Error) == "poison";
    }

    /// <summary>
    /// Ends an inline projection's rebuild. It takes the projection's gate exclusively, which waits for open appends
    /// that read the old status, then locks the counter, so no append commits while the last events are applied and
    /// the status flips back to running. Appends that follow see running and apply the projection inline again.
    /// </summary>
    private async Task CutOver(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct)
    {
        await Provider.LockInlineGate(connection, transaction, consumer.Name, ct);
        var head = await Provider.LockCounter(connection, transaction, ct);
        var position = row.Position;
        while (position < head)
        {
            var stored = await Provider.ReadEventsAfter(connection, transaction, position, Options.BatchSize, consumer.PayloadTypes, ct);
            if (stored.Count == 0)
                break;
            await consumer.Process(await Decode(connection, transaction, stored, ct), connection, transaction, services, ct);
            position = stored[^1].GlobalPosition;
        }

        await Provider.UpdateCheckpoint(connection, transaction,
            row with { Position = Math.Max(position, head), Status = CheckpointStatus.Running, Mode = consumer.Mode, Error = null }, ct);
    }

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
        _attempts = position == _failedPosition ? _attempts + 1 : 1;
        _failedPosition = position;
        _singleStepUntil = Math.Max(failure.RetryUntil, position);
        DeedboxDiagnostics.HandlerFailures.Add(1, DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
        LogHandlerFailed(failure.InnerException, consumer.Name, position, _attempts, Options.HandlerRetries + 1);

        if (_attempts <= Options.HandlerRetries)
            return;

        await using var connection = Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await Provider.LockCheckpoint(connection, transaction, consumer.Name, CheckpointLock.Batch, ct);
        if (row is not null)
        {
            await Provider.UpdateCheckpoint(connection, transaction, row with { Status = CheckpointStatus.Stalled, Error = PoisonError(failure, _attempts) }, ct);
            await transaction.CommitAsync(ct);
            DeedboxDiagnostics.Stalls.Add(1, DeedboxDiagnostics.Tag("deedbox.consumer", consumer.Name));
            LogStalled(consumer.Name, failure.Envelope.StreamId, failure.Envelope.Version, failure.Envelope.EventType, position);
        }

        _retryStalled = false;
        ResetFailureState();
    }

    private TimeSpan RetryDelay()
    {
        var factor = Math.Pow(2, Math.Max(0, _attempts - 1));
        return TimeSpan.FromTicks((long)Math.Min(Options.RetryDelay.Ticks * factor, TimeSpan.FromMinutes(5).Ticks));
    }

    private void ResetFailureState()
    {
        _singleStepUntil = null;
        _failedPosition = -1;
        _attempts = 0;
    }

    internal static string? StallReason(string? error)
    {
        if (error is null)
            return null;
        try
        {
            return JsonNode.Parse(error)?["reason"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string PoisonError(HandlerFailure failure, int attempts)
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
            ["message"] = ex.Message,
            ["stackTrace"] = ex.ToString(),
            ["attempts"] = attempts,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }.ToJsonString();
    }

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Deedbox consumer '{Consumer}' failed a batch; retrying.")]
    private partial void LogTickFailed(Exception exception, string consumer);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Deedbox consumer '{Consumer}' failed on the event at position {Position} (attempt {Attempt} of {Attempts}).")]
    private partial void LogHandlerFailed(Exception? exception, string consumer, long position, int attempt, int attempts);

    [LoggerMessage(EventId = 23, Level = LogLevel.Error, Message = "Deedbox consumer '{Consumer}' stalled on stream '{StreamId}' version {Version} ({EventType}, position {Position}). Fix the handler and restart, or skip the event.")]
    private partial void LogStalled(string consumer, string streamId, long version, string eventType, long position);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Deedbox projection '{Consumer}' is not catching up with appends; appends wait while it applies the last {Gap} events.")]
    private partial void LogForcedCutOver(string consumer, long gap);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Deedbox projection '{Consumer}' finished rebuilding.")]
    private partial void LogRebuilt(string consumer);
}
