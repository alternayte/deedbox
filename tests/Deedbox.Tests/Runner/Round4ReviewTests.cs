using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Runner;

public sealed class PostgresRound4ReviewTests(Databases databases) : Round4ReviewTests(databases, Db.Postgres);

public sealed class SqlServerRound4ReviewTests(Databases databases) : Round4ReviewTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiRound4ReviewTests(Databases databases) : Round4ReviewTests(databases, Db.SqlServerRcsi);

/// <summary>The deployed version of a read model: one row per order. Its reset clears the rows that it writes.</summary>
public sealed class LedgerV1 : Projection
{
    private readonly Probe _probe;

    public LedgerV1(Probe probe)
    {
        _probe = probe;
        On<OrderPlaced>((_, ctx) => Row(probe, ctx, "ledger-orders"));
    }

    internal static Task Row(Probe probe, ProjectionContext ctx, string label) => TestTables.Insert(ctx.Connection, ctx.Transaction,
        $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
        ("id", ctx.EventId.ToString()), ("projection", label), ("position", ctx.GlobalPosition ?? -1));

    internal static Task Clear(Probe probe, WriteContext context, string label) => TestTables.Insert(context.Connection, context.Transaction,
        $"DELETE FROM {probe.Table("applied")} WHERE projection = @projection", ("projection", label));

    protected override Task ResetAsync(WriteContext context) => Clear(_probe, context, "ledger-orders");
}

/// <summary>The next version, under the same name: it also keeps one row per added item, and its reset clears both.</summary>
public sealed class LedgerV2 : Projection
{
    private readonly Probe _probe;

    public LedgerV2(Probe probe)
    {
        _probe = probe;
        On<OrderPlaced>((_, ctx) => LedgerV1.Row(probe, ctx, "ledger-orders"));
        On<ItemAdded>((_, ctx) => LedgerV1.Row(probe, ctx, "ledger-items"));
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        await LedgerV1.Clear(_probe, context, "ledger-orders");
        await LedgerV1.Clear(_probe, context, "ledger-items");
    }
}

public abstract class Round4ReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private async Task<InstanceRow?> Instance(Microsoft.Extensions.Hosting.IHost host, Guid id)
    {
        await using var connection = await OpenConnection();
        return (await RuntimeOf(host).Provider.ReadInstances(connection, null, TimeSpan.FromSeconds(6), Ct)).FirstOrDefault(i => i.Id == id);
    }

    [Fact]
    public async Task A_rebuild_that_the_old_version_runs_while_the_new_version_is_live_does_not_make_the_new_version_apply_an_event_twice()
    {
        // A rolling deploy: the old version handles orders, the new one also handles added items. A pod of the new
        // version runs the projection, so the old one leaves the checkpoint to it.
        var probe = NewProbe();
        var old = await StartHost(probe, b => b.Projection<LedgerV1>("ledger", Run.Async));
        var first = await StartHost(probe, b => b.Projection<LedgerV2>("ledger", Run.Async));
        await StoreOf(first).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StoreOf(first).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("cart-1")]);
        await WaitForCaughtUp(first, "ledger");
        Assert.Equal(1, await AppliedCount("ledger-items"));
        Assert.Equal(1, await AppliedCount("ledger-orders"));

        // A second pod of the new version is live; its runner is off, so the old pod is the only one that takes jobs.
        var next = await StartHost(probe, b => b.Projection<LedgerV2>("ledger", Run.Async), o => o.Enabled = false);
        await StopHost(first);

        // The operator rebuilds the projection. The old version's reset does not know the rows of the new version, so
        // the old pod leaves the job to the new version, and the job waits while that pod's runner is off.
        var job = await Enqueue(old, Jobs.Rebuild, new JsonObject { ["projection"] = "ledger" });
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(JobState.Queued, (await old.Services.GetRequiredService<IEventStoreAdmin>().GetJobAsync(job, Ct))!.Status);
        Assert.True((await Instance(old, RuntimeOf(next).InstanceId))?.Live, "The new instance is live while the job waits.");
        var itemsAfterReset = await AppliedCount("ledger-items");

        // The deploy finishes: only the new version runs, and it replays the events.
        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(probe, b => b.Projection<LedgerV2>("ledger", Run.Async));
        Assert.Equal("done", (await WaitForJob(after, job)).Status);
        var head = await Head(after);
        await WaitForCheckpoint(after, "ledger", r => r.Status == "stalled" || (r.Status == "running" && r.Position >= head));

        var row = await Checkpoint(after, "ledger");
        Assert.True(row.Status == "running" && await AppliedCount("ledger-items") == 1,
            $"The rebuild ran the reset of the old version while an instance of the new version was live, and left {itemsAfterReset} row of cart.item_added in place. " +
            $"The replay by the new version then applied that event a second time. The checkpoint is {row.Status} at position {row.Position} of {head}: {row.Error}");
    }
}
