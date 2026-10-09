using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Streams;

public sealed class PostgresReadStreamTests(Databases databases) : ReadStreamTests(databases, Db.Postgres);

public sealed class SqlServerReadStreamTests(Databases databases) : ReadStreamTests(databases, Db.SqlServer);

public abstract class ReadStreamTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task ReadStream_returns_pages_in_version_order_with_afterVersion_as_the_cursor()
    {
        var store = await Store();
        var id = NewStreamId();
        var appended = await store.Append(id, ExpectedVersion.NoStream, Enumerable.Range(1, 5).Select(n => (object)new ItemAdded("sku", n)));

        var first = await store.ReadStream(id, limit: 2);
        var second = await store.ReadStream(id, afterVersion: first[^1].Version, limit: 2);
        var rest = await store.ReadStream(id, afterVersion: second[^1].Version);

        Assert.Equal([1L, 2L], first.Select(e => e.Version));
        Assert.Equal([3L, 4L], second.Select(e => e.Version));
        Assert.Equal([5L], rest.Select(e => e.Version));
        Assert.Empty(await store.ReadStream(id, afterVersion: 5));
        Assert.Equal([1, 2, 3, 4, 5], first.Concat(second).Concat(rest).Select(e => ((ItemAdded)e.Event).Qty));
        Assert.Equal(appended.Events.Select(e => (e.EventId, e.GlobalPosition, e.EventType, e.StreamType)),
            first.Concat(second).Concat(rest).Select(e => (e.EventId, e.GlobalPosition, e.EventType, e.StreamType)));
    }

    [Fact]
    public async Task ReadStream_of_a_missing_stream_is_empty_and_of_a_deleted_stream_is_its_tombstone()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);

        Assert.Empty(await store.ReadStream(NewStreamId()));
        await store.DeleteStream(id);

        var tombstone = Assert.Single(await store.ReadStream(id, limit: 1));
        Assert.IsType<StreamDeleted>(tombstone.Event);
        Assert.Equal(3, tombstone.Version);
        Assert.Empty(await store.ReadStream(id, afterVersion: 3));
        Assert.Equal("DBX028", (await Assert.ThrowsAsync<DeedboxException>(() => store.Load<Cart>(id))).Code);
    }

    [Fact]
    public async Task ReadStream_in_a_caller_transaction_sees_its_uncommitted_append()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await store.UseTransaction(transaction).Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        var inside = await store.UseTransaction(transaction).ReadStream(id);
        await transaction.RollbackAsync(Ct);

        Assert.Equal("a", ((ItemAdded)Assert.Single(inside).Event).Sku);
        Assert.Empty(await store.ReadStream(id));
    }

    [Fact]
    public async Task ReadStream_rejects_an_unregistered_stream_type_and_a_limit_below_1()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await using (var connection = await OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE {Table("streams")} SET stream_type = 'gone' WHERE stream_id = '{id}'";
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal("DBX009", (await Assert.ThrowsAsync<DeedboxException>(() => store.ReadStream(id))).Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadStream(id, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadStream(id, afterVersion: -1));
    }
}
