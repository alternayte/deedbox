using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.PersonalData;

public sealed class PostgresPersonalDataTests(Databases databases) : PersonalDataTests(databases, Db.Postgres);

public sealed class SqlServerPersonalDataTests(Databases databases) : PersonalDataTests(databases, Db.SqlServer);

public sealed class ReviewerMail : Subscription
{
    public ReviewerMail(Probe probe) => On<ReviewerInvited>((_, ctx) =>
    {
        probe.Delivered.Enqueue(("mail", ctx.Envelope));
        return Task.CompletedTask;
    });
}

public sealed class DeletionLog : Projection
{
    public DeletionLog(Probe probe) => On<StreamDeleted>((_, ctx) =>
    {
        probe.Delivered.Enqueue(("deleted", new EventEnvelope(ctx.EventId, ctx.TenantId, ctx.StreamId, ctx.StreamType, ctx.Version, 0, "", 1, new StreamDeleted(), ctx.Metadata, ctx.OccurredAt)));
        return Task.CompletedTask;
    });
}

public abstract class PersonalDataTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private static readonly ReviewerInvited Ada = new("m-1", "person:1", "Ada Lovelace", "ada@example.org");
    private static readonly ReviewerInvited Grace = new("m-1", "person:2", "Grace Hopper", null);

    private static Action<DeedboxBuilder> InDatabase(Action<KeysBuilder>? more = null) => b =>
    {
        Keys.Streams(b);
        b.Keys(k =>
        {
            k.StoreInDatabase();
            more?.Invoke(k);
        });
    };

    [Fact]
    public async Task Personal_fields_and_state_are_stored_encrypted_and_read_back()
    {
        var host = await StartHost(NewProbe(), InDatabase());
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada, Grace]);

        var payload = await Scalar<string>($"SELECT payload FROM {Table("events")} WHERE global_position = 1");
        var state = await Scalar<string>($"SELECT state FROM {Table("streams")}");
        Assert.DoesNotContain("Ada", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@", payload, StringComparison.Ordinal);
        Assert.Contains("person:1", payload, StringComparison.Ordinal);
        Assert.Contains("$enc", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada", state, StringComparison.Ordinal);
        Assert.Contains("$state", state, StringComparison.Ordinal);

        Assert.Equal(["Ada Lovelace", "Grace Hopper"], (await StoreOf(host).Load<Manuscript>("m-1")).State.Names);
        await Execute($"UPDATE {Table("streams")} SET state = NULL");
        Assert.Equal(["Ada Lovelace", "Grace Hopper"], (await StoreOf(host).Load<Manuscript>("m-1")).State.Names);
        Assert.Equal(["person:1", "person:2"], await Strings($"SELECT subject_id FROM {Table("subject_streams")} ORDER BY subject_id"));
    }

    [Fact]
    public async Task Erasing_a_subject_redacts_their_data_everywhere_and_leaves_others_readable()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, InDatabase());
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada, Grace]);
        await StoreOf(host).Append("m-2", ExpectedVersion.NoStream, [Ada with { ManuscriptId = "m-2" }, new CoAuthorAdded("m-2", "person:1", "Ada L.", "person:2", "met Grace")]);

        var job = await host.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("person:1");

        // The key is gone before the job runs, so reads are redacted at once.
        Assert.Equal([null, "Grace Hopper"], (await StoreOf(host).Load<Manuscript>("m-1") is var m1 ? m1.State.Names : null)!);
        var done = await WaitForJob(host, job);
        Assert.Equal("done", done.Status);
        Assert.Equal(2, JsonNode.Parse(done.Progress!)!["streams"]!.GetValue<int>());

        var second = await StoreOf(host).Load<Manuscript>("m-2");
        Assert.Equal([null, null], second.State.Names);
        Assert.Equal(3, second.Version);
        Assert.Equal(["deedbox.subject_erased", "deedbox.subject_erased"],
            await Strings($"SELECT event_type FROM {Table("events")} WHERE event_type LIKE 'deedbox.%' ORDER BY global_position"));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("subject_keys")} WHERE subject_id = 'person:1'"));
        Assert.Equal(["person:2", "person:2"], await Strings($"SELECT subject_id FROM {Table("subject_streams")} ORDER BY stream_id"));

        // The rebuilt state holds no erased data; the note about person:2 stays readable.
        await StopHost(host);
        var verify = await StartHost(probe, b => { InDatabase()(b); b.Subscription<ReviewerMail>("mail"); });
        await WaitForCaughtUp(verify, "mail");
        var ada = probe.Delivered.Where(d => d.Consumer == "mail").Select(d => d.Envelope).First(e => ((ReviewerInvited)e.Event).ReviewerId == "person:1");
        Assert.Equal(["person:1"], ada.ErasedSubjects);
        Assert.Null(((ReviewerInvited)ada.Event).ReviewerName);
        Assert.Null(((ReviewerInvited)ada.Event).ReviewerEmail);
        var note = await StoreOf(verify).Load<Manuscript>("m-2");
        Assert.Equal(3, note.Version);
    }

    [Fact]
    public async Task Erased_strings_read_as_the_placeholder_and_new_data_about_the_subject_is_readable()
    {
        var host = await StartHost(NewProbe(), InDatabase(k => k.RedactWith("[erased]")));
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada]);

        await WaitForJob(host, await host.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("person:1"));
        await StoreOf(host).Append("m-1", ExpectedVersion.Any, [Ada with { ReviewerName = "Ada, again" }]);
        await Execute($"UPDATE {Table("streams")} SET state = NULL");

        Assert.Equal(["[erased]", "Ada, again"], (await StoreOf(host).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task Erasure_is_scoped_to_the_tenant()
    {
        var host = await StartHost(NewProbe(), InDatabase());
        var acme = Tenant(host, "acme");
        var globex = Tenant(host, "globex");
        await acme.Store.Append("m-1", ExpectedVersion.NoStream, [Ada]);
        await globex.Store.Append("m-1", ExpectedVersion.NoStream, [Ada]);

        await WaitForJob(host, await acme.Erasure.EraseSubjectAsync("person:1"));

        Assert.Equal([null], (await Tenant(host, "acme").Store.Load<Manuscript>("m-1")).State.Names);
        Assert.Equal(["Ada Lovelace"], (await Tenant(host, "globex").Store.Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task A_rerun_of_the_erasure_job_appends_no_second_tombstone()
    {
        var host = await StartHost(NewProbe(), InDatabase());
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada]);
        await StoreOf(host).Append("m-2", ExpectedVersion.NoStream, [Ada with { ManuscriptId = "m-2" }]);

        // A crashed run erased one stream before stopping; the queued job finishes the other.
        await RuntimeOf(host).Keys!.LoadAll(Ct);
        await SubjectErasure.DeleteKey(RuntimeOf(host), "", "person:1", Ct);
        var scope = host.Services.CreateScope();
        await ((EventStore)scope.ServiceProvider.GetRequiredService<IEventStore>()).EraseFromStream("m-1", "person:1", Ct);
        await WaitForJob(host, await Enqueue(host, Jobs.Erase, new JsonObject { ["tenantId"] = "", ["subjectId"] = "person:1" }));
        await WaitForJob(host, await Enqueue(host, Jobs.Erase, new JsonObject { ["tenantId"] = "", ["subjectId"] = "person:1" }));

        Assert.Equal(["m-1", "m-2"], await Strings($"SELECT stream_id FROM {Table("events")} WHERE event_type = 'deedbox.subject_erased' ORDER BY stream_id"));
    }

    [Fact]
    public async Task Deleting_a_stream_leaves_a_tombstone_and_blocks_reuse_of_its_id()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, b => { InDatabase()(b); b.Projection<DeletionLog>("deletions", Run.Inline); });
        var store = StoreOf(host);
        await store.Append("m-1", ExpectedVersion.NoStream, [Ada, new Submitted("On engines")]);
        await store.Append("m-2", ExpectedVersion.NoStream, [new Submitted("Kept")]);

        await store.DeleteStream("m-1");
        await store.DeleteStream("m-1");
        await store.DeleteStream("never-existed");

        Assert.Equal(["deedbox.stream_deleted"], await Strings($"SELECT event_type FROM {Table("events")} WHERE stream_id = 'm-1'"));
        Assert.Equal(3L, await Scalar<long>($"SELECT version FROM {Table("events")} WHERE stream_id = 'm-1'"));
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("subject_streams")}"));
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")} WHERE stream_id = 'm-2'"));
        Assert.Equal("DBX028", (await Assert.ThrowsAsync<DeedboxException>(() => store.Load<Manuscript>("m-1"))).Code);
        Assert.Equal("DBX028", (await Assert.ThrowsAsync<DeedboxException>(() => store.Append("m-1", ExpectedVersion.Any, [new Submitted("Again")]))).Code);
        var deleted = Assert.Single(probe.Delivered, d => d.Consumer == "deleted");
        Assert.Equal(("m-1", 3L), (deleted.Envelope.StreamId, deleted.Envelope.Version));
    }

    [Fact]
    public async Task Built_in_events_cannot_be_appended_directly()
    {
        var host = await StartHost(NewProbe(), InDatabase());

        var error = await Assert.ThrowsAsync<DeedboxException>(() => StoreOf(host).Append("m-1", ExpectedVersion.Any, [new SubjectErased("person:1")]));

        Assert.Equal("DBX031", error.Code);
    }

    [Fact]
    public async Task An_altered_ciphertext_fails_loudly_instead_of_reading_as_erased()
    {
        var host = await StartHost(NewProbe(), InDatabase());
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada]);
        var payload = JsonNode.Parse(await Scalar<string>($"SELECT payload FROM {Table("events")}"))!;
        var marker = payload["reviewerName"]!["$enc"]!.GetValue<string>();
        payload["reviewerName"]!["$enc"] = marker[..^4] + (marker[^4] == 'A' ? "B" : "A") + marker[^3..];
        await Execute($"UPDATE {Table("events")} SET payload = '{payload.ToJsonString()}'");
        await Execute($"UPDATE {Table("streams")} SET state = NULL");

        var error = await Assert.ThrowsAsync<DeedboxException>(() => StoreOf(host).Load<Manuscript>("m-1"));

        Assert.Equal("DBX030", error.Code);
    }

    [Fact]
    public async Task A_missing_subject_id_fails_the_append()
    {
        var host = await StartHost(NewProbe(), InDatabase());

        var error = await Assert.ThrowsAsync<DeedboxException>(() => StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada with { ReviewerId = "" }]));

        Assert.Equal("DBX027", error.Code);
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("events")}"));
    }

    [Fact]
    public async Task A_wrong_master_key_fails_startup_and_a_key_ring_rotation_rewraps()
    {
        var v1 = Keys.Ring($"{Schema}-v1");
        var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(v1)); });
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada]);
        await StopHost(host);

        var wrong = await Assert.ThrowsAsync<DeedboxException>(() => StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(Keys.Ring($"{Schema}-v2"))); }));
        Assert.Equal("DBX029", wrong.Code);

        var ring = Keys.Ring($"{Schema}-v2", $"{Schema}-v1");
        var rotated = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(ring)); });
        var keys = RuntimeOf(rotated).Keys!;
        Assert.Equal(1, await keys.Rewrap(keys.Master, Ct));
        await StopHost(rotated);

        var only = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(Keys.Ring($"{Schema}-v2"))); });
        await Execute($"UPDATE {Table("streams")} SET state = NULL");
        Assert.Equal(["Ada Lovelace"], (await StoreOf(only).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task Moving_from_database_mode_to_a_key_ring_rewraps_and_deletes_the_stored_master_key()
    {
        var host = await StartHost(NewProbe(), InDatabase());
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [Ada]);
        var ring = Keys.Ring($"{Schema}-env");

        Assert.Equal(1, await RuntimeOf(host).Keys!.Rewrap(new KeyRingMasterKey(ring), Ct));
        await StopHost(host);

        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("master_keys")} WHERE key_version = 0"));
        var env = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(ring)); });
        await Execute($"UPDATE {Table("streams")} SET state = NULL");
        Assert.Equal(["Ada Lovelace"], (await StoreOf(env).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public void Startup_rules_reject_missing_key_modes_and_unerasable_types()
    {
        var noMode = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => Keys.Streams(UseDatabase(b))));
        var notNullable = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Keys(k => k.StoreInDatabase()).Stream<Age>(s => s.Events<BadAge>())));
        var noSubject = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Keys(k => k.StoreInDatabase()).Stream<Age>(s => s.Events<NoSubject>())));

        Assert.Equal(("DBX025", "DBX026", "DBX027"), (noMode.Code, notNullable.Code, noSubject.Code));
        Assert.Contains("keys.StoreInDatabase()", noMode.Message, StringComparison.Ordinal);
        Assert.Contains("keys.FromEnvironment(\"DEEDBOX_MASTER_KEY\")", noMode.Message, StringComparison.Ordinal);
    }

    private (IEventStore Store, ISubjectErasure Erasure) Tenant(IHost host, string tenant)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenant;
        return (scope.ServiceProvider.GetRequiredService<IEventStore>(), scope.ServiceProvider.GetRequiredService<ISubjectErasure>());
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
