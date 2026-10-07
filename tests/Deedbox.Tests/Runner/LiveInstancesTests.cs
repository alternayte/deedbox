using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Runner;

public sealed class PostgresLiveInstancesTests(Databases databases) : LiveInstancesTests(databases, Db.Postgres);

public sealed class SqlServerLiveInstancesTests(Databases databases) : LiveInstancesTests(databases, Db.SqlServer);

public abstract class LiveInstancesTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private static IEventStoreAdmin AdminOf(IHost host) => host.Services.GetRequiredService<IEventStoreAdmin>();

    [Fact]
    public async Task A_new_inline_projection_stays_in_catch_up_while_an_old_instance_appends_its_events_then_goes_inline()
    {
        var probe = NewProbe();
        var old = await StartHost(probe, _ => { });
        await StoreOf(old).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);

        var next = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));
        await StoreOf(old).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1), new ItemAdded("c", 1)]);
        await WaitForCheckpoint(next, "inline", r => r.Position >= 3);
        await Task.Delay(500, Ct);
        Assert.Equal("rebuilding", (await Checkpoint(next, "inline")).Status);

        await StopHost(old);
        await WaitForCaughtUp(next, "inline");
        await StoreOf(next).Append("cart-3", ExpectedVersion.Any, [new ItemAdded("d", 1)]);

        Assert.Equal(4, await AppliedCount("inline"));
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("applied")} WHERE projection = 'inline' AND position = -1"));
    }

    [Fact]
    public async Task An_instance_whose_heartbeat_is_late_cannot_append_past_a_projection_that_went_inline_without_it()
    {
        // The old instance joins and then never beats again, as a paused or starved process does. It still appends.
        var probe = NewProbe();
        var old = await StartHost(probe, _ => { }, runner: o => o.HeartbeatInterval = TimeSpan.FromHours(1));
        await StoreOf(old).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);

        // After the liveness window the cut-over no longer waits for it; it evicts it.
        var next = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));
        await WaitForCheckpoint(next, "inline", r => r.Status == "running");

        // A transaction that Deedbox owns: the instance joins again, which moves the projection back, and the append runs.
        await StoreOf(old).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);
        Assert.Equal("rebuilding", (await Checkpoint(next, "inline")).Status);

        // The caller's transaction cannot be replayed, so its append is refused.
        await WaitForCheckpoint(next, "inline", r => r.Status == "running");
        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            var refused = await Assert.ThrowsAsync<DeedboxException>(() =>
                StoreOf(old).UseTransaction(transaction).Append("cart-3", ExpectedVersion.Any, [new ItemAdded("c", 1)]));
            Assert.Equal("DBX038", refused.Code);
            await transaction.RollbackAsync(Ct);
        }

        // It joined again in the background, so the caller's second try passes.
        await WaitForCheckpoint(next, "inline", r => r.Status == "rebuilding");

        // A DbContext passed to UseDbContext is the caller's work too: its changes were saved in the transaction that
        // was rolled back, so a second try by Deedbox would commit the events without them.
        await WaitForCheckpoint(next, "inline", r => r.Status == "running");
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OrdersDb>();
        options.Use(Db, ConnectionString);
        await using (var orders = new OrdersDb(options.Options))
        {
            orders.Orders.Add(new OrderRow { Id = probe.Prefix + "refused", Note = "placed" });
            var refused = await Assert.ThrowsAsync<DeedboxException>(() =>
                StoreOf(old).UseDbContext(orders).Append("cart-9", ExpectedVersion.Any, [new ItemAdded("x", 1)]));
            Assert.Equal("DBX038", refused.Code);
        }

        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM ef_tests.orders WHERE id = '{probe.Prefix}refused'"));
        Assert.Equal(0, (await StoreOf(next).Load<Cart>("cart-9")).Version);
        await WaitForCheckpoint(next, "inline", r => r.Status == "rebuilding");
        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await StoreOf(old).UseTransaction(transaction).Append("cart-3", ExpectedVersion.Any, [new ItemAdded("c", 1)]);
            await transaction.CommitAsync(Ct);
        }

        await StopHost(old);
        await WaitForCaughtUp(next, "inline");
        await StoreOf(next).Append("cart-4", ExpectedVersion.Any, [new ItemAdded("d", 1)]);

        // Every event exactly once: the table's primary key is the event ID.
        Assert.Equal(4, await AppliedCount("inline"));
    }

    [Fact]
    public async Task A_projection_that_starts_to_handle_an_event_another_app_appends_catches_up_until_that_app_stops()
    {
        // The other app appends only orders. The inline projection does not handle them yet, so nothing is skipped.
        var probe = NewProbe();
        var first = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));
        var other = await StartHost(probe, b => b.Stream<Order>(s => s.Events<OrderPlaced>()), defaultStreams: false);
        await StoreOf(first).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        Assert.Equal("running", (await Checkpoint(first, "inline")).Status);
        await StopHost(first);

        // The next version handles orders. The other app would skip it for each order it appends, so it catches up.
        var next = await StartHost(probe, b => b.Projection<InlineAppliedWithOrders>("inline", Run.Inline));
        Assert.Equal("rebuilding", (await Checkpoint(next, "inline")).Status);
        await StoreOf(other).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("x")]);
        await WaitForCheckpoint(next, "inline", r => r.Position >= 2);

        await StopHost(other);
        await WaitForCaughtUp(next, "inline");
        Assert.Equal(2, await AppliedCount("inline"));
    }

    [Fact]
    public async Task A_process_that_appends_without_a_started_host_creates_the_checkpoint_so_nothing_is_applied_twice()
    {
        var probe = NewProbe();
        var hostless = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline), start: false);
        await SchemaManager.Apply(RuntimeOf(hostless).Provider, Ct);
        await StoreOf(hostless).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        Assert.Equal(("running", 1), ((await Checkpoint(hostless, "inline")).Status, await AppliedCount("inline")));

        // A hosted instance finds the checkpoint; without it, it would create one in catch-up and apply the event again.
        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("b", 1)]);
        await Task.Delay(500, Ct);

        Assert.Equal(("running", 2), ((await Checkpoint(host, "inline")).Status, await AppliedCount("inline")));
    }

    [Fact]
    public async Task A_cut_over_that_runs_out_of_time_keeps_its_progress_and_gives_the_counter_back()
    {
        var probe = NewProbe();
        var before = await StartHost(probe, _ => { });
        for (var i = 0; i < 20; i++)
            await StoreOf(before).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StopHost(before);

        // No time at all on the counter: each attempt applies its one guaranteed batch and stops.
        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline), runner: o =>
        {
            o.Enabled = false;
            o.BatchSize = 3;
            o.CutOverHold = TimeSpan.Zero;
        });
        var loop = new ConsumerLoop(AsyncRunner.Consumers(RuntimeOf(host), host.Services.GetRequiredService<ProjectionSet>()).Single(),
            RuntimeOf(host), host.Services, new WakeSignal(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        async Task<CutOverResult> Attempt()
        {
            await using var connection = await OpenConnection();
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            var row = (await RuntimeOf(host).Provider.LockCheckpoint(connection, transaction, "inline", CheckpointLock.Batch, Ct))!;
            var result = await loop.CutOver(connection, transaction, row, Ct);
            await transaction.CommitAsync(Ct);
            return result;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
            Assert.Equal(CutOverResult.OutOfTime, await Attempt());

        // The counter is free again, and the projection is still in catch-up, so this append is not applied inline.
        await StoreOf(host).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);
        var slow = await Checkpoint(host, "inline");
        Assert.Equal(("rebuilding", 15L, 15), (slow.Status, slow.Position, await AppliedCount("inline")));
        Assert.Equal(HealthStatus.Degraded, (await Health(host)).Status);

        Assert.Equal(CutOverResult.OutOfTime, await Attempt());
        Assert.Equal(CutOverResult.Done, await Attempt());
        var done = await Checkpoint(host, "inline");
        Assert.Equal(("running", 21L, null, 21), (done.Status, done.Position, done.Error, await AppliedCount("inline")));
        Assert.Equal(HealthStatus.Healthy, (await Health(host)).Status);
    }

    [Fact]
    public async Task An_old_instance_that_starts_moves_an_inline_projection_back_to_catch_up_and_misses_none_of_its_appends()
    {
        var probe = NewProbe();
        var next = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));
        await StoreOf(next).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        Assert.Equal("running", (await Checkpoint(next, "inline")).Status);

        var old = await StartHost(probe, _ => { });
        var moved = await Checkpoint(next, "inline");
        Assert.Equal(("rebuilding", 1L), (moved.Status, moved.Position));
        await StoreOf(old).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1), new ItemAdded("c", 1)]);
        await WaitForCheckpoint(next, "inline", r => r.Position >= 3);

        await StopHost(old);
        await WaitForCaughtUp(next, "inline");
        Assert.Equal(3, await AppliedCount("inline"));
    }

    [Fact]
    public async Task An_instance_that_cannot_append_the_projections_events_does_not_hold_it_back()
    {
        var probe = NewProbe();
        await StartHost(probe, b => b.Stream<Counter>(s => s.Events<Incremented>()), defaultStreams: false);

        var next = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline));

        Assert.Equal("running", (await Checkpoint(next, "inline")).Status);
    }

    [Fact]
    public async Task Retiring_refuses_while_an_instance_registers_the_projection_then_parks_it_until_a_rebuild()
    {
        var probe = NewProbe();
        var user = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        var admin = await StartHost(probe, _ => { });
        await StoreOf(admin).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await WaitForCaughtUp(user, "applied");

        var refused = await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(admin).RetireAsync("applied"));
        Assert.Equal(Errors.ProjectionInUse, refused.Code);
        Assert.Contains(RuntimeOf(user).InstanceId.ToString("D"), refused.Message, StringComparison.Ordinal);
        Assert.Equal(Errors.UnknownConsumer, (await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(admin).RetireAsync("nope"))).Code);

        await StopHost(user);
        await AdminOf(admin).RetireAsync("applied");
        var retired = (await AdminOf(admin).GetStatusAsync()).Consumers.Single(c => c.Name == "applied");
        Assert.Equal((ConsumerState.Retired, 0L), (retired.Status, retired.Lag));
        await StoreOf(admin).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);

        // An instance that still registers it starts; the projection stays idle, and the health check says why.
        var back = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await Task.Delay(500, Ct);
        Assert.Equal(1, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Degraded, (await Health(back)).Status);

        Assert.Equal("done", (await WaitForJob(back, await AdminOf(back).RebuildAsync("applied"))).Status);
        await WaitForCaughtUp(back, "applied");
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Healthy, (await Health(back)).Status);
    }

    [Fact]
    public async Task A_rebuild_job_waits_for_a_live_instance_that_registers_the_projection_instead_of_failing()
    {
        var probe = NewProbe();
        var other = await StartHost(probe, _ => { });
        var registers = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async), o => o.Enabled = false);

        var id = await AdminOf(registers).RebuildAsync("applied");
        await Task.Delay(1000, Ct);
        Assert.Equal(JobState.Queued, (await AdminOf(other).GetJobAsync(id))!.Status);

        // With no live instance that registers it, the job runs where it is and fails with the reason.
        await StopHost(registers);
        var job = await WaitForJob(other, id);
        Assert.Equal("failed", job.Status);
        Assert.Contains("not a registered projection", job.Progress, StringComparison.Ordinal);
    }
}
