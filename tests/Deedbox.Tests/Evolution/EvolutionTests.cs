using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Evolution;

public sealed class PostgresEvolutionTests(Databases databases) : EvolutionTests(databases, Db.Postgres);

public sealed class SqlServerEvolutionTests(Databases databases) : EvolutionTests(databases, Db.SqlServer);

public record ItemAddedV1(string Sku);

public record ItemAddedV2(string Sku, int Qty);

public record LineAdded(string Sku, int Qty);

public static class CartEvents
{
    public record Opened(string Owner);

    public record Closed;
}

public abstract class EvolutionTests(Databases databases, Db db) : StoreTest(databases, db)
{
    private static Action<DeedboxBuilder> Version1 => b => b.Stream<Cart>(s => s.Event<ItemAddedV1>("cart.item_added").Event<CheckedOut>());

    [Fact]
    public async Task An_alias_keeps_events_under_an_old_name_readable_and_new_ones_use_the_new_name()
    {
        var id = NewStreamId();
        await (await Store(b => b.Stream<Cart>(s => s.Event<ItemAdded>("cart.line_added")))).Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 2)]);

        var renamed = await Store(b => b.Stream<Cart>(s => s.Event<ItemAdded>(e => e.Alias("cart.line_added"))));
        await renamed.Append(id, ExpectedVersion.Exact(1), [new ItemAdded("a", 3)]);
        await ClearSnapshots();

        Assert.Equal(5, (await renamed.Load<Cart>(id)).State.Items["a"]);
        Assert.Equal(["cart.line_added", "cart.item_added"], await EventTypesInOrder());
    }

    [Fact]
    public async Task A_json_upcaster_reads_old_versions_and_new_events_store_the_new_version()
    {
        var id = NewStreamId();
        await (await Store(Version1)).Append(id, ExpectedVersion.NoStream, [new ItemAddedV1("a")]);

        var v2 = await Store(b => b.Stream<Cart>(s => s
            .Event<ItemAdded>(version: 2, up => up.From(1, json => json["qty"] ??= 1))
            .Event<CheckedOut>()));
        await v2.Append(id, ExpectedVersion.Exact(1), [new ItemAdded("a", 4)]);
        await ClearSnapshots();

        Assert.Equal(5, (await v2.Load<Cart>(id)).State.Items["a"]);
        Assert.Equal([1, 2], await EventVersionsInOrder());
    }

    [Fact]
    public async Task Upcasters_chain_json_steps_then_a_typed_step()
    {
        var id = NewStreamId();
        await (await Store(Version1)).Append(id, ExpectedVersion.NoStream, [new ItemAddedV1("a")]);
        await (await Store(b => b.Stream<Cart>(s => s
            .Event<ItemAddedV2>(version: 2, up => up.Name("cart.item_added").From(1, json => json["qty"] ??= 1))
            .Event<CheckedOut>()))).Append(id, ExpectedVersion.Exact(1), [new ItemAddedV2("a", 2)]);

        var v3 = await Store(b => b.Stream<Cart>(s => s
            .Event<ItemAdded>(version: 3, up => up
                .From(1, json => json["qty"] ??= 1)
                .Upcast<ItemAddedV2, ItemAdded>(old => new ItemAdded(old.Sku, old.Qty * 10)))
            .Event<CheckedOut>()));
        await ClearSnapshots();

        // v1 gets qty 1 from the JSON step, then x10 from the typed step; v2 gets x10.
        Assert.Equal(30, (await v3.Load<Cart>(id)).State.Items["a"]);
    }

    [Fact]
    public async Task Events_nested_in_a_class_register_with_conventional_names()
    {
        var store = await Store(b => b.Stream<Cart>(s => s.EventsNestedIn(typeof(CartEvents)).Events<ItemAdded>()));

        var result = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new CartEvents.Opened("ada"), new CartEvents.Closed()]);

        Assert.Equal(["cart.opened", "cart.closed"], result.Events.Select(e => e.EventType));
    }

    [Fact]
    public async Task Appends_record_each_event_type_once()
    {
        var store = await Store();
        await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.Append(NewStreamId(), ExpectedVersion.NoStream, [new OrderPlaced("x")])));

        Assert.Equal(["cart|cart.checked_out|1", "cart|cart.item_added|1", "order|order.order_placed|1"], await StoredEventTypes());
    }

    [Fact]
    public async Task An_event_type_first_written_in_a_rolled_back_transaction_is_still_recorded_later()
    {
        var store = await Store();
        await using (var connection = await OpenConnection())
        {
            await using (var rolledBack = await connection.BeginTransactionAsync(Ct))
            {
                await store.UseTransaction(rolledBack).Append(NewStreamId(), ExpectedVersion.NoStream, [new OrderPlaced("x")]);
                await rolledBack.RollbackAsync(Ct);
            }

            await using var committed = await connection.BeginTransactionAsync(Ct);
            await store.UseTransaction(committed).Append(NewStreamId(), ExpectedVersion.NoStream, [new OrderPlaced("y")]);
            await committed.CommitAsync(Ct);
        }

        Assert.Equal(["order|order.order_placed|1"], await StoredEventTypes());
    }

    [Fact]
    public async Task Startup_fails_when_a_stored_event_name_has_no_mapping_and_names_the_likely_rename()
    {
        await (await Store(b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()))).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);

        var error = await StartupError(b => b.Stream<Cart>(s => s.Events<LineAdded, CheckedOut>()));

        Assert.Equal("DBX016", error.Code);
        Assert.Contains("Stored events 'cart.item_added' v1 have no mapping. Did you rename LineAdded? Add .Alias(\"cart.item_added\") to it.", error.Message, StringComparison.Ordinal);
        await StartHost(b => b.Stream<Cart>(s => s.Event<LineAdded>(e => e.Alias("cart.item_added")).Event<CheckedOut>()));
    }

    [Fact]
    public async Task Startup_fails_when_stored_events_are_newer_than_the_build()
    {
        await (await Store(b => b.Stream<Cart>(s => s.Event<ItemAdded>(version: 2, up => up.From(1, _ => { }))))).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        var error = await StartupError(b => b.Stream<Cart>(s => s.Events<ItemAdded>()));

        Assert.Equal("DBX017", error.Code);
        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_fails_when_an_event_moved_to_another_stream_type()
    {
        await (await Store(b => b.Stream<Cart>(s => s.Events<ItemAdded>()))).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        var error = await StartupError(b => b.Stream<Cart>(s => s.Events<CheckedOut>()).Stream<Order>(s => s.Event<ItemAdded>("cart.item_added")));

        Assert.Equal("DBX019", error.Code);
    }

    [Fact]
    public async Task Startup_lists_every_stored_name_problem_at_once()
    {
        await (await Store(b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()))).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);

        var error = await StartupError(b => b.Stream<Cart>(s => s.Events<LineAdded>()));

        Assert.Contains("2 stored event types do not match", error.Message, StringComparison.Ordinal);
        Assert.Contains("cart.item_added", error.Message, StringComparison.Ordinal);
        Assert.Contains("cart.checked_out", error.Message, StringComparison.Ordinal);
    }

    private async Task<DeedboxException> StartupError(Action<DeedboxBuilder> configure) =>
        await Assert.ThrowsAsync<DeedboxException>(() => StartHost(configure));

    private async Task StartHost(Action<DeedboxBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDeedbox(b => configure(UseDatabase(b)));
        using var host = builder.Build();
        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    private async Task ClearSnapshots() => await Execute($"UPDATE {Table("streams")} SET state = NULL");

    private async Task<List<string>> EventTypesInOrder() =>
        await Strings($"SELECT event_type FROM {Table("events")} ORDER BY global_position");

    private async Task<List<int>> EventVersionsInOrder() =>
        (await Strings($"SELECT CAST(event_version AS varchar(10)) FROM {Table("events")} ORDER BY global_position")).Select(int.Parse).ToList();

    private async Task<List<string>> StoredEventTypes() =>
        (await Strings($"SELECT stream_type, event_type, event_version FROM {Table("event_types")}")).Order(StringComparer.Ordinal).ToList();

    private async Task<List<string>> Strings(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
