namespace Deedbox;

/// <summary>
/// An async projection that receives each batch of its events in one call, for heavy workloads such as bulk
/// inserts. Declare the event types with <see cref="Handles{TEvent}"/> in the constructor.
/// </summary>
public abstract class BatchProjection : ProjectionBase
{
    /// <summary>Creates the projection.</summary>
#pragma warning disable RS0022 // ProjectionBase has no protected members to expose.
    protected BatchProjection()
#pragma warning restore RS0022
    {
    }

    internal override bool IsBatch => true;

    /// <summary>Adds an event type to the batches.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    protected void Handles<TEvent>() where TEvent : notnull => AddHandler(typeof(TEvent), static (_, _) => Task.CompletedTask);

    /// <summary>Applies one batch through the context's connection and transaction, which also commits the checkpoint.</summary>
    /// <param name="events">The batch's events of the declared types, in position order.</param>
    /// <param name="context">The connection, transaction and scope.</param>
    protected abstract Task ApplyAsync(IReadOnlyList<EventEnvelope> events, WriteContext context);

    /// <summary>Removes everything the projection wrote, before a rebuild replays every event.</summary>
    /// <param name="context">The connection, transaction and scope.</param>
    protected virtual Task ResetAsync(WriteContext context) => throw ResetMissing();

    internal override Task ApplyBatch(IReadOnlyList<EventEnvelope> events, TransactionWork work) => ApplyAsync(events, new WriteContext(work));

    internal override Task Reset(TransactionWork work) => ResetAsync(new WriteContext(work));
}
