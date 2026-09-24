using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.EntityFrameworkCore;

/// <summary>One DbContext per type per transaction, shared by every handler in it.</summary>
internal static class Enlistment
{
    public static async Task<T> Get<T>(TransactionWork work) where T : DbContext
    {
        if (work.Items.TryGetValue(typeof(T), out var existing))
            return (T)existing;

        // A context passed to UseDbContext is already enlisted, and the lease saves it.
        if (work.Participants.OfType<T>().FirstOrDefault() is { } participant)
        {
            work.Items[typeof(T)] = participant;
            return participant;
        }

        var db = ActivatorUtilities.CreateInstance<T>(work.Services);
        try
        {
            db.Database.SetDbConnection(work.Connection, contextOwnsConnection: false);
            await db.Database.UseTransactionAsync(work.Transaction, work.CancellationToken);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }

        work.Items[typeof(T)] = db;
        work.BeforeCounter(ct => db.SaveChangesAsync(ct));
        work.Cleanup(db);
        return db;
    }
}
