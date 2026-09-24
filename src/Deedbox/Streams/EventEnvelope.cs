namespace Deedbox;

/// <summary>One stored event with its position and identity.</summary>
public sealed class EventEnvelope
{
    internal EventEnvelope(
        Guid eventId, string tenantId, string streamId, string streamType, long version, long globalPosition,
        string eventType, int eventVersion, object @event, DateTimeOffset occurredAt)
    {
        EventId = eventId;
        TenantId = tenantId;
        StreamId = streamId;
        StreamType = streamType;
        Version = version;
        GlobalPosition = globalPosition;
        EventType = eventType;
        EventVersion = eventVersion;
        Event = @event;
        OccurredAt = occurredAt;
    }

    /// <summary>The event's unique ID (UUIDv7). Use it, or StreamId plus Version, as an idempotency key.</summary>
    public Guid EventId { get; }

    /// <summary>The tenant; empty when the app has no tenants.</summary>
    public string TenantId { get; }

    /// <summary>The stream the event belongs to.</summary>
    public string StreamId { get; }

    /// <summary>The stored stream type name, such as <c>cart</c>.</summary>
    public string StreamType { get; }

    /// <summary>The event's version in its stream, starting at 1.</summary>
    public long Version { get; }

    /// <summary>
    /// The event's position across all streams. Positions are gapless and follow commit order.
    /// Compare positions; never do arithmetic on them.
    /// </summary>
    public long GlobalPosition { get; }

    /// <summary>The stored event type name, such as <c>cart.item_added</c>.</summary>
    public string EventType { get; }

    /// <summary>The version of the event's shape.</summary>
    public int EventVersion { get; }

    /// <summary>The event.</summary>
    public object Event { get; }

    /// <summary>When the event was appended.</summary>
    public DateTimeOffset OccurredAt { get; }
}
