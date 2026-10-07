using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Deedbox.EntityFrameworkCore;

/// <summary>
/// Runs EF Core work inside a transaction that already exists. A context with a retrying execution strategy, such as
/// EnableRetryOnFailure, refuses every query and SaveChanges in a transaction that the strategy did not start, because
/// it could not replay the transaction. Here the transaction is Deedbox's or the caller's, and whoever owns it retries
/// it as a whole. This strategy never retries; while it runs, the context's own strategy runs each operation once.
/// </summary>
internal sealed class InTransaction(DbContext context) : ExecutionStrategy(context, 0, TimeSpan.Zero)
{
    public override bool RetriesOnFailure => false;

    protected override bool ShouldRetryOn(Exception exception) => false;

    public static Task Run(DbContext context, Func<Task> work, CancellationToken ct) =>
        new InTransaction(context).ExecuteAsync(_ => work(), ct);
}
