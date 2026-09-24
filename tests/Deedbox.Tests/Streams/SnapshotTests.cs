using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Streams;

public sealed class PostgresSnapshotTests(Databases databases) : SnapshotTests(databases, Db.Postgres);

public sealed class SqlServerSnapshotTests(Databases databases) : SnapshotTests(databases, Db.SqlServer);

public abstract class SnapshotTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task Every_append_stores_the_state_at_the_stream_version()
    {
        var store = await Store();
        var id = await AppendFive(store);

        var (stateAt, hasState) = await SnapshotRow(id);

        Assert.Equal((5L, true), (stateAt, hasState));
        Assert.Equal(new Counter(15, 5), (await store.Load<Counter>(id)).State);
    }

    [Fact]
    public async Task Every_n_stores_the_state_when_the_stream_passes_a_multiple_of_n()
    {
        var store = await Store(b => b.Stream<Counter>(s => s.Events<Incremented>().Snapshots(SnapshotPolicy.Every(3))));
        var id = await AppendFive(store);

        var (stateAt, _) = await SnapshotRow(id);

        Assert.Equal(3, stateAt);
        Assert.Equal(new Counter(15, 5), (await store.Load<Counter>(id)).State);
        Assert.Equal(new Counter(15, 5), (await store.Execute<Counter>(id, _ => [])).State);
    }

    [Fact]
    public async Task Never_stores_no_state_and_replays_every_load()
    {
        var store = await Store(b => b.Stream<Counter>(s => s.Events<Incremented>().Snapshots(SnapshotPolicy.Never)));
        var id = await AppendFive(store);

        var (_, hasState) = await SnapshotRow(id);

        Assert.False(hasState);
        Assert.Equal(new Counter(15, 5), (await store.Load<Counter>(id)).State);
    }

    [Fact]
    public async Task Load_reads_the_snapshot_instead_of_the_events()
    {
        var store = await Store();
        var id = await AppendFive(store);

        // A snapshot that differs from the events proves which one the load used.
        await Execute($"UPDATE {Table("streams")} SET state = '{{\"total\":999,\"count\":5}}'");

        Assert.Equal(new Counter(999, 5), (await store.Load<Counter>(id)).State);
    }

    [Fact]
    [Trait("Regression", "Anthology: snapshot lost fields on a state change")]
    public async Task A_new_state_version_rebuilds_the_snapshot_from_events_on_load()
    {
        var id = await AppendFive(await Store());
        await Execute($"UPDATE {Table("streams")} SET state = '{{\"total\":999,\"count\":5}}'");

        var upgraded = await Store(b => b.Stream<Counter>(s => s.Events<Incremented>().StateVersion(2)));
        var (state, version) = await upgraded.Load<Counter>(id);

        Assert.Equal((new Counter(15, 5), 5L), (state, version));
        Assert.Equal(2, await Scalar<int>($"SELECT state_version FROM {Table("streams")}"));
        Assert.Contains("15", await Scalar<string>($"SELECT state FROM {Table("streams")}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_state_version_rebuilds_the_snapshot_on_append()
    {
        var id = await AppendFive(await Store());
        await Execute($"UPDATE {Table("streams")} SET state = '{{\"total\":999,\"count\":5}}'");

        var upgraded = await Store(b => b.Stream<Counter>(s => s.Events<Incremented>().StateVersion(2)));
        await upgraded.Append(id, ExpectedVersion.Exact(5), [new Incremented(10)]);

        Assert.Equal(2, await Scalar<int>($"SELECT state_version FROM {Table("streams")}"));
        await Execute($"UPDATE {Table("events")} SET payload = '{{\"by\":0}}'");
        Assert.Equal(new Counter(25, 6), (await upgraded.Load<Counter>(id)).State);
    }

    private static async Task<string> AppendFive(IEventStore store)
    {
        var id = NewStreamId();
        for (var i = 1; i <= 5; i++)
            await store.Append(id, ExpectedVersion.Exact(i - 1), [new Incremented(i)]);
        return id;
    }

    private async Task<(long StateAt, bool HasState)> SnapshotRow(string id)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT state_at, CASE WHEN state IS NULL THEN 0 ELSE 1 END FROM {Table("streams")} WHERE stream_id = '{id}'";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt32(1) == 1);
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
