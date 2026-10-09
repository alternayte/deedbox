using System.Data.Common;
using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Runner;

public sealed class PostgresRound2ReviewTests(Databases databases) : Round2ReviewTests(databases, Db.Postgres);

public sealed class SqlServerRound2ReviewTests(Databases databases) : Round2ReviewTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiRound2ReviewTests(Databases databases) : Round2ReviewTests(databases, Db.SqlServerRcsi);

/// <summary>Switches for the consumers of these tests.</summary>
public sealed class Round2
{
    public TaskCompletionSource ResetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ResetRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Resets;
}

/// <summary>Handles orders. Its reset waits for the test, then fails, as a reset does whose table is gone.</summary>
public sealed class OrdersWithFailingReset : Projection
{
    private readonly Round2 _switches;

    public OrdersWithFailingReset(Probe probe, Round2 switches)
    {
        _switches = switches;
        On<OrderPlaced>((_, ctx) => TestTables.Insert(ctx.Connection, ctx.Transaction,
            $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
            ("id", ctx.EventId.ToString()), ("projection", "p"), ("position", ctx.GlobalPosition ?? -1)));
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        Interlocked.Increment(ref _switches.Resets);
        _switches.ResetReached.TrySetResult();
        await _switches.ResetRelease.Task;
        throw new InvalidOperationException("The reset fails.");
    }
}

/// <summary>
/// Writes one row per application of an event, so a second application shows as a second row. Its reset deletes the
/// rows, then runs a statement that the database refuses and ignores the error.
/// </summary>
public sealed class ResetIgnoresAnError : Projection
{
    private readonly Probe _probe;

    public ResetIgnoresAnError(Probe probe)
    {
        _probe = probe;
        On<ItemAdded>((_, ctx) => TestTables.Insert(ctx.Connection, ctx.Transaction,
            $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
            ("id", Guid.NewGuid().ToString()), ("projection", "reset"), ("position", ctx.GlobalPosition ?? -1)));
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        await TestTables.Insert(context.Connection, context.Transaction, $"DELETE FROM {_probe.Table("applied")} WHERE projection = 'reset'");
        try
        {
            await TestTables.Insert(context.Connection, context.Transaction, "SELECT CAST('x' AS int)");
        }
        catch (DbException)
        {
            // An optional clean-up step; the reset goes on without it.
        }
    }
}

/// <summary>The deployed version of a projection that can be rebuilt: it handles orders only.</summary>
public sealed class RebuiltV1 : Projection
{
    public RebuiltV1(Review review) => On<OrderPlaced>((_, ctx) => ShippedV1.Saw(review, "v1", ctx, "order.order_placed"));

    protected override Task ResetAsync(WriteContext context) => Task.CompletedTask;
}

/// <summary>The next version of <see cref="RebuiltV1"/>, under the same name: it also handles added items.</summary>
public sealed class RebuiltV2 : Projection
{
    public RebuiltV2(Review review)
    {
        On<OrderPlaced>((_, ctx) => ShippedV1.Saw(review, "v2", ctx, "order.order_placed"));
        On<ItemAdded>((_, ctx) => ShippedV1.Saw(review, "v2", ctx, "cart.item_added"));
    }

    protected override Task ResetAsync(WriteContext context) => Task.CompletedTask;
}

public abstract class Round2ReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private async Task<int> EventuallyApplied(string label, int expected, int seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var count = await AppliedCount(label);
        while (count != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
            count = await AppliedCount(label);
        }

        return count;
    }

    [Fact]
    public async Task A_rebuild_that_an_instance_of_the_old_version_runs_does_not_let_it_move_the_projection_past_an_event_of_the_new_version()
    {
        // A rolling deploy, as in round 1: the old version handles orders, the new one also handles added items.
        var review = new Review();
        var old = await StartHost(NewProbe(), b => b.Projection<RebuiltV1>("shipped", Run.Async), services: s => s.AddSingleton(review));
        var next = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), o => o.Enabled = false, services: s => s.AddSingleton(review));
        await StoreOf(next).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StoreOf(next).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("ada")]);

        // The operator rebuilds the projection during the deploy. Both instances register the name. The old one leaves
        // the job to the new one, whose runner is off here, so the job waits.
        var before = await Checkpoint(old, "shipped");
        var job = await Enqueue(old, Jobs.Rebuild, new JsonObject { ["projection"] = "shipped" });
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(JobState.Queued, (await old.Services.GetRequiredService<IEventStoreAdmin>().GetJobAsync(job, Ct))!.Status);
        var rebuilt = await Checkpoint(old, "shipped");

        // Time for the old instance's loop, in case it runs the catch-up.
        try
        {
            await WaitFor(async () => (await Checkpoint(old, "shipped")).Position >= 2, "the old instance to run the catch-up", seconds: 5);
        }
        catch (TimeoutException)
        {
        }

        var moved = await Checkpoint(old, "shipped");

        // The deploy finishes: only the new version runs.
        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), services: s => s.AddSingleton(review));
        Assert.Equal("done", (await WaitForJob(after, job)).Status);
        await WaitForCaughtUp(after, "shipped");

        Assert.True(review.Seen.Any(s => s.Position == 1),
            $"No version applied cart.item_added at position 1. The checkpoint recorded {before.Handles} before the rebuild and {rebuilt.Handles} after it. " +
            $"With both instances live, the checkpoint moved to {moved.Position} ({moved.Status}). Applied: {string.Join(", ", review.Seen)}.");
    }

    [Fact]
    public async Task An_instance_that_joins_while_a_failing_rebuild_holds_the_row_of_an_inline_projection_does_not_append_past_the_projection()
    {
        var probe = NewProbe();
        var switches = new Round2();

        // An app with carts and an inline projection of them.
        var carts = await StartHost(probe, b => b.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()).Projection<InlineApplied>("q", Run.Inline),
            o => o.Enabled = false, defaultStreams: false);
        var provider = RuntimeOf(carts).Provider;

        // A second app appends carts and orders and runs no projection. Its join reads the checkpoints, then waits
        // for the gate of the cart projection, which the test holds.
        await using var gate = await OpenConnection();
        await using var gated = await gate.BeginTransactionAsync(Ct);
        await provider.LockInlineGate(gate, gated, "q", Ct);
        var waiting = Db == Db.Postgres
            ? "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted"
            : "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT'";
        var joining = StartHost(probe, _ => { }, o => o.Enabled = false);
        await WaitFor(async () => await Scalar<int>(waiting) > 0, "the join to wait for the gate");

        // Meanwhile a third app starts with a new inline projection of orders. No instance with a heartbeat row
        // skips it, so it starts running.
        var orders = await StartHost(probe, b => b.Stream<Order>(s => s.Events<OrderPlaced>()).Projection<OrdersWithFailingReset>("p", Run.Inline),
            defaultStreams: false, services: s => s.AddSingleton(switches));
        Assert.Equal(("running", "inline"), ((await Checkpoint(orders, "p")).Status, (await Checkpoint(orders, "p")).Mode));

        // A rebuild of the new projection starts. It holds the gate and the checkpoint row while its reset runs.
        var job = await Enqueue(orders, Jobs.Rebuild, new JsonObject { ["projection"] = "p" });
        await switches.ResetReached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        // The join goes on. It either joins now, or waits for the rebuild.
        await gated.RollbackAsync(Ct);
        await Task.WhenAny(joining, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        var joinedDuringReset = joining.IsCompleted;

        // The reset fails, each time, so the rebuild rolls back and the job fails.
        switches.ResetRelease.TrySetResult();
        Assert.Equal("failed", (await WaitForJob(orders, job)).Status);
        var other = await joining;
        var joined = await Checkpoint(orders, "p");

        await StoreOf(other).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("x")]);

        var applied = await EventuallyApplied("p", 1);
        var now = await Checkpoint(orders, "p");
        Assert.True(applied == 1,
            $"The order that the second app appended was never applied to 'p': {applied} of 1. The join finished during the reset: {joinedDuringReset}. " +
            $"After the join and the failed rebuild the checkpoint was '{joined.Status}' ({joined.Mode}) at {joined.Position}; it is '{now.Status}' at {now.Position} now, with the head at {await Head(orders)}.");
    }

    [Fact]
    public async Task A_failed_append_leaves_no_write_of_another_inline_projection()
    {
        // The first projection ignores an error with which the database rolled the transaction back. The append fails.
        // The second projection ran after that, for the same event.
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<IgnoresBadRows>("first", Run.Inline).Projection<InlineApplied>("inline", Run.Inline),
            services: s => s.AddSingleton(new Review()));

        await Assert.ThrowsAnyAsync<Exception>(() => StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("bad", 1)]));

        var events = await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}");
        var applied = await AppliedCount("inline");
        Assert.True(events == 0 && applied == 0, $"The append failed and stored {events} events, but the second inline projection kept {applied} row for its event.");
    }

    [Fact]
    public async Task A_rebuild_whose_reset_ignores_an_error_that_ended_the_transaction_applies_no_event_twice()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<ResetIgnoresAnError>("reset", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        await WaitForCaughtUp(host, "reset");
        Assert.Equal(2, await AppliedCount("reset"));

        var job = await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "reset" }));

        // Whatever the job says, the projection must end with one application of each event.
        await WaitForCheckpoint(host, "reset", r => r.Status != "rebuilding");
        var row = await Checkpoint(host, "reset");
        var applied = await AppliedCount("reset");
        Assert.True(applied == 2, $"The store holds 2 events, and the projection applied them {applied} times. The job is '{job.Status}'; the checkpoint is '{row.Status}' at {row.Position}.");
    }
}
