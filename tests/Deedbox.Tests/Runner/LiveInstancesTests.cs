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
        Assert.Equal(("retired", 0L), (retired.Status, retired.Lag));
        await StoreOf(admin).Append("cart-2", ExpectedVersion.Any, [new ItemAdded("b", 1)]);

        // An instance that still registers it starts; the projection stays idle, and the health check says why.
        var back = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await Task.Delay(500, Ct);
        Assert.Equal(1, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Degraded, (await Health(back)).Status);

        await WaitForJob(back, await AdminOf(back).RebuildAsync("applied"));
        await WaitForCaughtUp(back, "applied");
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Equal(HealthStatus.Healthy, (await Health(back)).Status);
    }
}
