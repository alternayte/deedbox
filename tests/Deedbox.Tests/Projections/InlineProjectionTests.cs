using System.Collections.Concurrent;
using System.Data.Common;
using Deedbox.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Projections;

public sealed class PostgresInlineProjectionTests(Databases databases) : InlineProjectionTests(databases, Db.Postgres);

public sealed class SqlServerInlineProjectionTests(Databases databases) : InlineProjectionTests(databases, Db.SqlServer);

/// <summary>Where test projections write, and what they saw.</summary>
public sealed class TestTables(string schema, Db db)
{
    public string Table(string name) => db == Db.Postgres ? $"{schema}.{name}" : $"[{schema}].[{name}]";

    public ConcurrentQueue<(string Handler, ProjectionContext Context, object Event)> Seen { get; } = new();

    public ConcurrentQueue<AppendingContext> Appends { get; } = new();

    public static async Task Insert(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}

public sealed class CartLines : Projection
{
    public CartLines(TestTables tables)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            tables.Seen.Enqueue((nameof(CartLines), ctx, e));
            if (e.Sku == "boom")
                throw new InvalidOperationException("projection failed");
            await TestTables.Insert(ctx.Connection, ctx.Transaction,
                $"INSERT INTO {tables.Table("cart_lines")} (stream_id, version, sku, qty) VALUES (@stream, @version, @sku, @qty)",
                ("stream", ctx.StreamId), ("version", ctx.Version), ("sku", e.Sku), ("qty", e.Qty));
        });
    }
}

public sealed class OrderNotes : Projection
{
    public OrderNotes(TestTables tables)
    {
        On<OrderPlaced>((e, ctx) =>
        {
            tables.Seen.Enqueue((nameof(OrderNotes), ctx, e));
            return Task.CompletedTask;
        });
    }
}

public sealed class CartSummaries : Projection<OrdersDb>
{
    public CartSummaries(TestTables tables)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            tables.Seen.Enqueue((nameof(CartSummaries), ctx, e));
            var row = await ctx.Db.Orders.FindAsync([ctx.StreamId], ctx.CancellationToken);
            if (row is null)
                ctx.Db.Orders.Add(new OrderRow { Id = ctx.StreamId, Note = e.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            else
                row.Note = (int.Parse(row.Note, System.Globalization.CultureInfo.InvariantCulture) + e.Qty).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
    }
}

public sealed class Outbox(TestTables tables) : IAppendingHook
{
    public async Task OnAppending(AppendingContext context, CancellationToken ct)
    {
        tables.Appends.Enqueue(context);
        foreach (var e in context.Events)
        {
            if (e.Event is ItemAdded { Sku: "hook-boom" })
                throw new InvalidOperationException("hook failed");
            await TestTables.Insert(context.Connection, context.Transaction,
                $"INSERT INTO {tables.Table("outbox")} (event_id, event_type) VALUES (@id, @type)", ("id", e.EventId.ToString()), ("type", e.EventType));
        }
    }
}

public sealed class Unregistered;

public sealed class HandlesUnregistered : Projection
{
    public HandlesUnregistered() => On<Unregistered>((_, _) => Task.CompletedTask);
}

public abstract class InlineProjectionTests(Databases databases, Db db) : StoreTest(databases, db)
{
    private TestTables Tables => new(Schema, Db);

    [Fact]
    public async Task An_inline_projection_writes_in_the_append_transaction()
    {
        var (store, tables) = await Setup();
        var id = NewStreamId();

        var result = await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch), new ItemAdded("b", 2)]);

        Assert.Equal(2, await Scalar<int>($"SELECT COUNT(*) FROM {Table("cart_lines")}"));
        var seen = tables.Seen.Where(s => s.Handler == nameof(CartLines)).ToList();
        Assert.Equal([result.Events[0].EventId, result.Events[2].EventId], seen.Select(s => s.Context.EventId));
        Assert.Equal([1L, 3L], seen.Select(s => s.Context.Version));
        Assert.All(seen, s => Assert.Null(s.Context.GlobalPosition));
        Assert.All(seen, s => Assert.Equal((id, "cart", ""), (s.Context.StreamId, s.Context.StreamType, s.Context.TenantId)));
        Assert.All(seen, s => Assert.Equal((result.Events[0].OccurredAt, EventMetadata.Empty), (s.Context.OccurredAt, s.Context.Metadata)));
        Assert.All(seen, s => Assert.NotNull(s.Context.Services.GetService<TestTables>()));
        Assert.All(seen, s => Assert.False(s.Context.CancellationToken.IsCancellationRequested));
    }

    [Fact]
    public async Task A_failing_inline_projection_rolls_the_append_back()
    {
        var (store, _) = await Setup();
        var id = NewStreamId();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("boom", 1)]));

        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("cart_lines")}"));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
        Assert.Equal(0, (await store.Load<Cart>(id)).Version);
    }

    [Fact]
    public async Task A_caller_rollback_discards_projection_writes()
    {
        var (store, _) = await Setup();
        await using var connection = await OpenConnection();
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await store.UseTransaction(transaction).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("cart_lines")}"));
    }

    [Fact]
    public async Task A_projection_is_skipped_when_the_append_has_none_of_its_event_types()
    {
        var (store, tables) = await Setup();

        await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new OrderPlaced("ada")]);

        Assert.Equal(1, tables.Seen.Count(s => s.Handler == nameof(OrderNotes)));
        Assert.Equal(1, tables.Seen.Count(s => s.Handler == nameof(CartLines)));
    }

    [Fact]
    public async Task An_ef_projection_gets_a_context_on_the_append_connection_and_is_saved()
    {
        var (store, _) = await Setup();
        var id = NewStreamId();

        await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 2), new ItemAdded("b", 3)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Append(id, ExpectedVersion.Exact(2), [new ItemAdded("c", 4), new ItemAdded("boom", 1)]));

        Assert.Equal("5", await Scalar<string>($"SELECT note FROM ef_tests.orders WHERE id = '{id}'"));
    }

    [Fact]
    public async Task An_ef_projection_reuses_the_context_passed_to_UseDbContext()
    {
        var (store, tables) = await Setup();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var orders = new OrdersDb(new DbContextOptionsBuilder<OrdersDb>().Use(Db, connection).Options);

        await store.UseDbContext(orders).Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 2)]);

        var context = (ProjectionContext<OrdersDb>)tables.Seen.Single(s => s.Handler == nameof(CartSummaries)).Context;
        Assert.Same(orders, context.Db);
        Assert.Equal("2", await Scalar<string>($"SELECT note FROM ef_tests.orders WHERE id = '{id}'"));
    }

    [Fact]
    public async Task An_appending_hook_writes_in_the_append_transaction()
    {
        var (store, tables) = await Setup();

        var result = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("hook-boom", 1)]));

        Assert.Equal(2, await Scalar<int>($"SELECT COUNT(*) FROM {Table("outbox")}"));
        var append = tables.Appends.First();
        Assert.Equal(("", result.Events[0].StreamId, "cart"), (append.TenantId, append.StreamId, append.StreamType));
        Assert.Equal(result.Events.Select(e => (e.EventId, e.Version, e.EventType, e.EventVersion, e.OccurredAt, e.Metadata)),
            append.Events.Select(e => (e.EventId, e.Version, e.EventType, e.EventVersion, e.OccurredAt, e.Metadata)));
        Assert.Same(tables, append.Services.GetRequiredService<TestTables>());
        Assert.Equal("cart.checked_out", await Scalar<string>($"SELECT event_type FROM {Table("outbox")} WHERE event_id = '{result.Events[1].EventId}'"));
    }

    [Fact]
    [Trait("Regression", "Anthology: projections ran inline and async")]
    public async Task A_projection_registered_twice_or_under_a_used_name_fails_at_startup()
    {
        var twice = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Projection<CartLines>("lines", Run.Inline)
            .Projection<CartLines>("lines_async", Run.Async)));
        var sameName = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Projection<CartLines>("lines", Run.Inline)
            .Projection<CartSummaries>("lines", Run.Inline)));

        Assert.Equal(("DBX020", "DBX020"), (twice.Code, sameName.Code));
        Assert.Contains("applies events twice", twice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_projection_handling_an_unregistered_event_fails_at_startup()
    {
        await Services();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDeedbox(b => UseDatabase(b).Stream<Cart>(s => s.Events<ItemAdded>()).Projection<HandlesUnregistered>("bad", Run.Inline));
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<DeedboxException>(() => host.StartAsync(Ct));

        Assert.Equal("DBX021", error.Code);
    }

    private async Task<(IEventStore Store, TestTables Tables)> Setup()
    {
        var tables = Tables;
        var services = await Services(
            b =>
            {
                DefaultStreams(b);
                b.Projection<CartLines>("cart_lines", Run.Inline)
                    .Projection<OrderNotes>("order_notes", Run.Inline)
                    .Projection<CartSummaries>("cart_summaries", Run.Inline)
                    .OnAppending<Outbox>();
            },
            s => s.AddSingleton(tables).AddDbContext<OrdersDb>(o => o.Use(Db, ConnectionString)));

        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {Table("cart_lines")} (stream_id varchar(200) NOT NULL, version bigint NOT NULL, sku varchar(50) NOT NULL, qty int NOT NULL);
            CREATE TABLE {Table("outbox")} (event_id varchar(50) NOT NULL, event_type varchar(200) NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(Ct);
        return (StoreFrom(services), tables);
    }
}
