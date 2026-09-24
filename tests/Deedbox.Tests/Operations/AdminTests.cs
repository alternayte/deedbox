using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.PersonalData;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Operations;

public sealed class PostgresAdminTests(Databases databases) : AdminTests(databases, Db.Postgres);

public sealed class SqlServerAdminTests(Databases databases) : AdminTests(databases, Db.SqlServer);

public abstract class AdminTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private static IEventStoreAdmin AdminOf(IHost host) => host.Services.GetRequiredService<IEventStoreAdmin>();

    private static async Task<JobInfo> Finished(IHost host, Guid id)
    {
        JobInfo? job = null;
        await WaitFor(async () => (job = await AdminOf(host).GetJobAsync(id))?.Status is "done" or "failed", $"job {id}");
        return job!;
    }

    [Fact]
    public async Task Status_shows_consumers_lag_stalls_and_jobs_and_admin_jobs_fix_them()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async).Projection<InlineApplied>("inline", Run.Inline));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("poison", 1), new ItemAdded("b", 1)]);
        await WaitForCheckpoint(host, "applied", r => r.Status == "stalled");

        var status = await AdminOf(host).GetStatusAsync();
        var applied = status.Consumers.Single(c => c.Name == "applied");
        var inline = status.Consumers.Single(c => c.Name == "inline");
        Assert.Equal(3, status.Head);
        Assert.Equal(("async", "stalled", 1L, 2L), (applied.Mode, applied.Status, applied.Position, applied.Lag));
        Assert.Equal(("inline", "running", 0L), (inline.Mode, inline.Status, inline.Lag));
        var poison = Guid.Parse(JsonNode.Parse(applied.Error!)!["eventId"]!.GetValue<string>());

        var skip = await Finished(host, await AdminOf(host).SkipAsync("applied", poison));
        await WaitForCaughtUp(host, "applied");
        probe.PoisonSku = null;
        var rebuild = await Finished(host, await AdminOf(host).RebuildAsync("inline"));
        await WaitForCaughtUp(host, "inline");

        Assert.Equal(("skip", "done"), (skip.Kind, skip.Status));
        Assert.Equal(("rebuild", "done"), (rebuild.Kind, rebuild.Status));
        Assert.Equal(2, await AppliedCount("async"));
        Assert.Contains((await AdminOf(host).GetStatusAsync()).Jobs, j => j.Id == skip.Id && j.FinishedAt is not null);
        Assert.Null(await AdminOf(host).GetJobAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Admin_jobs_for_unregistered_names_fail_at_once_and_queue_nothing()
    {
        var host = await StartHost(NewProbe(), b => b.Projection<AsyncApplied>("applied", Run.Async).Subscription<Receipts>("receipts"));

        var rebuild = await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).RebuildAsync("nope"));
        var subscription = await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).RebuildAsync("receipts"));
        var skip = await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).SkipAsync("nope", Guid.NewGuid()));

        Assert.Equal([Errors.UnknownConsumer, Errors.UnknownConsumer, Errors.UnknownConsumer], new[] { rebuild.Code, subscription.Code, skip.Code });
        Assert.Empty((await AdminOf(host).GetStatusAsync()).Jobs);
    }

    [Fact]
    public async Task Rebuilding_snapshots_replaces_every_stored_state_of_a_stream_type()
    {
        var host = await StartHost(NewProbe(), b => b.Stream<Counter>(s => s.Events<Incremented>()));
        foreach (var tenant in new[] { "", "acme" })
        {
            var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenant;
            for (var i = 0; i < 3; i++)
                await scope.ServiceProvider.GetRequiredService<IEventStore>().Append($"counter-{i}", ExpectedVersion.NoStream, [new Incremented(i + 1)]);
        }

        await Execute($"UPDATE {Table("streams")} SET state = '{{\"total\":999,\"count\":1}}' WHERE stream_type = 'counter'");
        var job = await Finished(host, await AdminOf(host).RebuildSnapshotsAsync("counter"));

        Assert.Equal(6, JsonNode.Parse(job.Progress!)!["streams"]!.GetValue<int>());
        Assert.Equal(new Counter(2, 1), (await StoreOf(host).Load<Counter>("counter-1")).State);
        Assert.Equal(Errors.UnregisteredState, (await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).RebuildSnapshotsAsync("nope"))).Code);
    }

    [Fact]
    public async Task Shredding_a_tenant_erases_its_personal_data_on_every_instance_and_new_data_gets_a_new_key()
    {
        var probe = NewProbe();
        Action<DeedboxBuilder> configure = b => { Keys.Streams(b); b.Keys(k => k.StoreInDatabase()); };
        var first = await StartHost(probe, configure);
        var second = await StartHost(probe, configure);
        await Tenant(first, "acme").Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", null)]);
        await Tenant(first, "globex").Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", null)]);

        await AdminOf(second).ShredTenantAsync("acme");

        // The first instance still holds acme's old key in memory; a new subject there must get a key under a live tenant key.
        await Tenant(first, "acme").Append("m-1", ExpectedVersion.Any, [new ReviewerInvited("m-1", "person:2", "Grace", null)]);
        Assert.Equal([null, "Grace"], (await Tenant(second, "acme").Load<Manuscript>("m-1")).State.Names);
        Assert.Equal(["Ada"], (await Tenant(second, "globex").Load<Manuscript>("m-1")).State.Names);
        Assert.Equal(["shredded", "database:0"], await Strings($"SELECT wrapped_by FROM {Table("master_keys")} WHERE tenant_id = 'acme' ORDER BY key_version"));
    }

    [Fact]
    public async Task Admin_erasure_and_rewrap_act_on_the_named_tenant_and_keys()
    {
        var ring = Keys.Ring($"{Schema}-a");
        var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(ring)); });
        await Tenant(host, "acme").Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", null)]);

        var job = await Finished(host, await AdminOf(host).EraseSubjectAsync("person:1", "acme"));
        var rewrapped = await AdminOf(host).RewrapKeysAsync(new KeyRingMasterKey(Keys.Ring($"{Schema}-b")));

        Assert.Equal("done", job.Status);
        Assert.Equal([null], (await Tenant(host, "acme").Load<Manuscript>("m-1")).State.Names);
        Assert.Equal(1, rewrapped);
    }

    private static IEventStore Tenant(IHost host, string tenant)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenant;
        return scope.ServiceProvider.GetRequiredService<IEventStore>();
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<string>> Strings(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
            rows.Add(reader.GetString(0));
        return rows;
    }
}
