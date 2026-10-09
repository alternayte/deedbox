using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox;

/// <summary>A handler threw on one event. The runner retries from that event, then stalls the consumer.</summary>
internal sealed class HandlerFailure(EventEnvelope envelope, long retryUntil, Exception inner)
    : Exception($"Handling event {envelope.EventId} at position {envelope.GlobalPosition} failed.", inner)
{
    public EventEnvelope Envelope { get; } = envelope;

    /// <summary>The runner reads one event at a time until it passes this position, to pin down the failing event.</summary>
    public long RetryUntil { get; } = retryUntil;
}

/// <summary>
/// The database failed in passing while a handler ran: a failover, a lost connection, a deadlock victim. The runner
/// runs the batch again after a backoff. It is not a fault of the event, so it counts no attempt toward a stall.
/// </summary>
internal sealed class TransientFailure(EventEnvelope envelope, long retryUntil, Exception inner) : Exception("A transient database error interrupted a handler.", inner)
{
    public EventEnvelope Envelope { get; } = envelope;

    public long RetryUntil { get; } = retryUntil;
}

/// <summary>The handler timeout, and how long a handler gets to stop after the timeout cancelled its token.</summary>
internal readonly record struct HandlerLimits(TimeSpan Timeout, TimeSpan Grace);

/// <summary>Something the runner feeds committed events to: an async or rebuilding projection, or a subscription.</summary>
internal abstract class Consumer(string name, string mode, IReadOnlyList<string> payloadTypes, Handles handles)
{
    public string Name { get; } = name;

    /// <summary>The events it handles, stored with its checkpoint for instances that do not run it.</summary>
    public Handles Handles { get; } = handles;

    public string Mode { get; } = mode;

    /// <summary>The stored event names this consumer reads payloads for.</summary>
    public IReadOnlyList<string> PayloadTypes { get; } = payloadTypes;

    public bool IsInline => Mode == CheckpointMode.Inline;

    /// <summary>True when a new checkpoint starts at the head instead of the first event.</summary>
    public virtual bool StartsAtHead => false;

    /// <summary>True when the handlers write in the batch transaction, so a failure rolls the whole batch back.</summary>
    public abstract bool Transactional { get; }

    /// <summary>Handles the batch's events. Throws <see cref="HandlerFailure"/> naming the failing event.</summary>
    public abstract Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, HandlerLimits limits, CancellationToken ct);

    /// <summary>
    /// Runs one handler call with the handler timeout. The handler gets a token that the timeout cancels. The call
    /// starts on the thread pool, so a handler that blocks before its first await is bounded too.
    /// </summary>
    /// <remarks>
    /// A subscription's handler has nothing of the batch: after the timeout it gets a short time to stop, and is then
    /// abandoned, so it cannot hold the checkpoint row for good. A projection's handler writes on the batch's connection.
    /// Nothing can be done safely with a connection that another task still uses, so the runner waits for that handler
    /// after it cancelled the token. A projection handler must pass its token to what it awaits. When the host stops,
    /// no handler is abandoned.
    /// </remarks>
    protected static async Task Bounded(Func<CancellationToken, Task> handler, HandlerLimits limits, bool abandon, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(limits.Timeout);
        var token = limit.Token;
        var call = Task.Run(() => handler(token), CancellationToken.None);
        try
        {
            await call.WaitAsync(token);
            return;
        }
        catch (OperationCanceledException) when (!call.IsCompleted)
        {
        }

        // The limit passed, or the host stops. Only a subscription's handler may be left behind, and only on a timeout.
        if (abandon && !ct.IsCancellationRequested)
            await Task.WhenAny(call, Task.Delay(limits.Grace, ct));
        if (!abandon || ct.IsCancellationRequested)
            await Settled(call);
        ct.ThrowIfCancellationRequested();

        if (call.IsCompletedSuccessfully)
            return; // It finished while the limit passed; its work is done.

        var stopped = call.IsCompleted;
        if (stopped)
            _ = call.Exception;
        else
            _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        throw new TimeoutException(stopped
            ? $"The handler did not finish in {limits.Timeout}, the runner's HandlerTimeout. It was cancelled and counts as a failed attempt."
            : $"The handler did not finish in {limits.Timeout}, the runner's HandlerTimeout, and did not stop when its token was cancelled. It was abandoned and counts as a failed attempt.");

        // Waits for the task to end, whatever way it ends.
        static Task Settled(Task task) => task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The failure of one handler call: transient when the database says so, or when the batch's connection broke.</summary>
    internal static Exception Failure(Exception ex, DbConnection connection, EventEnvelope envelope, long retryUntil)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException { IsTransient: true })
                return new TransientFailure(envelope, retryUntil, ex);
        }

        return connection.State != System.Data.ConnectionState.Open ? new TransientFailure(envelope, retryUntil, ex) : new HandlerFailure(envelope, retryUntil, ex);
    }

    /// <summary>
    /// Fails the event when its handler returned but the batch's transaction is over. A handler can catch an error with
    /// which the database rolled the whole transaction back. The writes of the events before it are gone then, and on
    /// SQL Server every later statement, the checkpoint update too, would commit on its own.
    /// </summary>
    protected static void RequireTransaction(DbTransaction transaction, EventEnvelope envelope, long retryUntil)
    {
        if (transaction.Connection is null)
        {
            throw new HandlerFailure(envelope, retryUntil, new InvalidOperationException(
                "The handler returned, but the database had rolled the batch's transaction back. A handler must not catch an error that ends the transaction."));
        }
    }

    protected static Activity? StartActivity(string consumer, EventEnvelope envelope)
    {
        ActivityContext.TryParse(envelope.Metadata.TraceParent, null, out var parent);
        var activity = DeedboxDiagnostics.Source.StartActivity("deedbox.handle " + consumer, ActivityKind.Consumer, parent);
        activity?.SetTag("deedbox.consumer", consumer);
        activity?.SetTag("deedbox.event_type", envelope.EventType);
        activity?.SetTag("deedbox.global_position", envelope.GlobalPosition);
        return activity;
    }
}

internal sealed class ProjectionConsumer(RegisteredProjection projection, IReadOnlyList<string> payloadTypes, Handles handles)
    : Consumer(projection.Name, projection.Run == Run.Inline ? CheckpointMode.Inline : CheckpointMode.Async, payloadTypes, handles)
{
    public ProjectionBase Instance => projection.Instance;

    public override bool Transactional => true;

    public override async Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, HandlerLimits limits, CancellationToken ct)
    {
        if (events.Count == 0)
            return;

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DeedboxContext>();
        await using var work = new TransactionWork(connection, transaction, scope.ServiceProvider, [], ct);

        if (projection.Instance.IsBatch)
        {
            try
            {
                await Bounded(async token =>
                {
                    work.CancellationToken = token;
                    await projection.Instance.ApplyBatch(events, work);
                    await work.RunBeforeCounter();
                }, limits, abandon: false, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw Failure(ex, connection, events[0], events[^1].GlobalPosition);
            }

            RequireTransaction(transaction, events[0], events[^1].GlobalPosition);
            return;
        }

        foreach (var envelope in events)
        {
            context.CausedBy(envelope);
            using var activity = StartActivity(Name, envelope);
            try
            {
                await Bounded(token =>
                {
                    work.CancellationToken = token;
                    return projection.Instance.Handle(envelope.Event, Invocation(work, envelope));
                }, limits, abandon: false, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                throw Failure(ex, connection, envelope, envelope.GlobalPosition);
            }

            RequireTransaction(transaction, envelope, envelope.GlobalPosition);
        }

        try
        {
            await Bounded(token =>
            {
                work.CancellationToken = token;
                return work.RunBeforeCounter();
            }, limits, abandon: false, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw Failure(ex, connection, events[0], events[^1].GlobalPosition);
        }
    }

    private static ProjectionInvocation Invocation(TransactionWork work, EventEnvelope envelope) => new(work)
    {
        EventId = envelope.EventId,
        TenantId = envelope.TenantId,
        StreamId = envelope.StreamId,
        StreamType = envelope.StreamType,
        Version = envelope.Version,
        GlobalPosition = envelope.GlobalPosition,
        Metadata = envelope.Metadata,
        OccurredAt = envelope.OccurredAt,
    };
}

internal sealed class SubscriptionConsumer(RegisteredSubscription subscription, IReadOnlyList<string> payloadTypes, Handles handles)
    : Consumer(subscription.Name, CheckpointMode.Subscription, payloadTypes, handles)
{
    public override bool Transactional => false;

    public override bool StartsAtHead => subscription.Start == SubscriptionStart.Now;

    public override async Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, HandlerLimits limits, CancellationToken ct)
    {
        foreach (var envelope in events)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DeedboxContext>().CausedBy(envelope);
            using var activity = StartActivity(Name, envelope);
            try
            {
                await Bounded(token => subscription.Instance.Handle(envelope.Event, new SubscriptionContext(envelope, scope.ServiceProvider, token)), limits, abandon: true, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                throw Failure(ex, connection, envelope, envelope.GlobalPosition);
            }
        }
    }
}
