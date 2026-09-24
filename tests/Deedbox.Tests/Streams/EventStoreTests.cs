using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Streams;

public sealed class PostgresEventStoreTests(Databases databases) : EventStoreTests(databases, Db.Postgres);

public sealed class SqlServerEventStoreTests(Databases databases) : EventStoreTests(databases, Db.SqlServer);

public abstract class EventStoreTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task Execute_creates_a_stream_and_load_returns_its_state()
    {
        var store = await Store();
        var id = NewStreamId();

        var first = await store.Execute<Cart>(id, _ => [new ItemAdded("apple", 2)]);
        var second = await store.Execute<Cart>(id, s => s.IsCheckedOut ? [] : [new ItemAdded("apple", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);
        var (state, version) = await store.Load<Cart>(id);

        Assert.Equal(1, first.Version);
        Assert.Equal(3, second.Version);
        Assert.Equal(3, version);
        Assert.Equal(3, state.Items["apple"]);
        Assert.True(state.IsCheckedOut);
        Assert.Equal(second.State, state with { Items = second.State.Items });
        Assert.Equal(second.State.Items, state.Items);
    }

    [Fact]
    public async Task Load_of_a_missing_stream_returns_the_initial_state_and_version_0()
    {
        var store = await Store();

        var (state, version) = await store.Load<Cart>(NewStreamId());

        Assert.Same(Cart.Initial, state);
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task Append_with_NoStream_fails_when_the_stream_exists()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() => store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("c", 1)]));

        Assert.Equal((id, 2L, "DBX014"), (conflict.StreamId, conflict.Actual, conflict.Code));
        Assert.Equal(ExpectedVersion.NoStream, conflict.Expected);
    }

    [Fact]
    public async Task Append_with_a_stale_version_fails_and_writes_nothing()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.Exact(0), [new ItemAdded("a", 1)]);
        await store.Append(id, ExpectedVersion.Exact(1), [new ItemAdded("a", 1)]);

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() => store.Append(id, ExpectedVersion.Exact(1), [new ItemAdded("a", 5)]));

        Assert.Equal(2, conflict.Actual);
        var (state, version) = await store.Load<Cart>(id);
        Assert.Equal((2L, 2), (version, state.Items["a"]));
        Assert.Equal(2, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task Append_with_Exact_on_a_missing_stream_fails()
    {
        var store = await Store();

        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() => store.Append(NewStreamId(), ExpectedVersion.Exact(3), [new ItemAdded("a", 1)]));

        Assert.Equal(0, conflict.Actual);
    }

    [Fact]
    public async Task Append_with_Any_creates_and_extends_a_stream()
    {
        var store = await Store();
        var id = NewStreamId();

        var created = await store.Append(id, ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        var extended = await store.Append(id, ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);

        Assert.Equal((1L, 3L), (created.Version, extended.Version));
        Assert.Equal([2L, 3L], extended.Events.Select(e => e.Version));
    }

    [Fact]
    public async Task Execute_that_decides_nothing_appends_nothing()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Execute<Cart>(id, _ => [new ItemAdded("a", 1)]);

        var result = await store.Execute<Cart>(id, _ => []);

        Assert.Equal(1, result.Version);
        Assert.Empty(result.Events);
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task Envelopes_carry_versions_gapless_positions_and_stored_names()
    {
        var store = await Store();
        var a = NewStreamId();
        var b = NewStreamId();

        var first = await store.Append(a, ExpectedVersion.NoStream, [new ItemAdded("x", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);
        var second = await store.Append(b, ExpectedVersion.NoStream, [new OrderPlaced("ada")]);

        Assert.Equal([1L, 2L, 3L], first.Events.Concat(second.Events).Select(e => e.GlobalPosition));
        Assert.Equal(["cart.item_added", "cart.checked_out", "order.order_placed"], first.Events.Concat(second.Events).Select(e => e.EventType));
        Assert.Equal(["cart", "cart", "order"], first.Events.Concat(second.Events).Select(e => e.StreamType));
        Assert.All(first.Events.Concat(second.Events), e => Assert.Equal('7', e.EventId.ToString()[14]));
        Assert.Equal(3L, await Scalar<long>($"SELECT value FROM {Table("position")}"));
        Assert.Equal("cart.checked_out", await Scalar<string>($"SELECT event_type FROM {Table("events")} WHERE global_position = 2"));
        var payload = System.Text.Json.JsonDocument.Parse(await Scalar<string>($"SELECT payload FROM {Table("events")} WHERE global_position = 1"));
        Assert.Equal("x", payload.RootElement.GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Concurrent_executes_on_one_stream_lose_no_update()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new Incremented(0)]);

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.Execute<Counter>(id, _ => [new Incremented(i)])));

        var (state, version) = await store.Load<Counter>(id);
        Assert.Equal(21, version);
        Assert.Equal((210L, 21), (state.Total, state.Count));
    }

    [Fact]
    public async Task Concurrent_executes_creating_one_stream_all_succeed()
    {
        var store = await Store();
        var id = NewStreamId();

        await Task.WhenAll(Enumerable.Range(1, 10).Select(i => store.Execute<Counter>(id, _ => [new Incremented(i)])));

        var (state, version) = await store.Load<Counter>(id);
        Assert.Equal(10, version);
        Assert.Equal((55L, 10), (state.Total, state.Count));
    }

    [Fact]
    public async Task Concurrent_appends_with_Any_creating_one_stream_all_succeed()
    {
        var store = await Store();
        var id = NewStreamId();

        await Task.WhenAll(Enumerable.Range(1, 10).Select(i => store.Append(id, ExpectedVersion.Any, [new Incremented(i)])));

        var (state, version) = await store.Load<Counter>(id);
        Assert.Equal((10L, 55L), (version, state.Total));
    }

    [Fact]
    public async Task Stream_of_another_type_is_rejected()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new OrderPlaced("ada")]);

        var load = await Assert.ThrowsAsync<DeedboxException>(() => store.Load<Cart>(id));
        var append = await Assert.ThrowsAsync<DeedboxException>(() => store.Append(id, ExpectedVersion.Any, [new ItemAdded("a", 1)]));

        Assert.Equal(("DBX008", "DBX008"), (load.Code, append.Code));
    }

    [Fact]
    public async Task Unregistered_types_and_mixed_streams_are_rejected()
    {
        var store = await Store();

        var unregisteredEvent = await Assert.ThrowsAsync<DeedboxException>(() => store.Append(NewStreamId(), ExpectedVersion.Any, [new Unregistered()]));
        var mixed = await Assert.ThrowsAsync<DeedboxException>(() => store.Append(NewStreamId(), ExpectedVersion.Any, [new ItemAdded("a", 1), new OrderPlaced("b")]));
        var foreignInExecute = await Assert.ThrowsAsync<DeedboxException>(() => store.Execute<Cart>(NewStreamId(), _ => [new OrderPlaced("b")]));

        Assert.Equal(("DBX006", "DBX010", "DBX010"), (unregisteredEvent.Code, mixed.Code, foreignInExecute.Code));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("streams")}"));
    }

    [Fact]
    public async Task Unregistered_state_is_rejected()
    {
        var store = await Store(b => b.Stream<Cart>(s => s.Events<ItemAdded>()));

        var error = await Assert.ThrowsAsync<DeedboxException>(() => store.Load<Order>(NewStreamId()));

        Assert.Equal("DBX009", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public async Task Invalid_stream_ids_are_rejected(string id)
    {
        var store = await Store();

        await Assert.ThrowsAsync<ArgumentException>(() => store.Load<Cart>(id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Append(id, ExpectedVersion.Any, [new ItemAdded("a", 1)]));
    }

    [Fact]
    public async Task Stream_ids_up_to_200_characters_are_accepted_and_longer_ones_rejected()
    {
        var store = await Store();

        await store.Append(new string('x', 200), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.Append(new string('x', 201), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]));
    }

    [Fact]
    public async Task Stream_ids_are_case_sensitive()
    {
        var store = await Store();

        await store.Append("Cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await store.Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("b", 1)]);

        Assert.True((await store.Load<Cart>("Cart-1")).State.Items.ContainsKey("a"));
        Assert.True((await store.Load<Cart>("cart-1")).State.Items.ContainsKey("b"));
    }

    private sealed record Unregistered;
}
