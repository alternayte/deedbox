using System.Data.Common;

namespace Deedbox;

/// <summary>
/// The common base of <see cref="Projection"/> and the EF Core <c>Projection&lt;TDbContext&gt;</c>.
/// Derive from one of those, not from this class.
/// </summary>
public abstract class ProjectionBase
{
    private readonly Dictionary<Type, Func<object, ProjectionInvocation, Task>> _handlers = [];

    internal ProjectionBase()
    {
    }

    internal IReadOnlyCollection<Type> HandledTypes => _handlers.Keys;

    internal virtual bool IsBatch => false;

    /// <summary>Removes everything the projection wrote, in the rebuild's transaction.</summary>
    internal abstract Task Reset(TransactionWork work);

    internal virtual Task ApplyBatch(IReadOnlyList<EventEnvelope> events, TransactionWork work) => throw new NotSupportedException();

    internal DeedboxException ResetMissing() => new(Errors.ResetNotImplemented,
        $"{GetType().Name} does not override ResetAsync, so it cannot be rebuilt. Override ResetAsync to delete what the projection wrote.");

    internal bool Handles(Type eventType) => _handlers.ContainsKey(eventType);

    internal Task Handle(object @event, ProjectionInvocation invocation) =>
        _handlers.TryGetValue(@event.GetType(), out var handler) ? handler(@event, invocation) : Task.CompletedTask;

    internal void AddHandler(Type eventType, Func<object, ProjectionInvocation, Task> handler)
    {
        if (!_handlers.TryAdd(eventType, handler))
            throw new InvalidOperationException($"{GetType().Name} handles {eventType.Name} twice. Register one handler per event type.");
    }
}

/// <summary>
/// A projection that writes with plain ADO.NET or Dapper through <see cref="ProjectionContext.Connection"/> and
/// <see cref="ProjectionContext.Transaction"/>. Register handlers in the constructor with <see cref="On{TEvent}"/>.
/// </summary>
public abstract class Projection : ProjectionBase
{
    /// <summary>Creates the projection.</summary>
#pragma warning disable RS0022 // ProjectionBase has no protected members to expose.
    protected Projection()
#pragma warning restore RS0022
    {
    }

    /// <summary>
    /// Removes everything the projection wrote. A rebuild calls it, then replays every event. The default throws,
    /// so a projection without it cannot be rebuilt.
    /// </summary>
    /// <param name="context">The rebuild's connection, transaction and scope.</param>
    protected virtual Task ResetAsync(WriteContext context) => throw ResetMissing();

    internal override Task Reset(TransactionWork work) => ResetAsync(new WriteContext(work));

    /// <summary>Handles one event type. Events of types the projection does not handle are skipped.</summary>
    /// <param name="handler">Writes the event's effect through the context's connection and transaction.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    protected void On<TEvent>(Func<TEvent, ProjectionContext, Task> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddHandler(typeof(TEvent), (e, invocation) => handler((TEvent)e, new ProjectionContext(invocation)));
    }
}

/// <summary>Where a rebuild's reset or a batch projection writes: a connection, a transaction and a scope.</summary>
public class WriteContext
{
    internal WriteContext(TransactionWork work)
    {
        Work = work;
    }

    internal TransactionWork Work { get; }

    /// <summary>The connection to write on.</summary>
    public DbConnection Connection => Work.Connection;

    /// <summary>The transaction to write in.</summary>
    public DbTransaction Transaction => Work.Transaction;

    /// <summary>The services of the scope the work runs in.</summary>
    public IServiceProvider Services => Work.Services;

    /// <summary>Cancels the work.</summary>
    public CancellationToken CancellationToken => Work.CancellationToken;
}

/// <summary>What a projection handler knows about the event it handles, and where it writes.</summary>
public class ProjectionContext
{
    internal ProjectionContext(ProjectionInvocation invocation)
    {
        Invocation = invocation;
    }

    internal ProjectionInvocation Invocation { get; }

    /// <summary>The event's ID; an idempotency key.</summary>
    public Guid EventId => Invocation.EventId;

    /// <summary>The tenant.</summary>
    public string TenantId => Invocation.TenantId;

    /// <summary>The stream.</summary>
    public string StreamId => Invocation.StreamId;

    /// <summary>The stored stream type name.</summary>
    public string StreamType => Invocation.StreamType;

    /// <summary>The event's version in its stream.</summary>
    public long Version => Invocation.Version;

    /// <summary>
    /// The event's global position, or null in an inline handler: an inline handler runs before the position is assigned.
    /// </summary>
    public long? GlobalPosition => Invocation.GlobalPosition;

    /// <summary>The event's metadata.</summary>
    public EventMetadata Metadata => Invocation.Metadata;

    /// <summary>When the event was appended.</summary>
    public DateTimeOffset OccurredAt => Invocation.OccurredAt;

    /// <summary>The connection to write on.</summary>
    public DbConnection Connection => Invocation.Connection;

    /// <summary>The transaction to write in. Its commit also commits the checkpoint or the events.</summary>
    public DbTransaction Transaction => Invocation.Transaction;

    /// <summary>The services of the scope the handler runs in.</summary>
    public IServiceProvider Services => Invocation.Services;

    /// <summary>Cancels the handler.</summary>
    public CancellationToken CancellationToken => Invocation.CancellationToken;
}

/// <summary>One handler call: the event's identity plus the shared state of the transaction it runs in.</summary>
internal sealed class ProjectionInvocation(TransactionWork work)
{
    public TransactionWork Work { get; } = work;
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public string StreamType { get; set; } = "";
    public long Version { get; set; }
    public long? GlobalPosition { get; set; }
    public EventMetadata Metadata { get; set; } = EventMetadata.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DbConnection Connection => Work.Connection;
    public DbTransaction Transaction => Work.Transaction;
    public IServiceProvider Services => Work.Services;
    public CancellationToken CancellationToken => Work.CancellationToken;
}

/// <summary>
/// State shared by every handler in one transaction: resources a flavour creates once (such as an EF Core
/// DbContext), work to run just before the position counter, and cleanup when the transaction ends.
/// </summary>
internal sealed class TransactionWork(DbConnection connection, DbTransaction transaction, IServiceProvider services, IReadOnlyList<object> participants, CancellationToken ct)
    : IAsyncDisposable
{
    private readonly List<Func<CancellationToken, Task>> _beforeCounter = [];
    private readonly List<IAsyncDisposable> _cleanup = [];

    public DbConnection Connection { get; } = connection;
    public DbTransaction Transaction { get; } = transaction;
    public IServiceProvider Services { get; } = services;
    public CancellationToken CancellationToken { get; } = ct;

    /// <summary>Objects that already take part in the transaction, such as the DbContexts passed to UseDbContext.</summary>
    public IReadOnlyList<object> Participants { get; } = participants;

    public Dictionary<object, object> Items { get; } = [];

    public void BeforeCounter(Func<CancellationToken, Task> work) => _beforeCounter.Add(work);

    public void Cleanup(IAsyncDisposable resource) => _cleanup.Add(resource);

    public async Task RunBeforeCounter()
    {
        foreach (var work in _beforeCounter)
            await work(CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var resource in _cleanup)
            await resource.DisposeAsync();
    }
}
