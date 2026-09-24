using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Regressions;

public sealed class PostgresReadRaceTests(Databases databases) : ReadRaceTests(databases, Db.Postgres);

public sealed class SqlServerReadRaceTests(Databases databases) : ReadRaceTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiReadRaceTests(Databases databases) : ReadRaceTests(databases, Db.SqlServerRcsi);

public abstract class ReadRaceTests(Databases databases, Db db) : StoreTest(databases, db)
{
    /// <summary>
    /// Under locking READ COMMITTED (SQL Server without RCSI), a scan that waits on an append's row can resume past
    /// positions that a later append reuses after the first one rolls back, and return a later position first.
    /// The append torture suite found it; a reader that skipped those positions would lose events.
    /// </summary>
    [Fact]
    [Trait("Regression", "SQL Server: a locking read skipped positions reused after a rollback")]
    public async Task A_read_that_waits_on_a_rolled_back_append_returns_positions_without_gaps()
    {
        var store = await Store(b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()));
        await using var provider = CreateProvider();
        await store.Append("seed", ExpectedVersion.NoStream, [new ItemAdded("a", 1)], Ct);

        for (var round = 0; round < 30; round++)
        {
            await using var probe = await OpenConnection();
            var after = await provider.ReadHead(probe, null, Ct);

            await using var holder = await OpenConnection();
            await using var rolledBack = await holder.BeginTransactionAsync(Ct);
            // Many rolled-back rows give the waiting readers a long walk past them while the next append reuses their positions.
            await store.UseTransaction(rolledBack).Append($"a-{round}", ExpectedVersion.NoStream, Items(50), Ct);

            // Several readers wait on the uncommitted rows, each racing the append that reuses their positions.
            var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await using var reader = await OpenConnection();
                return await provider.ReadEventsAfter(reader, null, after, 100, null, Ct);
            })).ToList();
            var reuse = Task.Run(() => store.Append($"b-{round}", ExpectedVersion.NoStream, Items(60), Ct));
            await Task.Delay(30, Ct);
            await rolledBack.RollbackAsync(Ct);
            await reuse;

            foreach (var read in reads)
            {
                var positions = (await read).Select(e => e.GlobalPosition).ToList();
                Assert.Equal(Enumerable.Range(1, positions.Count).Select(i => after + i), positions);
            }
        }
    }

    private static List<object> Items(int count) => [.. Enumerable.Range(0, count).Select(i => new ItemAdded($"sku-{i}", 1))];
}
