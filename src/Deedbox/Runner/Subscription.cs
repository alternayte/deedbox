namespace Deedbox;

/// <summary>
/// Reacts to committed events with side effects outside the database, such as email or HTTP calls. Delivery is
/// at least once: make handlers idempotent with <see cref="EventEnvelope.EventId"/>. Register handlers in the
/// constructor with <see cref="On{TEvent}"/>, and the subscription with <c>Subscription&lt;T&gt;(name)</c>.
/// </summary>
public abstract class Subscription
{
    private readonly Dictionary<Type, Func<object, SubscriptionContext, Task>> _handlers = [];

    /// <summary>Creates the subscription.</summary>
    protected Subscription()
    {
    }

    internal IReadOnlyCollection<Type> HandledTypes => _handlers.Keys;

    internal Task Handle(object @event, SubscriptionContext context) =>
        _handlers.TryGetValue(@event.GetType(), out var handler) ? handler(@event, context) : Task.CompletedTask;

    /// <summary>Handles one event type. Events of types the subscription does not handle are skipped.</summary>
    /// <param name="handler">Does the side effect. Throwing retries the event with backoff, then stalls the subscription.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    protected void On<TEvent>(Func<TEvent, SubscriptionContext, Task> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(typeof(TEvent), (e, context) => handler((TEvent)e, context)))
            throw new InvalidOperationException($"{GetType().Name} handles {typeof(TEvent).Name} twice. Register one handler per event type.");
    }
}

/// <summary>The event a subscription handles, and the scope it runs in.</summary>
public sealed class SubscriptionContext
{
    internal SubscriptionContext(EventEnvelope envelope, IServiceProvider services, CancellationToken ct)
    {
        Envelope = envelope;
        Services = services;
        CancellationToken = ct;
    }

    /// <summary>The event with its position and metadata.</summary>
    public EventEnvelope Envelope { get; }

    /// <summary>
    /// The services of a scope made for this event. A store resolved here appends with the event's tenant and
    /// correlation ID, and the event as the cause.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>Cancels the handler when the host stops.</summary>
    public CancellationToken CancellationToken { get; }
}
