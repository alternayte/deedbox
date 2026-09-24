using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Benchmarks;

internal sealed record Counted(int N);

internal sealed record Tally(long Count) : IState<Tally>
{
    public static Tally Initial { get; } = new(0);

    public static Tally Evolve(Tally state, object @event) => state with { Count = state.Count + 1 };
}

internal sealed class TallyRow
{
    public required string Id { get; set; }
    public long Count { get; set; }
}

internal sealed class BenchDb(DbContextOptions<BenchDb> options) : DbContext(options)
{
    public DbSet<TallyRow> Tallies => Set<TallyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<TallyRow>(e =>
        {
            e.ToTable("bench_tally", "bench_ef");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Count).HasColumnName("count");
        });

    /// <summary>One table for every cell: rows are keyed by stream ID and only counted, so cells may share them.</summary>
    public static async Task CreateTable(IServiceProvider services, string provider)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<BenchDb>>().CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(provider == "postgres"
            ? "CREATE SCHEMA IF NOT EXISTS bench_ef; CREATE TABLE IF NOT EXISTS bench_ef.bench_tally (id text PRIMARY KEY, count bigint NOT NULL)"
            : "IF SCHEMA_ID('bench_ef') IS NULL EXEC('CREATE SCHEMA bench_ef'); IF OBJECT_ID('bench_ef.bench_tally') IS NULL CREATE TABLE bench_ef.bench_tally (id nvarchar(200) PRIMARY KEY, count bigint NOT NULL)");
    }
}

/// <summary>The one inline EF Core projection of the efcore mode: a count per stream.</summary>
internal sealed class TallyProjection : Projection<BenchDb>
{
    public TallyProjection() => On<Counted>(async (_, ctx) =>
    {
        var row = await ctx.Db.Tallies.FindAsync([ctx.StreamId], ctx.CancellationToken);
        if (row is null)
            ctx.Db.Tallies.Add(new TallyRow { Id = ctx.StreamId, Count = 1 });
        else
            row.Count++;
    });
}
