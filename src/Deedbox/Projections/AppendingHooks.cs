using System.Data.Common;

namespace Deedbox;

/// <summary>
/// Runs inside every append's transaction, after inline projections and before the events are written.
/// Write outbox rows here so they commit or roll back with the events. Register with <c>OnAppending&lt;T&gt;()</c>.
/// </summary>
public interface IAppendingHook
{
    /// <summary>Called once per append.</summary>
    /// <param name="context">The append.</param>
    /// <param name="ct">Cancels the append.</param>
    Task OnAppending(AppendingContext context, CancellationToken ct);
}

/// <summary>One append, as an <see cref="IAppendingHook"/> sees it.</summary>
public sealed class AppendingContext
{
    internal AppendingContext(string tenantId, string streamId, string streamType, IReadOnlyList<PendingEvent> events, TransactionWork work)
    {
        TenantId = tenantId;
        StreamId = streamId;
        StreamType = streamType;
        Events = events;
        Connection = work.Connection;
        Transaction = work.Transaction;
        Services = work.Services;
    }

    /// <summary>The tenant.</summary>
    public string TenantId { get; }

    /// <summary>The stream.</summary>
    public string StreamId { get; }

    /// <summary>The stored stream type name.</summary>
    public string StreamType { get; }

    /// <summary>The events being appended, in order.</summary>
    public IReadOnlyList<PendingEvent> Events { get; }

    /// <summary>The append's connection.</summary>
    public DbConnection Connection { get; }

    /// <summary>The append's transaction.</summary>
    public DbTransaction Transaction { get; }

    /// <summary>The services of the append's scope.</summary>
    public IServiceProvider Services { get; }
}

/// <summary>An event that is being appended. Its global position is not assigned yet.</summary>
public sealed class PendingEvent
{
    internal PendingEvent(string streamId, Guid eventId, long version, string eventType, int eventVersion, object @event, EventMetadata metadata, DateTimeOffset occurredAt)
    {
        StreamId = streamId;
        EventId = eventId;
        Version = version;
        EventType = eventType;
        EventVersion = eventVersion;
        Event = @event;
        Metadata = metadata;
        OccurredAt = occurredAt;
    }

    /// <summary>The stream the event is appended to.</summary>
    public string StreamId { get; }

    /// <summary>The event's ID.</summary>
    public Guid EventId { get; }

    /// <summary>The event's version in its stream.</summary>
    public long Version { get; }

    /// <summary>The stored event type name.</summary>
    public string EventType { get; }

    /// <summary>The version of the event's shape.</summary>
    public int EventVersion { get; }

    /// <summary>The event.</summary>
    public object Event { get; }

    /// <summary>The event's metadata.</summary>
    public EventMetadata Metadata { get; }

    /// <summary>When the event is appended.</summary>
    public DateTimeOffset OccurredAt { get; }
}
