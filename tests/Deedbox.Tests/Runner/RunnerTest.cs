using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Runner;

/// <summary>What the test consumers did, and switches that make them fail.</summary>
public sealed class Probe(string schema, Db db)
{
    public string Table(string name) => db == Db.Postgres ? $"{schema}.{name}" : $"[{schema}].[{name}]";

    public ConcurrentQueue<(string Consumer, EventEnvelope Envelope)> Delivered { get; } = new();

    public ConcurrentQueue<IReadOnlyList<EventEnvelope>> Batches { get; } = new();

    public volatile string? PoisonSku;

    public int Resets;

    public string Prefix { get; } = schema + ":";
}

/// <summary>Writes one row per applied event, keyed by event ID, so applying an event twice fails loudly.</summary>
public abstract class Applied : Projection
{
    protected Applied(Probe probe, string label)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            if (e.Sku == probe.PoisonSku)
                throw new InvalidOperationException($"poison {e.Sku}");
            await TestTables.Insert(ctx.Connection, ctx.Transaction,
                $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
                ("id", ctx.EventId.ToString()), ("projection", label), ("position", ctx.GlobalPosition ?? -1));
        });
        Probe = probe;
        Label = label;
    }

    private Probe Probe { get; }

    private string Label { get; }

    protected override async Task ResetAsync(WriteContext context)
    {
        Interlocked.Increment(ref Probe.Resets);
        await TestTables.Insert(context.Connection, context.Transaction, $"DELETE FROM {Probe.Table("applied")} WHERE projection = @projection", ("projection", Label));
    }
}

public sealed class AsyncApplied(Probe probe) : Applied(probe, "async");

public sealed class InlineApplied(Probe probe) : Applied(probe, "inline");

public sealed class OrdersOnly : Projection
{
    public OrdersOnly(Probe probe) => On<OrderPlaced>((_, ctx) =>
    {
        probe.Delivered.Enqueue(("orders_only", new EventEnvelope(ctx.EventId, ctx.TenantId, ctx.StreamId, ctx.StreamType, ctx.Version, ctx.GlobalPosition!.Value, "", 1, new object(), ctx.Metadata, ctx.OccurredAt)));
        return Task.CompletedTask;
    });
}

/// <summary>Counts items per cart through EF Core; its rows are prefixed with the test schema, so a reset only clears them.</summary>
public sealed class EfCartTotals : Projection<OrdersDb>
{
    public EfCartTotals(Probe probe)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            var id = probe.Prefix + ctx.StreamId;
            var row = await ctx.Db.Orders.FindAsync([id], ctx.CancellationToken);
            if (row is null)
                ctx.Db.Orders.Add(new OrderRow { Id = id, Note = e.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            else
                row.Note = (int.Parse(row.Note, System.Globalization.CultureInfo.InvariantCulture) + e.Qty).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        Probe = probe;
    }

    private Probe Probe { get; }

    protected override async Task ResetAsync(WriteContext<OrdersDb> context)
    {
        Interlocked.Increment(ref Probe.Resets);
        context.Db.Orders.RemoveRange(await context.Db.Orders.Where(o => o.Id.StartsWith(Probe.Prefix)).ToListAsync(context.CancellationToken));
    }
}

public sealed class NoReset : Projection
{
    public NoReset() => On<ItemAdded>((_, _) => Task.CompletedTask);
}

public sealed class Receipts : Subscription
{
    public Receipts(Probe probe)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            if (e.Sku == probe.PoisonSku)
                throw new InvalidOperationException($"poison {e.Sku}");
            probe.Delivered.Enqueue(("receipts", ctx.Envelope));
            if (e.Sku == "follow-up")
            {
                var store = ctx.Services.GetRequiredService<IEventStore>();
                await store.Append("order-for-" + ctx.Envelope.StreamId, ExpectedVersion.Any, [new OrderPlaced(ctx.Envelope.StreamId)], ctx.CancellationToken);
            }
        });
    }
}

public sealed class Bulk : BatchProjection
{
    public Bulk(Probe probe)
    {
        Handles<ItemAdded>();
        Probe = probe;
    }

    private Probe Probe { get; }

    protected override Task ApplyAsync(IReadOnlyList<EventEnvelope> events, WriteContext context)
    {
        Probe.Batches.Enqueue(events);
        return Task.CompletedTask;
    }
}

public abstract class RunnerTest(Databases databases, Db db) : DatabaseTest(databases, db), IAsyncDisposable
{
    private readonly List<IHost> _hosts = [];

    protected Probe NewProbe() => new(Schema, Db);

    protected async Task<IHost> StartHost(Probe probe, Action<DeedboxBuilder> configure, Action<RunnerOptions>? runner = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(probe);
        builder.Services.AddDbContext<OrdersDb>(o => o.Use(Db, ConnectionString));
        builder.Services.AddHealthChecks().AddDeedboxHealthChecks();
        builder.Services.AddDeedbox(b =>
        {
            UseDatabase(b).ApplySchemaOnStartup().Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()).Stream<Order>(s => s.Events<OrderPlaced>());
            b.Runner(o =>
            {
                o.MinPollDelay = TimeSpan.FromMilliseconds(10);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(200);
                o.RetryDelay = TimeSpan.FromMilliseconds(10);
                o.HandlerRetries = 2;
                runner?.Invoke(o);
            });
            configure(b);
        });
        var host = builder.Build();
        _hosts.Add(host);
        await EnsureTables();
        await host.StartAsync(Ct);
        return host;
    }

    protected async Task StopHost(IHost host)
    {
        await host.StopAsync(Ct);
        _hosts.Remove(host);
        host.Dispose();
    }

    protected static IEventStore StoreOf(IHost host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

    private protected static DeedboxRuntime RuntimeOf(IHost host) => host.Services.GetRequiredService<DeedboxRuntime>();

    private protected async Task<CheckpointRow> Checkpoint(IHost host, string name)
    {
        await using var connection = await OpenConnection();
        return (await RuntimeOf(host).Provider.ReadCheckpoints(connection, Ct)).Single(r => r.Name == name);
    }

    protected async Task<long> Head(IHost host)
    {
        await using var connection = await OpenConnection();
        return await RuntimeOf(host).Provider.ReadHead(connection, null, Ct);
    }

    private protected async Task WaitForCheckpoint(IHost host, string name, Func<CheckpointRow, bool> condition)
    {
        CheckpointRow? last = null;
        try
        {
            await WaitFor(async () => condition(last = await Checkpoint(host, name)), $"checkpoint '{name}'");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Last: {last}; head {await Head(host)}.", ex);
        }
    }

    protected async Task WaitForCaughtUp(IHost host, string name)
    {
        var head = await Head(host);
        await WaitForCheckpoint(host, name, r => r.Position >= head && r.Status == "running");
    }

    protected static async Task WaitFor(Func<Task<bool>> condition, string what, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(25, Ct);
        }
    }

    protected static async Task<Guid> Enqueue(IHost host, string kind, JsonObject args) =>
        await Jobs.Enqueue(RuntimeOf(host), kind, args, Ct);

    private protected async Task<JobRow> WaitForJob(IHost host, Guid id)
    {
        JobRow? job = null;
        await WaitFor(async () =>
        {
            await using var connection = await OpenConnection();
            job = await RuntimeOf(host).Provider.ReadJob(connection, id, Ct);
            return job?.Status is "done" or "failed";
        }, $"job {id}");
        return job!;
    }

    protected async Task<HealthReport> Health(IHost host) =>
        await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);

    protected async Task<int> AppliedCount(string label) =>
        await Scalar<int>($"SELECT COUNT(*) FROM {Table("applied")} WHERE projection = '{label}'");

    private async Task EnsureTables()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = Db == Db.Postgres
            ? $"""
               CREATE SCHEMA IF NOT EXISTS {Schema};
               CREATE TABLE IF NOT EXISTS {Table("applied")} (event_id varchar(50) NOT NULL, projection varchar(50) NOT NULL, position bigint NOT NULL, PRIMARY KEY (event_id, projection));
               """
            : $"""
               IF SCHEMA_ID('{Schema}') IS NULL EXEC('CREATE SCHEMA [{Schema}]');
               IF OBJECT_ID('{Table("applied")}') IS NULL
               CREATE TABLE {Table("applied")} (event_id varchar(50) NOT NULL, projection varchar(50) NOT NULL, position bigint NOT NULL, PRIMARY KEY (event_id, projection));
               """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
