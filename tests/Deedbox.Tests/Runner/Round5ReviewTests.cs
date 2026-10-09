using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Runner;

public sealed class PostgresRound5ReviewTests(Databases databases) : Round5ReviewTests(databases, Db.Postgres);

public sealed class SqlServerRound5ReviewTests(Databases databases) : Round5ReviewTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiRound5ReviewTests(Databases databases) : Round5ReviewTests(databases, Db.SqlServerRcsi);

/// <summary>Holds a reset open, as a reset that deletes a large table is open for a while.</summary>
public sealed class ResetGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary><see cref="LedgerV1"/> with a reset that takes time.</summary>
public sealed class SlowLedgerV1 : Projection
{
    private readonly Probe _probe;
    private readonly ResetGate _gate;

    public SlowLedgerV1(Probe probe, ResetGate gate)
    {
        _probe = probe;
        _gate = gate;
        On<OrderPlaced>((_, ctx) => LedgerV1.Row(probe, ctx, "ledger-orders"));
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        _gate.Entered.TrySetResult();
        await _gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(120), context.CancellationToken);
        await LedgerV1.Clear(_probe, context, "ledger-orders");
    }
}

public abstract class Round5ReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private async Task<InstanceRow?> Instance(Microsoft.Extensions.Hosting.IHost host, Guid id)
    {
        await using var connection = await OpenConnection();
        return (await RuntimeOf(host).Provider.ReadInstances(connection, null, TimeSpan.FromSeconds(6), Ct)).FirstOrDefault(i => i.Id == id);
    }

    [Fact]
    public async Task A_new_version_that_joins_while_the_old_version_resets_does_not_apply_an_event_twice()
    {
        // A rolling deploy in which the pods of the new version restart: a pod of the new version applied the events,
        // and stopped. For a moment only the old version is live.
        var probe = NewProbe();
        var gate = new ResetGate();
        var old = await StartHost(probe, b => b.Projection<SlowLedgerV1>("ledger", Run.Async), services: s => s.AddSingleton(gate));
        var first = await StartHost(probe, b => b.Projection<LedgerV2>("ledger", Run.Async));
        await StoreOf(first).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StoreOf(first).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("cart-1")]);
        await WaitForCaughtUp(first, "ledger");
        Assert.Equal(1, await AppliedCount("ledger-items"));
        Assert.Equal(1, await AppliedCount("ledger-orders"));
        await StopHost(first);

        // The old pod takes the rebuild: no instance of the new version is live when it looks. Its reset is under way.
        var job = await Enqueue(old, Jobs.Rebuild, new JsonObject { ["projection"] = "ledger" });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        // A pod of the new version starts while the reset runs.
        var starting = StartHost(probe, b => b.Projection<LedgerV2>("ledger", Run.Async));
        var joined = await Task.WhenAny(starting, Task.Delay(TimeSpan.FromSeconds(20), Ct)) == starting;
        if (!joined)
        {
            // The join waits for the checkpoint row that the rebuild holds, so the new version is not live before the
            // reset ends, and the interleaving cannot happen on this database.
            gate.Release.TrySetResult();
            await starting.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(Db.SqlServer, Db);
            return;
        }

        var next = await starting;
        Assert.True((await Instance(old, RuntimeOf(next).InstanceId))?.Live, "The new instance is live while the old version's reset runs.");
        Assert.Equal(JobState.Queued, (await old.Services.GetRequiredService<IEventStoreAdmin>().GetJobAsync(job, Ct))!.Status);

        // The reset of the old version ends. It did not clear the rows of the new version.
        gate.Release.TrySetResult();
        var done = await WaitForJob(next, job);
        var itemsAfterReset = await AppliedCount("ledger-items");
        var head = await Head(next);
        await WaitForCheckpoint(next, "ledger", r => r.Status == "stalled" || (r.Status == "running" && r.Position >= head));

        var row = await Checkpoint(next, "ledger");
        Assert.True(row.Status == "running" && await AppliedCount("ledger-items") == 1,
            $"The rebuild job is {done.Status}: the old version ran its reset to the end while an instance of the new version was live, and left {itemsAfterReset} row of cart.item_added in place. " +
            $"The replay by the new version then applied that event a second time. The checkpoint is {row.Status} at position {row.Position} of {head}: {row.Error}");
    }
}
