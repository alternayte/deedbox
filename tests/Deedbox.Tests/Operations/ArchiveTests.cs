using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.PersonalData;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Operations;

public sealed class PostgresArchiveTests(Databases databases) : ArchiveTests(databases, Db.Postgres);

public sealed class SqlServerArchiveTests(Databases databases) : ArchiveTests(databases, Db.SqlServer);

public abstract class ArchiveTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private static IEventStoreAdmin AdminOf(IHost host) => host.Services.GetRequiredService<IEventStoreAdmin>();

    private static async Task<JsonObject> Done(IHost host, Guid id)
    {
        JobInfo? job = null;
        await WaitFor(async () => (job = await AdminOf(host).GetJobAsync(id))?.Status is JobState.Done or JobState.Failed, $"job {id}");
        Assert.True(job!.Status == JobState.Done, job.Progress);
        return JsonNode.Parse(job.Progress!)!.AsObject();
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task<int> Rows(string table, string streamId) => Scalar<int>($"SELECT COUNT(*) FROM {Table(table)} WHERE stream_id = '{streamId}'");

    [Fact]
    public async Task An_archived_stream_loads_reads_appends_and_deletes_as_before_and_rebuilds_skip_it_unless_asked()
    {
        var host = await StartHost(NewProbe(), b => b.Projection<AsyncApplied>("applied", Run.Async).Projection<InlineApplied>("inline", Run.Inline));
        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 2), new ItemAdded("a", 3)]);
        await StoreOf(host).Append("cart-2", ExpectedVersion.NoStream, [new ItemAdded("c", 1), new ItemAdded("c", 1)]);
        await WaitForCaughtUp(host, "applied");
        var loaded = await StoreOf(host).Load<Cart>("cart-1");
        var read = await StoreOf(host).ReadStream("cart-1");

        var progress = await Done(host, await AdminOf(host).ArchiveStreamAsync("", "cart-1"));

        Assert.Equal((1, 0, 0, 3L), (progress["archived"]!.GetValue<int>(), progress["partial"]!.GetValue<int>(), progress["skipped"]!.GetValue<int>(), progress["events"]!.GetValue<long>()));
        Assert.Equal((0, 3, 2), (await Rows("events", "cart-1"), await Rows("events_archive", "cart-1"), await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}")));
        Assert.Equal(3, await Scalar<long>($"SELECT archived_to FROM {Table("streams")} WHERE stream_id = 'cart-1'"));
        var archived = await StoreOf(host).Load<Cart>("cart-1");
        Assert.Equal((loaded.Version, loaded.State.Changes), (archived.Version, archived.State.Changes));
        Assert.Equal(loaded.State.Items.OrderBy(i => i.Key), archived.State.Items.OrderBy(i => i.Key));
        await Execute($"UPDATE {Table("streams")} SET state = NULL");
        var replayed = await StoreOf(host).Load<Cart>("cart-1");
        Assert.Equal((3L, 4, 2), (replayed.Version, replayed.State.Items["a"], replayed.State.Items["b"]));
        Assert.Equal(read.Select(e => (e.EventId, e.Version, e.GlobalPosition)), (await StoreOf(host).ReadStream("cart-1")).Select(e => (e.EventId, e.Version, e.GlobalPosition)));

        // A new event goes to the events table; the stream reads whole, and a second archive moves it too.
        await StoreOf(host).Append("cart-1", ExpectedVersion.Exact(3), [new ItemAdded("d", 1)]);
        Assert.Equal([1L, 2L, 3L, 4L], (await StoreOf(host).ReadStream("cart-1")).Select(e => e.Version));
        Assert.Equal([3L, 4L], (await StoreOf(host).ReadStream("cart-1", afterVersion: 2, limit: 2)).Select(e => e.Version));
        await WaitForCaughtUp(host, "applied");

        Assert.Equal(6, await AppliedCount("async"));
        await Done(host, await AdminOf(host).RebuildAsync("applied"));
        await WaitForCaughtUp(host, "applied");
        Assert.Equal(3, await AppliedCount("async"));
        await Done(host, await AdminOf(host).RebuildWithArchiveAsync("applied"));
        await WaitForCaughtUp(host, "applied");
        Assert.Equal(6, await AppliedCount("async"));
        await Done(host, await AdminOf(host).RebuildWithArchiveAsync("inline"));
        await WaitForCaughtUp(host, "inline");
        Assert.Equal(6, await AppliedCount("inline"));

        Assert.Equal(1L, (await Done(host, await AdminOf(host).ArchiveStreamsAsync([new StreamKey("", "cart-1"), new StreamKey("", "cart-9")])))["events"]!.GetValue<long>());
        await StoreOf(host).DeleteStream("cart-1");
        Assert.Equal((1, 0), (await Rows("events", "cart-1"), await Rows("events_archive", "cart-1")));
        Assert.IsType<StreamDeleted>(Assert.Single(await StoreOf(host).ReadStream("cart-1")).Event);
        Assert.Equal(1, (await Done(host, await AdminOf(host).ArchiveStreamAsync("", "cart-1")))["skipped"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_archive_moves_no_event_that_a_consumer_has_not_applied_and_counts_the_stream_partial()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("poison", 1), new ItemAdded("b", 1)]);
        await WaitForCheckpoint(host, "applied", r => r.Status == "stalled");

        var partial = await Done(host, await AdminOf(host).ArchiveStreamAsync("", "cart-1"));

        Assert.Equal((0, 1, 1L), (partial["archived"]!.GetValue<int>(), partial["partial"]!.GetValue<int>(), partial["events"]!.GetValue<long>()));
        Assert.Equal((2, 1), (await Rows("events", "cart-1"), await Rows("events_archive", "cart-1")));
        var poison = Guid.Parse(JsonNode.Parse((await Checkpoint(host, "applied")).Error!)!["eventId"]!.GetValue<string>());
        await Done(host, await AdminOf(host).SkipAsync("applied", poison));
        await WaitForCaughtUp(host, "applied");
        Assert.Equal(2, await AppliedCount("async"));

        var rest = await Done(host, await AdminOf(host).ArchiveStreamAsync("", "cart-1"));
        Assert.Equal((1, 2L), (rest["archived"]!.GetValue<int>(), rest["events"]!.GetValue<long>()));
        Assert.Equal(3, await Rows("events_archive", "cart-1"));
    }

    [Fact]
    public async Task Nothing_is_archived_while_an_instance_that_cannot_read_the_archive_has_a_heartbeat_row()
    {
        var host = await StartHost(NewProbe(), _ => { });
        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        var old = Guid.NewGuid();
        await Execute($"INSERT INTO {Table("instances")} (instance_id, host, app, consumers, inline_projections, event_types, formats) VALUES ('{old}', 'old', 'old', '[]', '[]', '[]', 2)");

        var refused = await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).ArchiveStreamAsync("", "cart-1"));
        var job = await WaitForJob(host, await Enqueue(host, Jobs.Archive, new JsonObject { ["streams"] = new JsonArray((JsonNode)new JsonObject { ["tenantId"] = "", ["streamId"] = "cart-1" }) }));

        Assert.Equal(Errors.ArchiveUnreadable, refused.Code);
        Assert.Equal("failed", job.Status);
        Assert.Contains("DBX041", job.Progress, StringComparison.Ordinal);
        Assert.Equal(1, await Rows("events", "cart-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => AdminOf(host).ArchiveStreamsAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => AdminOf(host).ArchiveStreamAsync("", " cart-1"));

        await Execute($"DELETE FROM {Table("instances")} WHERE instance_id = '{old}'");
        await Done(host, await AdminOf(host).ArchiveStreamAsync("", "cart-1"));
        Assert.Equal(1, await Rows("events_archive", "cart-1"));
    }

    [Fact]
    public async Task Erasing_a_subject_of_an_archived_stream_redacts_its_archived_events_and_rebuilds_the_state()
    {
        var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.StoreInDatabase()); }, defaultStreams: false);
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream,
            [new ReviewerInvited("m-1", "person:1", "Ada", null), new ReviewerInvited("m-1", "person:2", "Grace", null)]);
        await Done(host, await AdminOf(host).ArchiveStreamAsync("", "m-1"));

        await WaitForJob(host, (await AdminOf(host).EraseSubjectAsync("person:1", "")).JobIds[0]);

        var events = await StoreOf(host).ReadStream("m-1");
        Assert.Equal(["person:1"], events[0].ErasedSubjects);
        Assert.Null(((ReviewerInvited)events[0].Event).ReviewerName);
        Assert.Equal("person:1", ((SubjectErased)events[2].Event).SubjectId);
        var loaded = await StoreOf(host).Load<Manuscript>("m-1");
        Assert.Equal([null, "Grace"], loaded.State.Names);
        Assert.Equal(3, loaded.Version);
        Assert.Equal((2, 1), (await Rows("events_archive", "m-1"), await Rows("events", "m-1")));
    }
}
