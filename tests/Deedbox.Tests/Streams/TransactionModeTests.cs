using System.Data.Common;
using Deedbox.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Deedbox.Tests.Streams;

public sealed class PostgresTransactionModeTests(Databases databases) : TransactionModeTests(databases, Db.Postgres);

public sealed class SqlServerTransactionModeTests(Databases databases) : TransactionModeTests(databases, Db.SqlServer);

public abstract class TransactionModeTests(Databases databases, Db db) : StoreTest(databases, db)
{
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
    public async Task DbContext_mode_commits_entities_of_several_contexts_with_the_events()
    {
        var store = await Store();
        await EnsureEfTables();
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
        await EnsureEfTables();
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
        await EnsureEfTables();
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
        await EnsureEfTables();
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

    private DbContextOptions<T> Options<T>(DbConnection connection) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        if (Db == Db.Postgres)
            builder.UseNpgsql(connection);
        else
            builder.UseSqlServer(connection);
        return builder.Options;
    }

    private async Task EnsureEfTables()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = Db == Db.Postgres
            ? """
              CREATE SCHEMA IF NOT EXISTS ef_tests;
              CREATE TABLE IF NOT EXISTS ef_tests.orders (id text PRIMARY KEY, note text NOT NULL);
              CREATE TABLE IF NOT EXISTS ef_tests.audit (id text PRIMARY KEY, what text NOT NULL);
              """
            : """
              IF SCHEMA_ID('ef_tests') IS NULL EXEC('CREATE SCHEMA ef_tests');
              IF OBJECT_ID('ef_tests.orders') IS NULL CREATE TABLE ef_tests.orders (id nvarchar(200) PRIMARY KEY, note nvarchar(200) NOT NULL);
              IF OBJECT_ID('ef_tests.audit') IS NULL CREATE TABLE ef_tests.audit (id nvarchar(200) PRIMARY KEY, what nvarchar(200) NOT NULL);
              """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task<int> CountEf(string table, string id) => Scalar<int>($"SELECT COUNT(*) FROM ef_tests.{table} WHERE id = '{id}'");
}

public sealed class OrderRow
{
    public required string Id { get; set; }
    public required string Note { get; set; }
}

public sealed class AuditRow
{
    public required string Id { get; set; }
    public required string What { get; set; }
}

public sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OrderRow>(e =>
        {
            e.ToTable("orders", "ef_tests");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Note).HasColumnName("note");
        });
}

public sealed class AuditDb(DbContextOptions<AuditDb> options) : DbContext(options)
{
    public DbSet<AuditRow> Entries => Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AuditRow>(e =>
        {
            e.ToTable("audit", "ef_tests");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.What).HasColumnName("what");
        });
}
