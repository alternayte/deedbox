using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Deedbox.Tests.Runner;

public sealed class PostgresRunnerTests(Databases databases) : RunnerTests(databases, Db.Postgres)
{
    [Fact]
    public async Task Notify_wakes_an_idle_runner_before_its_poll_delay()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async),
            o => (o.MinPollDelay, o.MaxPollDelay) = (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)));
        await Task.Delay(500, Ct);

        var appended = DateTime.UtcNow;
        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await WaitFor(async () => await AppliedCount("async") == 1, "the notified runner", seconds: 20);

        Assert.True(DateTime.UtcNow - appended < TimeSpan.FromSeconds(20));
    }
}

public sealed class SqlServerRunnerTests(Databases databases) : RunnerTests(databases, Db.SqlServer);

public abstract class RunnerTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Fact]
    public async Task An_async_projection_applies_each_committed_event_once_and_checkpoints_the_head()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        var store = StoreOf(host);
        for (var i = 0; i < 20; i++)
            await store.Append($"cart-{i % 4}", ExpectedVersion.Any, [new ItemAdded("a", 1), new CheckedOut(DateTimeOffset.UnixEpoch)]);

        await WaitForCaughtUp(host, "applied");

        Assert.Equal(20, await AppliedCount("async"));
        Assert.Equal(40L, (await Checkpoint(host, "applied")).Position);
        Assert.Equal("async", (await Checkpoint(host, "applied")).Mode);
    }

    [Fact]
    public async Task A_filtered_projection_advances_its_checkpoint_past_events_it_does_not_handle()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<OrdersOnly>("orders_only", Run.Async), o => o.BatchSize = 3);
        var store = StoreOf(host);
        for (var i = 0; i < 10; i++)
            await store.Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await store.Append("order-1", ExpectedVersion.Any, [new OrderPlaced("ada")]);
        for (var i = 0; i < 10; i++)
            await store.Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);

        await WaitForCaughtUp(host, "orders_only");

        var delivered = Assert.Single(probe.Delivered);
        Assert.Equal((11L, "order-1"), (delivered.Envelope.GlobalPosition, delivered.Envelope.StreamId));
        Assert.Equal(21L, (await Checkpoint(host, "orders_only")).Position);
    }

    [Fact]
    public async Task A_subscription_gets_envelopes_and_its_appends_record_the_cause()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Subscription<Receipts>("receipts"));
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata { CorrelationId = "req-5" };
        var cause = (await scope.ServiceProvider.GetRequiredService<IEventStore>().Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("follow-up", 1)])).Events[0];

        await WaitForCaughtUp(host, "receipts");
        var effect = await StoreOf(host).Load<Order>("order-for-cart-1");

        var delivered = Assert.Single(probe.Delivered);
        Assert.Equal((cause.EventId, 1L, "cart.item_added", "req-5"),
            (delivered.Envelope.EventId, delivered.Envelope.GlobalPosition, delivered.Envelope.EventType, delivered.Envelope.Metadata.CorrelationId));
        Assert.Equal(1, effect.Version);
        var stored = await Scalar<string>($"SELECT metadata FROM {Table("events")} WHERE global_position = 2");
        var metadata = JsonNode.Parse(stored)!;
        Assert.Equal((cause.EventId.ToString("D"), "req-5"), (metadata["causationId"]!.GetValue<string>(), metadata["correlationId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_poison_event_stalls_the_projection_after_retries_and_a_skip_job_moves_it_on()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        var store = StoreOf(host);
        await store.Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        var poison = (await store.Append("cart-2", ExpectedVersion.Any, [new ItemAdded("poison", 1)])).Events[0];
        await store.Append("cart-1", ExpectedVersion.Any, [new ItemAdded("c", 1)]);

        await WaitForCheckpoint(host, "applied", r => r.Status == "stalled");

        var stalled = await Checkpoint(host, "applied");
        var error = JsonNode.Parse(stalled.Error!)!;
        Assert.Equal(2L, stalled.Position);
        Assert.Equal(("poison", "cart-2", 1L, "cart.item_added", poison.EventId.ToString("D"), 3L),
            (error["reason"]!.GetValue<string>(), error["streamId"]!.GetValue<string>(), error["version"]!.GetValue<long>(),
             error["eventType"]!.GetValue<string>(), error["eventId"]!.GetValue<string>(), error["globalPosition"]!.GetValue<long>()));
        Assert.Contains("poison poison", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Unhealthy, (await Health(host)).Status);

        var wrong = await WaitForJob(host, await Enqueue(host, Jobs.Skip, new JsonObject { ["projection"] = "applied", ["eventId"] = Guid.NewGuid().ToString() }));
        Assert.Equal("failed", wrong.Status);

        var skip = await WaitForJob(host, await Enqueue(host, Jobs.Skip, new JsonObject { ["projection"] = "applied", ["eventId"] = poison.EventId.ToString() }));
        await WaitForCaughtUp(host, "applied");

        Assert.Equal("done", skip.Status);
        Assert.Equal(poison.EventId.ToString("D"), JsonNode.Parse(skip.Progress!)!["skipped"]!["eventId"]!.GetValue<string>());
        Assert.Equal(3, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Healthy, (await Health(host)).Status);
    }

    [Fact]
    public async Task A_stalled_projection_retries_after_a_restart_and_resumes_when_fixed()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("poison", 1), new ItemAdded("b", 1)]);
        await WaitForCheckpoint(host, "applied", r => r.Status == "stalled");
        await StopHost(host);

        probe.PoisonSku = null;
        var restarted = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));

        await WaitForCaughtUp(restarted, "applied");
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Null((await Checkpoint(restarted, "applied")).Error);
    }

    [Fact]
    public async Task A_subscription_keeps_its_progress_before_a_failing_event()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Subscription<Receipts>("receipts"));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1), new ItemAdded("poison", 1)]);

        await WaitForCheckpoint(host, "receipts", r => r.Status == "stalled");

        Assert.Equal(2L, (await Checkpoint(host, "receipts")).Position);
        Assert.Equal(["a", "b"], probe.Delivered.Select(d => ((ItemAdded)d.Envelope.Event).Sku).Distinct());
    }

    [Fact]
    public async Task Rebuilding_an_async_projection_resets_and_replays_it()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        await WaitForCaughtUp(host, "applied");
        await Execute($"UPDATE {Table("applied")} SET position = -5");

        var job = await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "applied" }));
        await WaitForCaughtUp(host, "applied");

        Assert.Equal("done", job.Status);
        Assert.Equal(1, probe.Resets);
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("applied")} WHERE position = -5"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task Rebuilding_an_inline_projection_under_concurrent_appends_applies_each_event_exactly_once(int pauseMs)
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<InlineApplied>("inline", Run.Inline), o => o.BatchSize = 50);
        var store = StoreOf(host);
        for (var i = 0; i < 30; i++)
            await store.Append($"cart-{i % 5}", ExpectedVersion.Any, [new ItemAdded("a", 1)]);

        using var stop = new CancellationTokenSource();
        var writers = Enumerable.Range(0, 4).Select(async w =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                await StoreOf(host).Append($"cart-{w}", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
                n++;
                if (pauseMs > 0)
                    await Task.Delay(pauseMs, CancellationToken.None);
            }

            return n;
        }).ToList();

        await Task.Delay(200, Ct);
        var job = await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "inline" }));
        await WaitForCheckpoint(host, "inline", r => r.Status == "rebuilding" || r.Status == "running");
        await WaitForCheckpoint(host, "inline", r => r.Status == "running");
        await Task.Delay(300, Ct);
        await stop.CancelAsync();
        var appended = (await Task.WhenAll(writers)).Sum() * 2 + 30;

        Assert.Equal("done", job.Status);
        Assert.Equal(1, probe.Resets);
        Assert.Equal(appended, await AppliedCount("inline"));
        Assert.Equal(appended, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task An_async_ef_projection_saves_its_context_and_its_reset_runs_in_the_rebuild()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<EfCartTotals>("ef_totals", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 2), new ItemAdded("b", 3)]);
        await WaitForCaughtUp(host, "ef_totals");
        Assert.Equal("5", await Scalar<string>($"SELECT note FROM ef_tests.orders WHERE id = '{probe.Prefix}cart-1'"));

        await Execute($"UPDATE ef_tests.orders SET note = '99' WHERE id = '{probe.Prefix}cart-1'");
        await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "ef_totals" }));
        await WaitForCaughtUp(host, "ef_totals");

        Assert.Equal(1, probe.Resets);
        Assert.Equal("5", await Scalar<string>($"SELECT note FROM ef_tests.orders WHERE id = '{probe.Prefix}cart-1'"));
    }

    [Fact]
    public async Task Rebuilding_a_projection_without_ResetAsync_fails_the_job()
    {
        var host = await StartHost(NewProbe(), b => b.Projection<NoReset>("no_reset", Run.Async));

        var job = await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "no_reset" }));

        Assert.Equal("failed", job.Status);
        Assert.Contains("DBX023", job.Progress, StringComparison.Ordinal);
        Assert.Equal("running", (await Checkpoint(host, "no_reset")).Status);
    }

    [Fact]
    public async Task A_projection_whose_run_mode_changed_stalls_until_rebuilt()
    {
        var probe = NewProbe();
        var inline = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Inline));
        await StoreOf(inline).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await StopHost(inline);

        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("b", 1)]);

        var stalled = await Checkpoint(host, "applied");
        Assert.Equal("stalled", stalled.Status);
        Assert.Equal("mode_changed", JsonNode.Parse(stalled.Error!)!["reason"]!.GetValue<string>());
        await Task.Delay(300, Ct);
        Assert.Equal(1, await AppliedCount("async"));

        await WaitForJob(host, await Enqueue(host, Jobs.Rebuild, new JsonObject { ["projection"] = "applied" }));
        await WaitForCaughtUp(host, "applied");
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Equal("async", (await Checkpoint(host, "applied")).Mode);
    }

    [Fact]
    public async Task Two_instances_share_a_projection_and_apply_each_event_once()
    {
        var probe = NewProbe();
        var first = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async), o => o.BatchSize = 5);
        var second = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async), o => o.BatchSize = 5);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async w =>
        {
            for (var i = 0; i < 25; i++)
                await StoreOf(w % 2 == 0 ? first : second).Append($"cart-{w}", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        }));
        await WaitForCaughtUp(first, "applied");

        Assert.Equal(100, await AppliedCount("async"));
    }

    [Fact]
    public async Task A_batch_projection_receives_batches_of_its_event_types()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => b.Projection<Bulk>("bulk", Run.Async), o => o.BatchSize = 4);
        var store = StoreOf(host);
        for (var i = 0; i < 5; i++)
            await store.Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", i), new CheckedOut(DateTimeOffset.UnixEpoch)]);

        await WaitForCaughtUp(host, "bulk");

        Assert.Equal(Enumerable.Range(0, 5), probe.Batches.SelectMany(b => b).Select(e => ((ItemAdded)e.Event).Qty));
        Assert.All(probe.Batches, b => Assert.InRange(b.Count, 1, 2));
    }

    [Fact]
    public void A_batch_projection_registered_inline_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() =>
        {
            var services = new ServiceCollection().AddSingleton(NewProbe());
            services.AddDeedbox(b => UseDatabase(b).Stream<Cart>(s => s.Events<ItemAdded>()).Projection<Bulk>("bulk", Run.Inline));
            services.BuildServiceProvider().GetRequiredService<ProjectionSet>();
        });

        Assert.Equal("DBX024", error.Code);
    }

    [Fact]
    public async Task Health_is_healthy_while_behind_and_moving_and_unhealthy_when_stuck()
    {
        var probe = NewProbe();
        var disabled = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async), o =>
        {
            o.Enabled = false;
            o.StallAfter = TimeSpan.FromMilliseconds(1);
        });
        await StoreOf(disabled).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        await Task.Delay(50, Ct);

        var stuck = await Health(disabled);
        Assert.Equal(HealthStatus.Unhealthy, stuck.Status);
        Assert.Contains("'applied' has not moved", stuck.Entries["deedbox"].Description, StringComparison.Ordinal);
        await StopHost(disabled);

        var running = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await WaitForCaughtUp(running, "applied");
        var healthy = await Health(running);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal("running, position 1, lag 0", healthy.Entries["deedbox"].Data["applied"]);
    }

    [Fact]
    public async Task A_subscription_and_a_projection_cannot_share_a_name()
    {
        var error = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Projection<AsyncApplied>("shared", Run.Async)
            .Subscription<Receipts>("shared")));

        Assert.Equal("DBX020", error.Code);
        await Task.CompletedTask;
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
