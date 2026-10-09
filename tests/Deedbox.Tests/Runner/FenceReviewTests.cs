using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deedbox.Tests.Runner;

public sealed class PostgresFenceReviewTests(Databases databases) : FenceReviewTests(databases, Db.Postgres);

public sealed class SqlServerFenceReviewTests(Databases databases) : FenceReviewTests(databases, Db.SqlServer);

/// <summary>Handles cart items and the deletion of any stream. One row per applied event.</summary>
public sealed class InlineItemsAndDeletions : Projection
{
    public InlineItemsAndDeletions(Probe probe)
    {
        On<ItemAdded>((_, ctx) => Insert(probe, ctx));
        On<StreamDeleted>((_, ctx) => Insert(probe, ctx));
    }

    private static Task Insert(Probe probe, ProjectionContext ctx) => TestTables.Insert(ctx.Connection, ctx.Transaction,
        $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
        ("id", ctx.EventId.ToString()), ("projection", "deletions"), ("position", ctx.GlobalPosition ?? -1));
}

internal sealed class RecordingLogger : ILogger
{
    public ConcurrentQueue<int> Events { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Events.Enqueue(eventId.Id);
}

/// <summary>Reproductions from the review of the fence and the inline cut-over.</summary>
public abstract class FenceReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private async Task<int> EventuallyApplied(string label, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var count = await AppliedCount(label);
        while (count != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
            count = await AppliedCount(label);
        }

        return count;
    }

    [Fact]
    public async Task An_evicted_instance_that_registers_the_projection_async_does_not_append_past_it_after_it_went_inline()
    {
        // The old version registers the projection async. It joins and never beats again, as a paused process does.
        var probe = NewProbe();
        var old = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async), runner: o =>
        {
            o.HeartbeatInterval = TimeSpan.FromHours(1);
            o.Enabled = false;
        });
        await StoreOf(old).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);

        // The next version registers it inline: the mode change stalls it, and the operator rebuilds it.
        var next = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Inline));
        Assert.Equal("mode_changed", JsonNode.Parse((await Checkpoint(next, "applied")).Error!)!["reason"]!.GetValue<string>());
        Assert.Equal("done", (await WaitForJob(next, await Enqueue(next, Jobs.Rebuild, new JsonObject { ["projection"] = "applied" }))).Status);

        // The cut-over waits for the old instance while it is live, then evicts it and goes inline.
        await WaitForCheckpoint(next, "applied", r => r is { Status: "running", Mode: "inline" });
        Assert.Equal(1, await AppliedCount("async"));

        // The old instance wakes up and appends. It joins again; nothing moves the projection back or stalls it.
        await StoreOf(old).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);
        var after = await Checkpoint(next, "applied");

        await StopHost(old);
        await WaitForCheckpoint(next, "applied", r => r.Status != "rebuilding");
        var applied = await EventuallyApplied("async", 2);
        Assert.True(applied == 2 || (await Checkpoint(next, "applied")).Status == "stalled",
            $"The event that the evicted instance appended was never applied: {applied} of 2 events applied; the checkpoint was '{after.Status}' ({after.Mode}) right after the append and is '{(await Checkpoint(next, "applied")).Status}' now.");
    }

    [Fact]
    public async Task An_instance_that_deletes_streams_of_a_type_the_projection_has_no_events_of_does_not_skip_the_projection()
    {
        // The projection handles StreamDeleted on every stream, and here it sees the deletion of an order stream.
        var probe = NewProbe();
        var owner = await StartHost(probe, b => b.Projection<InlineItemsAndDeletions>("deletions", Run.Inline));
        await StoreOf(owner).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("x")]);
        await StoreOf(owner).Append("order-2", ExpectedVersion.Any, [new OrderPlaced("y")]);
        await StoreOf(owner).DeleteStream("order-1");
        Assert.Equal(1, await AppliedCount("deletions"));

        // Another app registers only orders. Its deletion of an order stream must reach the projection too.
        var other = await StartHost(probe, b => b.Stream<Order>(s => s.Events<OrderPlaced>()), defaultStreams: false);
        var joined = await Checkpoint(owner, "deletions");
        await StoreOf(other).DeleteStream("order-2");

        await StopHost(other);
        await WaitForCheckpoint(owner, "deletions", r => r.Status == "running");
        var applied = await EventuallyApplied("deletions", 2);
        Assert.True(applied == 2, $"The StreamDeleted that the other app appended was never applied: {applied} of 2. The checkpoint was '{joined.Status}' after the other app joined.");
    }

    [Fact]
    public async Task An_instance_that_registers_a_stream_type_without_events_does_not_skip_the_projection_when_it_deletes_a_stream()
    {
        // A clean-up tool: it registers the stream type only to delete its streams. It runs before the store holds a cart.
        var probe = NewProbe();
        var tool = await StartHost(probe, b => b.Stream<Cart>(_ => { }), defaultStreams: false);
        var owner = await StartHost(probe, b => b.Projection<InlineItemsAndDeletions>("deletions", Run.Inline));
        var joined = await Checkpoint(owner, "deletions");
        await StoreOf(owner).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await WaitFor(async () => (await Checkpoint(owner, "deletions")).Position >= 1 || await AppliedCount("deletions") == 1, "the first event");
        await StoreOf(tool).DeleteStream("cart-1");

        await StopHost(tool);
        await WaitForCheckpoint(owner, "deletions", r => r.Status == "running");
        var applied = await EventuallyApplied("deletions", 2);
        Assert.True(applied == 2, $"The StreamDeleted that the tool appended was never applied: {applied} of 2. The checkpoint was '{joined.Status}' after the tool joined.");
    }

    [Fact]
    public async Task An_append_that_applies_an_inline_projection_does_not_wait_for_the_rebuild_of_another_projection()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline).Projection<AsyncApplied>("applied", Run.Async), o => o.Enabled = false);
        var provider = RuntimeOf(host).Provider;

        // A rebuild holds its checkpoint row like this while its reset runs.
        await using var rebuild = await OpenConnection();
        await using var rebuilding = await rebuild.BeginTransactionAsync(Ct);
        await provider.LockCheckpoint(rebuild, rebuilding, "applied", CheckpointLock.Exclusive, Ct);

        var append = StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        var finished = await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(8), Ct)) == append;
        await rebuilding.RollbackAsync(Ct);
        await append;

        Assert.True(finished, "The append waited for the checkpoint row of a projection that it does not apply.");
    }

    [Fact]
    public async Task A_join_does_not_hold_the_position_counter_while_it_waits_for_a_rebuild_of_another_projection()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline).Projection<AsyncApplied>("applied", Run.Async), o => o.Enabled = false);
        var provider = RuntimeOf(host).Provider;
        Assert.Equal("running", (await Checkpoint(host, "inline")).Status);

        // The gate is held so that the join has read the checkpoints once and waits before its locks.
        await using var gate = await OpenConnection();
        await using var gated = await gate.BeginTransactionAsync(Ct);
        await provider.LockInlineGate(gate, gated, "inline", Ct);
        var waiting = Db == Db.Postgres
            ? "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted"
            : "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT'";
        var joining = StartHost(probe, _ => { });
        await WaitFor(async () => await Scalar<int>(waiting) > 0, "the join to wait for the gate");

        // A rebuild of the other projection starts: it holds its checkpoint row while its reset runs.
        await using var rebuild = await OpenConnection();
        await using var rebuilding = await rebuild.BeginTransactionAsync(Ct);
        await provider.LockCheckpoint(rebuild, rebuilding, "applied", CheckpointLock.Exclusive, Ct);
        await gated.RollbackAsync(Ct);
        await WaitFor(async () => await Scalar<int>(waiting) == 0, "the join to take the gate");
        await Task.Delay(500, Ct);

        // An append that applies no inline projection needs only the counter.
        var append = StoreOf(host).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("x")]);
        var finished = await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(8), Ct)) == append;
        await rebuilding.RollbackAsync(Ct);
        await append;
        await joining;

        Assert.True(finished, "The append waited for the position counter, which the join held while it waited for the checkpoint row of a rebuild.");
    }

    [Fact]
    public async Task A_new_catch_up_does_not_start_with_a_forced_cut_over_left_from_an_earlier_one()
    {
        var probe = NewProbe();
        var before = await StartHost(probe, _ => { });
        for (var i = 0; i < 20; i++)
            await StoreOf(before).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StopHost(before);

        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline), runner: o =>
        {
            o.Enabled = false;
            o.BatchSize = 3;
            o.CutOverHold = TimeSpan.Zero;
        });
        var log = new RecordingLogger();
        var loop = new ConsumerLoop(AsyncRunner.Consumers(RuntimeOf(host), host.Services.GetRequiredService<ProjectionSet>()).Single(),
            RuntimeOf(host), host.Services, new WakeSignal(), log);
        int Forced() => log.Events.Count(id => id == 25);

        // Appends keep pace with the catch-up for 20 ticks, so the 21st forces a cut-over, which runs out of time.
        for (var tick = 1; tick <= 21; tick++)
        {
            await StoreOf(host).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1), new ItemAdded("b", 1), new ItemAdded("b", 1)]);
            Assert.Equal(Outcome.Progress, await loop.Tick(Ct));
        }

        Assert.Equal(1, Forced());
        Assert.Equal("rebuilding", (await Checkpoint(host, "inline")).Status);

        // The operator rebuilds the projection. No append runs now, so this catch-up has no reason to hold the counter.
        var id = await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "inline" });
        var jobs = new JobLoop(RuntimeOf(host), host.Services, host.Services.GetRequiredService<AsyncRunner>(), new WakeSignal(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.True(await jobs.RunOne(Ct));
        Assert.Equal("done", (await WaitForJob(host, id)).Status);
        Assert.Equal(("rebuilding", 0L), ((await Checkpoint(host, "inline")).Status, (await Checkpoint(host, "inline")).Position));

        for (var tick = 1; tick <= 12; tick++)
        {
            await Task.Delay(30, Ct);
            Assert.Equal(Outcome.Progress, await loop.Tick(Ct));
        }

        var row = await Checkpoint(host, "inline");
        Assert.True(Forced() == 1 && row.Error is null,
            $"The new catch-up forced {Forced() - 1} cut-overs with no append running; checkpoint error: {row.Error ?? "none"}; health: {(await Health(host)).Status}.");
    }
}
