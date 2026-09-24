using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Deedbox.Tests.Infrastructure;

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

/// <summary>EF Core tables shared by every test, in the ef_tests schema; tests use unique row IDs.</summary>
public static class EfTables
{
    public static DbContextOptionsBuilder<T> Use<T>(this DbContextOptionsBuilder<T> builder, Db db, DbConnection connection) where T : DbContext =>
        db == Db.Postgres ? builder.UseNpgsql(connection) : builder.UseSqlServer(connection);

    public static DbContextOptionsBuilder Use(this DbContextOptionsBuilder builder, Db db, string connectionString) =>
        db == Db.Postgres ? builder.UseNpgsql(connectionString) : builder.UseSqlServer(connectionString);

    public static async Task Ensure(DbConnection connection, Db db, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = db == Db.Postgres
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
        await command.ExecuteNonQueryAsync(ct);
    }
}
