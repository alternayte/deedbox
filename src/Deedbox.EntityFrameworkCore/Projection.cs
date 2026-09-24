using Deedbox.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox;

/// <summary>
/// A projection that writes through an EF Core <typeparamref name="TDbContext"/> enlisted in the transaction.
/// Deedbox calls SaveChanges before it commits. Register handlers in the constructor with <see cref="On{TEvent}"/>.
/// </summary>
/// <typeparam name="TDbContext">The app's DbContext type, registered with AddDbContext.</typeparam>
public abstract class Projection<TDbContext> : ProjectionBase where TDbContext : DbContext
{
    /// <summary>Creates the projection.</summary>
#pragma warning disable RS0022 // ProjectionBase has no protected members to expose.
    protected Projection()
#pragma warning restore RS0022
    {
    }

    /// <summary>Handles one event type. Events of types the projection does not handle are skipped.</summary>
    /// <param name="handler">Changes entities through <see cref="ProjectionContext{TDbContext}.Db"/>.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    protected void On<TEvent>(Func<TEvent, ProjectionContext<TDbContext>, Task> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        AddHandler(typeof(TEvent), async (e, invocation) =>
        {
            var db = await Enlistment.Get<TDbContext>(invocation.Work);
            await handler((TEvent)e, new ProjectionContext<TDbContext>(invocation, db));
        });
    }
}

/// <summary>The handler's view of the event, plus the enlisted DbContext.</summary>
/// <typeparam name="TDbContext">The app's DbContext type.</typeparam>
public sealed class ProjectionContext<TDbContext> : ProjectionContext where TDbContext : DbContext
{
    internal ProjectionContext(ProjectionInvocation invocation, TDbContext db)
        : base(invocation)
    {
        Db = db;
    }

    /// <summary>The DbContext, on the transaction's connection. Do not call SaveChanges; Deedbox does.</summary>
    public TDbContext Db { get; }
}
