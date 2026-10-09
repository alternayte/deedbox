using System.Collections.Concurrent;
using System.Data.Common;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Deedbox.Tests.Runner;

public sealed class PostgresLoopReviewTests(Databases databases) : LoopReviewTests(databases, Db.Postgres);

public sealed class SqlServerLoopReviewTests(Databases databases) : LoopReviewTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiLoopReviewTests(Databases databases) : LoopReviewTests(databases, Db.SqlServerRcsi);

/// <summary>Switches and records for the consumers of these tests.</summary>
public sealed class Review
{
    public string BadStatement { get; set; } = "SELECT CAST('x' AS int)";

    public ConcurrentBag<DbDataReader> Readers { get; } = [];

    /// <summary>Runs inside each subscription handler call.</summary>
    public Func<Task>? WhileHandling { get; set; }

    public int Handled;

    public ConcurrentQueue<(string Version, string EventType, long Position)> Seen { get; } = new();
}

/// <summary>Holds the thread that writes one log event until the test lets it go, to fix the order of two instances.</summary>
public sealed class HoldLogEvent(int eventId) : ILoggerProvider, ILogger
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualResetEventSlim Release { get; } = new();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (id.Id != eventId)
            return;
        Reached.TrySetResult();
        Release.Wait(TimeSpan.FromSeconds(60));
    }

    public void Dispose() => Release.Set();
}

/// <summary>The deployed version of a consumer: it handles orders only.</summary>
public sealed class ShippingV1 : Subscription
{
    public ShippingV1(Review review) => On<OrderPlaced>((_, ctx) => Saw(review, "v1", ctx));

    internal static Task Saw(Review review, string version, SubscriptionContext ctx)
    {
        review.Seen.Enqueue((version, ctx.Envelope.EventType, ctx.Envelope.GlobalPosition));
        return Task.CompletedTask;
    }
}

/// <summary>The next version of <see cref="ShippingV1"/>, under the same name: it also handles added items.</summary>
public sealed class ShippingV2 : Subscription
{
    public ShippingV2(Review review)
    {
        On<OrderPlaced>((_, ctx) => ShippingV1.Saw(review, "v2", ctx));
        On<ItemAdded>((_, ctx) => ShippingV1.Saw(review, "v2", ctx));
    }
}

/// <summary>The projection form of <see cref="ShippingV1"/>.</summary>
public sealed class ShippedV1 : Projection
{
    public ShippedV1(Review review) => On<OrderPlaced>((_, ctx) => Saw(review, "v1", ctx, "order.order_placed"));

    internal static Task Saw(Review review, string version, ProjectionContext ctx, string eventType)
    {
        review.Seen.Enqueue((version, eventType, ctx.GlobalPosition ?? -1));
        return Task.CompletedTask;
    }
}

/// <summary>The next version of <see cref="ShippedV1"/>, under the same name.</summary>
public sealed class ShippedV2 : Projection
{
    public ShippedV2(Review review)
    {
        On<OrderPlaced>((_, ctx) => ShippedV1.Saw(review, "v2", ctx, "order.order_placed"));
        On<ItemAdded>((_, ctx) => ShippedV1.Saw(review, "v2", ctx, "cart.item_added"));
    }
}

/// <summary>A subscription whose handler succeeds; the test decides what happens to the database while it runs.</summary>
public sealed class Notifies : Subscription
{
    public Notifies(Review review) => On<ItemAdded>(async (_, _) =>
    {
        if (review.WhileHandling is { } act)
            await act();
        Interlocked.Increment(ref review.Handled);
    });
}

/// <summary>
/// Writes one row per applied event. For the event "bad" it runs a statement that the database refuses, and ignores
/// the error, as a handler does that skips rows it cannot convert.
/// </summary>
public sealed class IgnoresBadRows : Projection
{
    public IgnoresBadRows(Probe probe, Review review)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            if (e.Sku == "bad")
            {
                try
                {
                    await TestTables.Insert(ctx.Connection, ctx.Transaction, review.BadStatement);
                }
                catch (DbException)
                {
                    // The row is bad; the projection leaves it out.
                }

                return;
            }

            await TestTables.Insert(ctx.Connection, ctx.Transaction,
                $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
                ("id", ctx.EventId.ToString()), ("projection", "async"), ("position", ctx.GlobalPosition ?? -1));
        });
    }
}

/// <summary>Reads through a reader that it does not close for the event "open", and returns normally.</summary>
public sealed class LeavesReaderOpen : Projection
{
    public LeavesReaderOpen(Probe probe, Review review)
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            if (e.Sku == "open")
            {
                var command = ctx.Connection.CreateCommand();
                command.Transaction = ctx.Transaction;
                command.CommandText = $"SELECT event_id FROM {probe.Table("applied")}";
                review.Readers.Add(await command.ExecuteReaderAsync(ctx.CancellationToken));
                return;
            }

            await TestTables.Insert(ctx.Connection, ctx.Transaction,
                $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, @projection, @position)",
                ("id", ctx.EventId.ToString()), ("projection", "async"), ("position", ctx.GlobalPosition ?? -1));
        });
    }
}

public abstract class LoopReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Fact]
    public async Task A_checkpoint_never_passes_an_event_whose_writes_the_database_rolled_back()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<IgnoresBadRows>("applied", Run.Async), services: s => s.AddSingleton(new Review()));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1), new ItemAdded("bad", 1), new ItemAdded("c", 1)]);

        // Time for the batch and its retries. The consumer may pass the batch, stall, or keep failing; whatever it
        // does, each event at or before its checkpoint, other than the bad one, must have its row.
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        var row = await Checkpoint(host, "applied");
        var applied = await Scalar<int>($"SELECT COUNT(*) FROM {Table("applied")} WHERE projection = 'async' AND position <= {row.Position}");
        var expected = new long[] { 1, 2, 4 }.Count(p => p <= row.Position);

        Assert.True(expected == applied, $"The checkpoint is at {row.Position} ({row.Status}), so {expected} events are behind it, but {applied} have a row.");
    }

    [Fact]
    public async Task An_append_stores_nothing_when_an_inline_projection_ignores_an_error_that_ended_the_transaction()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<IgnoresBadRows>("applied", Run.Inline), services: s => s.AddSingleton(new Review()));

        await Assert.ThrowsAnyAsync<Exception>(() => StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("bad", 1)]));

        // On SQL Server the events would commit on their own, with no stream row.
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task A_handler_that_ends_the_transaction_and_returns_stalls_the_consumer_or_lets_it_move_on()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<IgnoresBadRows>("applied", Run.Async), services: s => s.AddSingleton(new Review()));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1), new ItemAdded("bad", 1), new ItemAdded("c", 1)]);

        // Two retries, 10 ms apart, then a stall: a second is ample. A consumer that is neither past the event nor
        // stalled after 20 seconds retries for ever, and no skip can move it on.
        CheckpointRow? last = null;
        try
        {
            await WaitFor(async () => (last = await Checkpoint(host, "applied")) is { Position: >= 3 } or { Status: "stalled" }, "the consumer to stall or move on", seconds: 20);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"After 20 seconds the consumer is at {last!.Position}, status '{last.Status}', error {last.Error ?? "null"}: it neither stalled nor moved on.");
        }
    }

    [Fact]
    public async Task A_handler_that_leaves_a_reader_open_and_returns_stalls_the_consumer_or_lets_it_move_on()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<LeavesReaderOpen>("applied", Run.Async), services: s => s.AddSingleton(new Review()));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("open", 1), new ItemAdded("c", 1)]);

        CheckpointRow? last = null;
        try
        {
            await WaitFor(async () => (last = await Checkpoint(host, "applied")) is { Position: >= 2 } or { Status: "stalled" }, "the consumer to stall or move on", seconds: 20);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"After 20 seconds the consumer is at {last!.Position}, status '{last.Status}', error {last.Error ?? "null"}: it neither stalled nor moved on.");
        }
    }

    [Fact]
    public async Task A_subscription_whose_session_is_lost_during_every_call_of_its_handler_stalls_after_StallAfter()
    {
        // The runner's transaction is idle while a subscription's handler runs. A database that ends such sessions,
        // as idle_in_transaction_session_timeout does, ends it on every attempt. The handler itself succeeds.
        var review = new Review();
        var app = "app-" + Schema;
        review.WhileHandling = async () =>
        {
            await using var connection = await OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = Db == Db.Postgres
                ? $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '{app}' AND state = 'idle in transaction'"
                : $"""
                   DECLARE @kill nvarchar(max) = N'';
                   SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions
                   WHERE program_name = '{app}' AND open_transaction_count > 0 AND session_id <> @@SPID;
                   EXEC(@kill);
                   """;
            await command.ExecuteNonQueryAsync(Ct);
        };
        var host = await StartHost(NewProbe(), b => b.Subscription<Notifies>("notifies"), o => o.StallAfter = TimeSpan.FromSeconds(1),
            applicationName: app, services: s => s.AddSingleton(review));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);

        CheckpointRow? last = null;
        try
        {
            await WaitFor(async () => (last = await Checkpoint(host, "notifies")) is { Position: >= 1 } or { Status: "stalled" }, "the subscription to stall or move on", seconds: 30);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"After 30 seconds, with StallAfter at 1 second, the subscription is at {last!.Position}, status '{last.Status}', and its handler ran {review.Handled} times: it neither stalled nor moved on.");
        }
    }

    [Fact]
    public async Task A_rebuild_job_for_a_subscription_fails_when_two_instances_register_the_subscription()
    {
        var probe = NewProbe();
        var first = await StartHost(probe, b => b.Subscription<Receipts>("receipts"));
        await StartHost(probe, b => b.Subscription<Receipts>("receipts"));

        // The CLI queues this without a check: deedbox rebuild receipts.
        var id = await Enqueue(first, Jobs.Rebuild, new JsonObject { ["projection"] = "receipts" });

        JobRow? job = null;
        try
        {
            await WaitFor(async () =>
            {
                await using var connection = await OpenConnection();
                job = await RuntimeOf(first).Provider.ReadJob(connection, id, Ct);
                return job?.Status is "done" or "failed";
            }, "the job to finish", seconds: 30);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"After 30 seconds, five liveness windows, the job is still '{job!.Status}'. One instance alone fails it at once: subscriptions cannot be rebuilt.");
        }

        Assert.Equal("failed", job!.Status);
    }

    [Fact]
    public async Task A_subscription_is_never_moved_past_an_event_that_its_new_version_handles_by_an_instance_of_the_old_version()
    {
        // A rolling deploy: the old version runs next to the new one, and both share the checkpoint. The new instance
        // appends an event that only the new version handles. Here the old instance's loop is the one that takes the
        // checkpoint, which the new instance's disabled runner makes certain.
        var review = new Review();
        var old = await StartHost(NewProbe(), b => b.Subscription<ShippingV1>("shipping"), services: s => s.AddSingleton(review));
        var next = await StartHost(NewProbe(), b => b.Subscription<ShippingV2>("shipping"), o => o.Enabled = false, services: s => s.AddSingleton(review));

        await StoreOf(next).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StoreOf(next).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("ada")]);

        // The old version leaves the checkpoint to the new one, whose runner is off here, so nothing moves it.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(0, (await Checkpoint(old, "shipping")).Position);

        // The deploy finishes: only the new version runs.
        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(NewProbe(), b => b.Subscription<ShippingV2>("shipping"), services: s => s.AddSingleton(review));
        await WaitForCaughtUp(after, "shipping");

        var row = await Checkpoint(after, "shipping");
        Assert.True(review.Seen.Any(s => s.Position == 1),
            $"The checkpoint is at {row.Position} and records the handled events {row.Handles}, but no version handled cart.item_added at position 1. Handled: {string.Join(", ", review.Seen)}.");
    }

    [Fact]
    public async Task An_async_projection_is_never_moved_past_an_event_that_its_new_version_handles_by_an_instance_of_the_old_version()
    {
        var review = new Review();
        var old = await StartHost(NewProbe(), b => b.Projection<ShippedV1>("shipped", Run.Async), services: s => s.AddSingleton(review));
        var next = await StartHost(NewProbe(), b => b.Projection<ShippedV2>("shipped", Run.Async), o => o.Enabled = false, services: s => s.AddSingleton(review));

        await StoreOf(next).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StoreOf(next).Append("order-1", ExpectedVersion.Any, [new OrderPlaced("ada")]);

        // The old version leaves the checkpoint to the new one, whose runner is off here, so nothing moves it.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(0, (await Checkpoint(old, "shipped")).Position);

        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(NewProbe(), b => b.Projection<ShippedV2>("shipped", Run.Async), services: s => s.AddSingleton(review));
        await WaitForCaughtUp(after, "shipped");

        var row = await Checkpoint(after, "shipped");
        Assert.True(review.Seen.Any(s => s.Position == 1),
            $"The checkpoint is at {row.Position} and records the handled events {row.Handles}, but no version applied cart.item_added at position 1. Applied: {string.Join(", ", review.Seen)}.");
    }

    [Fact]
    public async Task A_job_that_one_instance_rejected_and_another_instance_then_finished_stays_done()
    {
        // An instance without the projection, and no live instance with it, rejects the rebuild. It gives the job row
        // back before it records the failure. Log event 32, "job failed", is written between the two; the test holds
        // the instance there while an instance with the projection starts, takes the job and finishes it.
        var probe = NewProbe();
        var hold = new HoldLogEvent(32);
        var with = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(with).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await WaitForCaughtUp(with, "applied");
        await StopHost(with);

        var without = await StartHost(probe, _ => { }, services: s => s.AddLogging(l => l.AddProvider(hold)));
        var id = await Enqueue(without, Jobs.Rebuild, new JsonObject { ["projection"] = "applied" });
        await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var back = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await WaitFor(() => Task.FromResult(Volatile.Read(ref probe.Resets) == 1), "the rebuild on the instance with the projection");
        await WaitFor(async () =>
        {
            await using var connection = await OpenConnection();
            return (await RuntimeOf(back).Provider.ReadJob(connection, id, Ct))!.Status == "done";
        }, "the job to be done");

        hold.Release.Set();
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        await using var read = await OpenConnection();
        var job = (await RuntimeOf(back).Provider.ReadJob(read, id, Ct))!;
        Assert.True(job.Status == "done", $"The rebuild ran ({probe.Resets} reset) and the job was done, but its row now says '{job.Status}': {job.Progress}");
    }
}
