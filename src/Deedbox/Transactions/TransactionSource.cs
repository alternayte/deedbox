using System.Data;
using System.Data.Common;

namespace Deedbox;

/// <summary>Where an operation gets its connection and transaction: Deedbox, the caller, or a DbContext.</summary>
internal abstract class TransactionSource
{
    /// <summary>A connection and transaction for a write.</summary>
    public abstract ValueTask<Lease> BeginWrite(DeedboxProvider provider, CancellationToken ct);

    /// <summary>A connection, and the caller's transaction if there is one, for a read.</summary>
    public abstract ValueTask<Lease> BeginRead(DeedboxProvider provider, CancellationToken ct);
}

internal abstract class Lease : IAsyncDisposable
{
    public abstract DbConnection Connection { get; }

    public abstract DbTransaction? Transaction { get; }

    /// <summary>True when <see cref="Complete"/> commits, so what the operation wrote is durable once it returns.</summary>
    public virtual bool Commits => false;

    public DbTransaction WriteTransaction => Transaction ?? throw new InvalidOperationException("A write needs a transaction.");

    /// <summary>Runs just before the position counter update, such as EF Core SaveChanges.</summary>
    public virtual Task BeforeCounter(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Commits when this lease owns the transaction.</summary>
    public virtual Task Complete(CancellationToken ct) => Task.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Deedbox opens a connection and a transaction, and commits it.</summary>
internal sealed class OwnedTransactions : TransactionSource
{
    public static readonly OwnedTransactions Instance = new();

    public override async ValueTask<Lease> BeginWrite(DeedboxProvider provider, CancellationToken ct)
    {
        var connection = provider.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            return new OwnedLease(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public override async ValueTask<Lease> BeginRead(DeedboxProvider provider, CancellationToken ct)
    {
        var connection = provider.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            return new OwnedLease(connection, null);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class OwnedLease(DbConnection connection, DbTransaction? transaction) : Lease
    {
        public override DbConnection Connection => connection;

        public override DbTransaction? Transaction => transaction;

        public override bool Commits => transaction is not null;

        public override Task Complete(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;

        public override async ValueTask DisposeAsync()
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

/// <summary>The caller passed a transaction; Deedbox writes in it and never commits it.</summary>
internal sealed class CallerTransaction(DbTransaction transaction) : TransactionSource
{
    public override ValueTask<Lease> BeginWrite(DeedboxProvider provider, CancellationToken ct) => new(new CallerLease(transaction));

    public override ValueTask<Lease> BeginRead(DeedboxProvider provider, CancellationToken ct) => new(new CallerLease(transaction));

    private sealed class CallerLease(DbTransaction transaction) : Lease
    {
        public override DbConnection Connection => transaction.Connection
            ?? throw new InvalidOperationException("The transaction has already completed.");

        public override DbTransaction? Transaction => transaction;
    }
}
