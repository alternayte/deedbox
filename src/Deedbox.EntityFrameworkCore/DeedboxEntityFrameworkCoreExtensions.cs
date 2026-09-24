using Deedbox.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Deedbox;

/// <summary>Runs Deedbox operations inside EF Core DbContext transactions.</summary>
public static class DeedboxEntityFrameworkCoreExtensions
{
    /// <summary>
    /// A store that writes in <paramref name="context"/>'s transaction. When the context has no transaction,
    /// each operation opens one and commits it at the end. Deedbox calls SaveChanges on every context just
    /// before it commits the events, so entity changes and events commit together.
    /// </summary>
    /// <param name="store">The event store.</param>
    /// <param name="context">The context whose connection and transaction Deedbox uses.</param>
    /// <param name="others">More contexts on the same DbConnection, such as one per module; each is enlisted in the transaction.</param>
    public static IEventStore UseDbContext(this IEventStore store, DbContext context, params DbContext[] others)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(others);
        if (store is not EventStore eventStore)
            throw new ArgumentException("UseDbContext needs the IEventStore that AddDeedbox registers.", nameof(store));

        return eventStore.With(new DbContextTransactions(context, others));
    }
}
