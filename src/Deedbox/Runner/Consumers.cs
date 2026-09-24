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

/// <summary>Something the runner feeds committed events to: an async or rebuilding projection, or a subscription.</summary>
internal abstract class Consumer(string name, string mode, IReadOnlyList<string> payloadTypes)
{
    public string Name { get; } = name;

    public string Mode { get; } = mode;

    /// <summary>The stored event names this consumer reads payloads for.</summary>
    public IReadOnlyList<string> PayloadTypes { get; } = payloadTypes;

    public bool IsInline => Mode == CheckpointMode.Inline;

    /// <summary>True when the handlers write in the batch transaction, so a failure rolls the whole batch back.</summary>
    public abstract bool Transactional { get; }

    /// <summary>Handles the batch's events. Throws <see cref="HandlerFailure"/> naming the failing event.</summary>
    public abstract Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, CancellationToken ct);

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

internal sealed class ProjectionConsumer(RegisteredProjection projection, IReadOnlyList<string> payloadTypes)
    : Consumer(projection.Name, projection.Run == Run.Inline ? CheckpointMode.Inline : CheckpointMode.Async, payloadTypes)
{
    public ProjectionBase Instance => projection.Instance;

    public override bool Transactional => true;

    public override async Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, CancellationToken ct)
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
                await projection.Instance.ApplyBatch(events, work);
                await work.RunBeforeCounter();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new HandlerFailure(events[0], events[^1].GlobalPosition, ex);
            }

            return;
        }

        foreach (var envelope in events)
        {
            context.CausedBy(envelope);
            using var activity = StartActivity(Name, envelope);
            try
            {
                await projection.Instance.Handle(envelope.Event, Invocation(work, envelope));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw new HandlerFailure(envelope, envelope.GlobalPosition, ex);
            }
        }

        try
        {
            await work.RunBeforeCounter();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new HandlerFailure(events[0], events[^1].GlobalPosition, ex);
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

internal sealed class SubscriptionConsumer(RegisteredSubscription subscription, IReadOnlyList<string> payloadTypes)
    : Consumer(subscription.Name, CheckpointMode.Subscription, payloadTypes)
{
    public override bool Transactional => false;

    public override async Task Process(IReadOnlyList<EventEnvelope> events, DbConnection connection, DbTransaction transaction, IServiceProvider services, CancellationToken ct)
    {
        foreach (var envelope in events)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DeedboxContext>().CausedBy(envelope);
            using var activity = StartActivity(Name, envelope);
            try
            {
                await subscription.Instance.Handle(envelope.Event, new SubscriptionContext(envelope, scope.ServiceProvider, ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw new HandlerFailure(envelope, envelope.GlobalPosition, ex);
            }
        }
    }
}

internal static class DeedboxDiagnostics
{
    public static readonly ActivitySource Source = new("Deedbox");
}
