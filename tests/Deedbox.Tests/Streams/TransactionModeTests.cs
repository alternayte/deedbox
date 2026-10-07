using System.Data.Common;
using Deedbox.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Deedbox.Tests.Streams;

public sealed class PostgresTransactionModeTests(Databases databases) : TransactionModeTests(databases, Db.Postgres);

public sealed class SqlServerTransactionModeTests(Databases databases) : TransactionModeTests(databases, Db.SqlServer);

public sealed class FailsOnSku : IAppendingHook
{
    public Task OnAppending(AppendingContext context, CancellationToken ct) =>
        context.Events.Any(e => e.Event is ItemAdded { Sku: "fail" }) ? throw new InvalidOperationException("The hook failed.") : Task.CompletedTask;
}

public abstract class TransactionModeTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task A_failed_append_in_a_caller_transaction_leaves_nothing_when_the_caller_commits()
    {
        var store = StoreFrom(await Services(b => DefaultStreams(b.OnAppending<FailsOnSku>())));
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            var inside = store.UseTransaction(transaction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => inside.Append(id, ExpectedVersion.Any, [new ItemAdded("fail", 1)]));
            await inside.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("b", 1)]);
            await transaction.CommitAsync(Ct);
        }

        // The stream row moves before the hooks run; without the savepoint it would be at version 2 with one event.
        var next = await store.Append(id, ExpectedVersion.Exact(1), [new ItemAdded("c", 1)]);
        Assert.Equal(2, next.Version);
        Assert.Equal(0, await Scalar<int>(
            $"SELECT COUNT(*) FROM {Table("streams")} s WHERE s.version <> (SELECT COUNT(*) FROM {Table("events")} e WHERE e.tenant_id = s.tenant_id AND e.stream_id = s.stream_id)"));
        Assert.Equal(2, (await store.Load<Cart>(id)).State.Changes);
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task An_append_in_a_transaction_that_keeps_one_snapshot_is_refused(System.Data.IsolationLevel level)
    {
        var store = await Store();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(level, Ct);

        var refused = await Assert.ThrowsAsync<DeedboxException>(() =>
            store.UseTransaction(transaction).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]));

        Assert.Equal("DBX040", refused.Code);
    }

    [Fact]
    public async Task Caller_transaction_commit_keeps_the_events()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        await store.UseTransaction(transaction).Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        var inside = await store.UseTransaction(transaction).Load<Cart>(id);
        await transaction.CommitAsync(Ct);

        Assert.Equal(1, inside.Version);
        Assert.Equal(1, (await store.Load<Cart>(id)).Version);
    }

    [Fact]
    public async Task Caller_transaction_rollback_discards_the_events_and_the_positions()
    {
        var store = await Store();
        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await store.UseTransaction(transaction).Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
            await transaction.RollbackAsync(Ct);
        }

        var next = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("c", 1)]);

        Assert.Equal(1, next.Events[0].GlobalPosition);
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task Execute_in_a_caller_transaction_leaves_the_commit_to_the_caller()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        await store.UseTransaction(transaction).Execute<Cart>(id, _ => [new ItemAdded("a", 1)]);
        await transaction.RollbackAsync(Ct);

        Assert.Equal(0, (await store.Load<Cart>(id)).Version);
    }

    [Fact]
    public async Task DbContext_mode_works_with_a_retrying_execution_strategy()
    {
        // EnableRetryOnFailure makes EF Core refuse SaveChanges inside a transaction it did not start through the strategy.
        var store = await Store();
        var id = NewStreamId();
        var options = new DbContextOptionsBuilder<OrdersDb>();
        _ = Db == Db.Postgres
            ? options.UseNpgsql(ConnectionString, o => o.EnableRetryOnFailure())
            : options.UseSqlServer(ConnectionString, o => o.EnableRetryOnFailure());
        await using var orders = new OrdersDb(options.Options);

        orders.Orders.Add(new OrderRow { Id = id, Note = "placed" });
        await store.UseDbContext(orders).Append(id, ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        Assert.Equal(1, (await store.Load<Cart>(id)).Version);
        Assert.Equal(1, await CountEf("orders", id));
    }

    [Fact]
    public async Task DbContext_mode_commits_entities_of_several_contexts_with_the_events()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var orders = new OrdersDb(Options<OrdersDb>(connection));
        await using var audit = new AuditDb(Options<AuditDb>(connection));

        orders.Orders.Add(new OrderRow { Id = id, Note = "placed" });
        audit.Entries.Add(new AuditRow { Id = id, What = "order placed" });
        await store.UseDbContext(orders, audit).Append(id, ExpectedVersion.NoStream, [new OrderPlaced("ada")]);

        Assert.Null(orders.Database.CurrentTransaction);
        Assert.Equal(1, await CountEf("orders", id));
        Assert.Equal(1, await CountEf("audit", id));
        Assert.Equal(1, (await store.Load<Order>(id)).Version);
    }

    [Fact]
    public async Task DbContext_mode_commits_no_entity_when_the_append_conflicts()
    {
        var store = await Store();
        var id = NewStreamId();
        await store.Append(id, ExpectedVersion.NoStream, [new OrderPlaced("ada")]);
        await using var connection = await OpenConnection();
        await using var orders = new OrdersDb(Options<OrdersDb>(connection));
        await using var audit = new AuditDb(Options<AuditDb>(connection));

        orders.Orders.Add(new OrderRow { Id = id, Note = "placed twice" });
        audit.Entries.Add(new AuditRow { Id = id, What = "placed twice" });
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.UseDbContext(orders, audit).Append(id, ExpectedVersion.NoStream, [new OrderPlaced("bob")]));

        Assert.Equal(0, await CountEf("orders", id));
        Assert.Equal(0, await CountEf("audit", id));
    }

    [Fact]
    public async Task DbContext_mode_joins_the_callers_transaction_and_its_rollback()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var orders = new OrdersDb(Options<OrdersDb>(connection));
        await using var audit = new AuditDb(Options<AuditDb>(connection));

        await using (var transaction = await orders.Database.BeginTransactionAsync(Ct))
        {
            orders.Orders.Add(new OrderRow { Id = id, Note = "placed" });
            audit.Entries.Add(new AuditRow { Id = id, What = "order placed" });
            await store.UseDbContext(orders, audit).Execute<Order>(id, _ => [new OrderPlaced("ada")]);

            Assert.Same(transaction, orders.Database.CurrentTransaction);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await CountEf("orders", id));
        Assert.Equal(0, await CountEf("audit", id));
        Assert.Equal(0, (await store.Load<Order>(id)).Version);
    }

    [Fact]
    public async Task DbContext_mode_joins_the_callers_transaction_and_its_commit()
    {
        var store = await Store();
        var id = NewStreamId();
        await using var connection = await OpenConnection();
        await using var orders = new OrdersDb(Options<OrdersDb>(connection));

        await using (var transaction = await orders.Database.BeginTransactionAsync(Ct))
        {
            orders.Orders.Add(new OrderRow { Id = id, Note = "placed" });
            await store.UseDbContext(orders).Append(id, ExpectedVersion.NoStream, [new OrderPlaced("ada")]);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1, await CountEf("orders", id));
        Assert.Equal(1, (await store.Load<Order>(id)).Version);
    }

    [Fact]
    public async Task DbContext_mode_rejects_contexts_on_different_connections()
    {
        var store = await Store();
        await using var first = await OpenConnection();
        await using var second = await OpenConnection();
        await using var orders = new OrdersDb(Options<OrdersDb>(first));
        await using var audit = new AuditDb(Options<AuditDb>(second));

        var error = await Assert.ThrowsAsync<DeedboxException>(() => store.UseDbContext(orders, audit).Append(NewStreamId(), ExpectedVersion.Any, [new OrderPlaced("a")]));

        Assert.Equal("DBX012", error.Code);
    }

    private DbContextOptions<T> Options<T>(DbConnection connection) where T : DbContext =>
        new DbContextOptionsBuilder<T>().Use(Db, connection).Options;

    private Task<int> CountEf(string table, string id) => Scalar<int>($"SELECT COUNT(*) FROM ef_tests.{table} WHERE id = '{id}'");
}
