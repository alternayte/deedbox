using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Deedbox.EntityFrameworkCore;

/// <summary>
/// Runs Deedbox operations in the primary DbContext's transaction, or opens one on it and commits it.
/// Every other DbContext must share the primary's connection; each is enlisted in the same transaction,
/// and each one's SaveChanges runs just before the position counter update.
/// </summary>
internal sealed class DbContextTransactions(DbContext primary, IReadOnlyList<DbContext> others) : TransactionSource
{
    public override async ValueTask<Lease> BeginWrite(DeedboxProvider provider, CancellationToken ct)
    {
        var connection = SharedConnection();
        var existing = primary.Database.CurrentTransaction;
        var efTransaction = existing ?? await primary.Database.BeginTransactionAsync(ct);
        var transaction = efTransaction.GetDbTransaction();

        var enlisted = new List<DbContext>();
        foreach (var other in others)
        {
            if (other.Database.CurrentTransaction?.GetDbTransaction() == transaction)
                continue;
            await other.Database.UseTransactionAsync(transaction, ct);
            enlisted.Add(other);
        }

        return new DbContextLease(connection, transaction, existing is null ? efTransaction : null, primary, others, enlisted);
    }

    public override async ValueTask<Lease> BeginRead(DeedboxProvider provider, CancellationToken ct)
    {
        var connection = SharedConnection();
        var transaction = primary.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is not null)
            return new ReadLease(connection, transaction, null);

        await primary.Database.OpenConnectionAsync(ct);
        return new ReadLease(connection, null, primary);
    }

    private DbConnection SharedConnection()
    {
        var connection = primary.Database.GetDbConnection();
        foreach (var other in others)
        {
            if (!ReferenceEquals(other.Database.GetDbConnection(), connection))
            {
                throw new DeedboxException(Errors.DbContextConnectionMismatch,
                    $"{other.GetType().Name} does not share {primary.GetType().Name}'s DbConnection, so they cannot share one transaction. " +
                    "Create both contexts with the same DbConnection instance.");
            }
        }

        return connection;
    }

    private sealed class DbContextLease(
        DbConnection connection, DbTransaction transaction, IDbContextTransaction? owned,
        DbContext primary, IReadOnlyList<DbContext> others, List<DbContext> enlisted) : Lease
    {
        public override DbConnection Connection => connection;

        public override DbTransaction? Transaction => transaction;

        public override async Task BeforeCounter(CancellationToken ct)
        {
            await primary.SaveChangesAsync(ct);
            foreach (var other in others)
                await other.SaveChangesAsync(ct);
        }

        public override bool Commits => owned is not null;

        public override Task Complete(CancellationToken ct) => owned?.CommitAsync(ct) ?? Task.CompletedTask;

        public override async ValueTask DisposeAsync()
        {
            if (owned is null)
                return;

            // Deedbox opened this transaction, so it detaches the contexts it enlisted before disposing it.
            foreach (var context in enlisted)
                await context.Database.UseTransactionAsync(null);
            await owned.DisposeAsync();
        }
    }

    private sealed class ReadLease(DbConnection connection, DbTransaction? transaction, DbContext? opened) : Lease
    {
        public override DbConnection Connection => connection;

        public override DbTransaction? Transaction => transaction;

        public override async ValueTask DisposeAsync()
        {
            if (opened is not null)
                await opened.Database.CloseConnectionAsync();
        }
    }
}
