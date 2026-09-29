using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.PersonalData;

public sealed class PostgresPseudonymTests(Databases databases) : PseudonymTests(databases, Db.Postgres);

public sealed class SqlServerPseudonymTests(Databases databases) : PseudonymTests(databases, Db.SqlServer);

public abstract class PseudonymTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    internal static readonly Action<DeedboxBuilder> InDatabase = b =>
    {
        Keys.Streams(b);
        b.Keys(k => k.StoreInDatabase());
    };

    [Fact]
    public async Task The_same_identity_and_period_give_the_same_subject_id_on_every_instance_and_anything_else_differs()
    {
        var first = await StartHost(NewProbe(), InDatabase);
        var second = await StartHost(NewProbe(), InDatabase);

        var alice = await Pseudonyms(first).SubjectForAsync("github:alice", "2026-Q3");

        Assert.Matches("^person:[a-z2-7]{26}$", alice);
        Assert.Equal(alice, await Pseudonyms(second).SubjectForAsync("github:alice", "2026-Q3"));
        Assert.Equal(alice, await Pseudonyms(first).SubjectForAsync("github:alice", "2026-Q3"));
        Assert.NotEqual(alice, await Pseudonyms(first).SubjectForAsync("github:alice", "2026-Q4"));
        Assert.NotEqual(alice, await Pseudonyms(first, "acme").SubjectForAsync("github:alice", "2026-Q3"));
        Assert.NotEqual(alice, await Pseudonyms(first).SubjectForAsync("github:alicf", "2026-Q3"));
        Assert.NotEqual(alice, await Pseudonyms(first).SubjectForAsync("GitHub:alice", "2026-Q3"));

        // Instances that create a period's secret at the same time agree on one.
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Pseudonyms(i % 2 == 0 ? first : second).SubjectForAsync("github:bob", "2027-Q1")));
        Assert.Single(racing.Distinct());

        // The ID is the stored secret's token, so any instance on either database computes the same one.
        var row = await PseudonymRow(first, "", "2026-Q3");
        var secret = await RuntimeOf(first).Keys!.Master.UnwrapAsync(row.WrappedKey, row.WrappedBy, Ct);
        Assert.Equal("person:" + Pseudonymizer.Token(secret, "github:alice"), alice);
        Assert.Equal(("person:", "database:0"), (row.Prefix, row.WrappedBy));
    }

    [Fact]
    public async Task Erasing_an_identity_erases_its_subject_in_every_period_whose_secret_exists()
    {
        var host = await StartHost(NewProbe(), InDatabase);
        var acme = Pseudonyms(host, "acme");
        var q1 = await acme.SubjectForAsync("github:alice", "2026-Q1");
        var q2 = await acme.SubjectForAsync("github:alice", "2026-Q2");
        var old = await acme.SubjectForAsync("github:alice", "2025-Q4");
        var bob = await acme.SubjectForAsync("github:bob", "2026-Q2");
        await Store(host, "acme").Append("m-1", ExpectedVersion.NoStream,
            [Invite(q1, "Alice in Q1"), Invite(q2, "Alice in Q2"), Invite(old, "Alice in 2025"), Invite(bob, "Bob")]);
        Assert.True(await AdminOf(host).DestroyPseudonymPeriodAsync("2025-Q4", "acme"));

        var jobs = await acme.EraseIdentityAsync("github:alice");

        Assert.Equal(2, jobs.Count);
        Assert.Equal([null, null, "Alice in 2025", "Bob"], (await Store(host, "acme").Load<Manuscript>("m-1")).State.Names);
        foreach (var job in jobs)
            Assert.Equal("done", (await WaitForJob(host, job)).Status);
        // Two jobs enqueued back to back can share created_at (SQL Server's clock ticks every few milliseconds), so
        // their order is not part of the contract: exactly the live periods' subjects are erased.
        Assert.Equal(new[] { q1, q2 }.Order(StringComparer.Ordinal), (await Strings($"SELECT args FROM {Table("jobs")} WHERE kind = 'erase'", json: "subjectId")).Order(StringComparer.Ordinal));
        Assert.Empty(await Pseudonyms(host, "globex").EraseIdentityAsync("github:alice"));
    }

    [Fact]
    public async Task Destroying_a_period_secret_makes_its_subject_ids_impossible_to_compute_on_every_instance_and_is_audited()
    {
        var first = await StartHost(NewProbe(), InDatabase);
        var second = await StartHost(NewProbe(), InDatabase);
        var before = await Pseudonyms(first, "acme").SubjectForAsync("github:alice", "2026-Q1");
        Assert.Equal(before, await Pseudonyms(second, "acme").SubjectForAsync("github:alice", "2026-Q1"));

        Assert.True(await AdminOf(first).DestroyPseudonymPeriodAsync("2026-Q1", "acme"));

        foreach (var host in new[] { first, second })
        {
            var error = await Assert.ThrowsAsync<DeedboxException>(() => Pseudonyms(host, "acme").SubjectForAsync("github:alice", "2026-Q1"));
            Assert.Equal(Errors.PseudonymPeriodDestroyed, error.Code);
        }

        var row = await PseudonymRow(first, "acme", "2026-Q1");
        Assert.Equal((0, "destroyed", true), (row.WrappedKey.Length, row.WrappedBy, row.Destroyed));
        var audit = Assert.Single(await Jobs(first), j => j.Kind == "pseudonyms_destroyed");
        var args = System.Text.Json.Nodes.JsonNode.Parse(audit.Args)!;
        Assert.Equal(("done", "acme", "2026-Q1", true),
            (audit.Status, args["tenantId"]!.GetValue<string>(), args["periodId"]!.GetValue<string>(), System.Text.Json.Nodes.JsonNode.Parse(audit.Progress!)!["destroyed"]!.GetValue<bool>()));

        // Destroying again, or a period that never had a secret, closes it and returns false.
        Assert.False(await AdminOf(second).DestroyPseudonymPeriodAsync("2026-Q1", "acme"));
        Assert.False(await AdminOf(second).DestroyPseudonymPeriodAsync("2030-Q1", "acme"));
        Assert.Equal(Errors.PseudonymPeriodDestroyed,
            (await Assert.ThrowsAsync<DeedboxException>(() => Pseudonyms(first, "acme").SubjectForAsync("github:alice", "2030-Q1"))).Code);
        Assert.Equal(3, (await Jobs(first)).Count(j => j.Kind == "pseudonyms_destroyed"));

        // Other periods and tenants are untouched.
        Assert.Matches("^person:", await Pseudonyms(first, "acme").SubjectForAsync("github:alice", "2026-Q2"));
        Assert.Matches("^person:", await Pseudonyms(first, "globex").SubjectForAsync("github:alice", "2026-Q1"));
    }

    [Fact]
    public async Task Rotating_or_rewrapping_the_master_key_does_not_change_any_subject_id()
    {
        var v1 = Keys.Ring($"{Schema}-v1");
        var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(v1)); });
        var alice = await Pseudonyms(host).SubjectForAsync("github:alice", "2026-Q1");
        var bob = await Pseudonyms(host, "acme").SubjectForAsync("github:bob", "2026-Q1");
        await Store(host).Append("m-1", ExpectedVersion.NoStream, [Invite(alice, "Alice")]);
        await StopHost(host);

        var rotated = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(Keys.Ring($"{Schema}-v2", $"{Schema}-v1"))); });
        Assert.Equal(3, await AdminOf(rotated).RewrapKeysAsync(RuntimeOf(rotated).Keys!.Master));
        await StopHost(rotated);

        var v2 = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromKeyRing(Keys.Ring($"{Schema}-v2"))); });
        Assert.Equal(alice, await Pseudonyms(v2).SubjectForAsync("github:alice", "2026-Q1"));
        Assert.Equal(bob, await Pseudonyms(v2, "acme").SubjectForAsync("github:bob", "2026-Q1"));
        Assert.Equal(3, await AdminOf(v2).RewrapKeysAsync(new DatabaseMasterKey(RuntimeOf(v2).Provider)));
        await StopHost(v2);

        var database = await StartHost(NewProbe(), InDatabase);
        Assert.Equal(alice, await Pseudonyms(database).SubjectForAsync("github:alice", "2026-Q1"));
        Assert.Equal(bob, await Pseudonyms(database, "acme").SubjectForAsync("github:bob", "2026-Q1"));
        Assert.Equal(["Alice"], (await Store(database).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task Shredding_a_tenant_destroys_its_pseudonym_secrets()
    {
        var host = await StartHost(NewProbe(), InDatabase);
        var acme = await Pseudonyms(host, "acme").SubjectForAsync("github:alice", "2026-Q1");
        var globex = await Pseudonyms(host, "globex").SubjectForAsync("github:alice", "2026-Q1");
        await Pseudonyms(host, "acme").SubjectForAsync("github:alice", "2026-Q2");
        await AdminOf(host).DestroyPseudonymPeriodAsync("2025-Q4", "acme");

        await AdminOf(host).ShredTenantAsync("acme");

        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("pseudonym_keys")} WHERE tenant_id = 'acme'"));
        Assert.Empty(await Pseudonyms(host, "acme").EraseIdentityAsync("github:alice"));
        Assert.NotEqual(acme, await Pseudonyms(host, "acme").SubjectForAsync("github:alice", "2026-Q1"));
        Assert.Equal(globex, await Pseudonyms(host, "globex").SubjectForAsync("github:alice", "2026-Q1"));
    }

    [Fact]
    public async Task Reads_rebuilds_and_erasure_by_subject_id_never_need_a_pseudonym_secret()
    {
        var probe = NewProbe();
        var host = await StartHost(probe, InDatabase);
        var alice = await Pseudonyms(host).SubjectForAsync("github:alice", "2026-Q1");
        var bob = await Pseudonyms(host).SubjectForAsync("github:bob", "2026-Q1");
        await Store(host).Append("m-1", ExpectedVersion.NoStream, [Invite(alice, "Alice"), Invite(bob, "Bob")]);
        await AdminOf(host).DestroyPseudonymPeriodAsync("2026-Q1");
        await StopHost(host);

        var later = await StartHost(probe, b => { InDatabase(b); b.Subscription<ReviewerMail>("mail"); });
        Assert.Equal(["Alice", "Bob"], (await Store(later).Load<Manuscript>("m-1")).State.Names);
        await WaitForCaughtUp(later, "mail");
        Assert.Equal(["Alice", "Bob"], probe.Delivered.Where(d => d.Consumer == "mail").Select(d => ((ReviewerInvited)d.Envelope.Event).ReviewerName));
        Assert.Equal("done", (await WaitForJob(later, await AdminOf(later).RebuildSnapshotsAsync("manuscript"))).Status);

        await WaitForJob(later, await AdminOf(later).EraseSubjectAsync(alice));
        Assert.Equal([null, "Bob"], (await Store(later).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task The_prefix_is_set_once_and_a_period_keeps_the_prefix_it_was_created_with()
    {
        var host = await StartHost(NewProbe(), InDatabase);
        await Pseudonyms(host).SubjectForAsync("github:alice", "2026-Q1");
        var renamed = await StartHost(NewProbe(), b => { InDatabase(b); b.PseudonymPrefix("user:"); });

        var changed = await Assert.ThrowsAsync<DeedboxException>(() => Pseudonyms(renamed).SubjectForAsync("github:alice", "2026-Q1"));
        var user = await Pseudonyms(renamed).SubjectForAsync("github:alice", "2026-Q2");
        await Store(renamed).Append("m-1", ExpectedVersion.NoStream, [Invite(user, "Alice")]);

        Assert.Equal(Errors.PseudonymPrefixChanged, changed.Code);
        Assert.Matches("^user:[a-z2-7]{26}$", user);

        // Erasure from an instance with the default prefix uses each period's own prefix.
        foreach (var job in await Pseudonyms(host).EraseIdentityAsync("github:alice"))
            await WaitForJob(host, job);
        Assert.Equal([null], (await Store(host).Load<Manuscript>("m-1")).State.Names);
    }

    [Fact]
    public async Task Pseudonyms_need_a_key_mode()
    {
        var host = await StartHost(NewProbe(), _ => { });

        var error = await Assert.ThrowsAsync<DeedboxException>(() => Pseudonyms(host).SubjectForAsync("github:alice", "2026-Q1"));

        Assert.Equal(Errors.NoKeyMode, error.Code);
        Assert.Equal(Errors.NoKeyMode, (await Assert.ThrowsAsync<DeedboxException>(() => AdminOf(host).EraseIdentityAsync("github:alice"))).Code);
    }

    internal static ReviewerInvited Invite(string subject, string name) => new("m", subject, name, null);

    internal static IPseudonyms Pseudonyms(IHost host, string tenant = "") => Scope(host, tenant).GetRequiredService<IPseudonyms>();

    internal static IEventStore Store(IHost host, string tenant = "") => Scope(host, tenant).GetRequiredService<IEventStore>();

    internal static IEventStoreAdmin AdminOf(IHost host) => host.Services.GetRequiredService<IEventStoreAdmin>();

    private static IServiceProvider Scope(IHost host, string tenant)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenant;
        return scope.ServiceProvider;
    }

    private async Task<PseudonymKeyRow> PseudonymRow(IHost host, string tenant, string period)
    {
        await using var connection = await OpenConnection();
        return (await RuntimeOf(host).Provider.ReadPseudonymKey(connection, null, tenant, period, Ct))!;
    }

    private async Task<List<JobRow>> Jobs(IHost host)
    {
        await using var connection = await OpenConnection();
        return await RuntimeOf(host).Provider.ReadJobs(connection, 100, Ct);
    }

    private async Task<List<string>> Strings(string sql, string json)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
            rows.Add(System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(0))![json]!.GetValue<string>());
        return rows;
    }
}

public sealed class PseudonymTokenTests
{
    [Fact]
    public void A_token_is_the_first_128_bits_of_hmac_sha256_in_lower_case_base32()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        Assert.Equal("5ovqv247376bgfu7d4vntyx23u", Pseudonymizer.Token(secret, "github:alice"));
        Assert.Equal("2l2qyranj7pfbzz6v4jnlpd6m4", Pseudonymizer.Token(secret, "email:alice@example.com"));
        Assert.Equal("mzxw6ytboi", Pseudonymizer.Base32("foobar"u8));
    }

    [Fact]
    public void Calendar_period_ids_use_the_utc_quarter_or_month()
    {
        var lateSeptemberInUtc = new DateTimeOffset(2026, 10, 1, 1, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-Q3", PseudonymPeriod.Quarter(lateSeptemberInUtc));
        Assert.Equal("2026-09", PseudonymPeriod.Month(lateSeptemberInUtc));
        Assert.Equal("2027-Q1", PseudonymPeriod.Quarter(new DateTimeOffset(2027, 3, 31, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal("2027-Q2", PseudonymPeriod.Quarter(new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("2026-12", PseudonymPeriod.Month(new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Invalid_identities_periods_and_prefixes_are_rejected_and_the_identity_is_never_echoed()
    {
        var padded = Assert.Throws<ArgumentException>(() => Pseudonymizer.ValidateIdentity(" email:secret-person@example.org"));
        Assert.Throws<ArgumentException>(() => Pseudonymizer.ValidateIdentity(""));
        Assert.DoesNotContain("secret-person", padded.Message, StringComparison.Ordinal);

        foreach (var period in new[] { "", " 2026-Q1", "2026 Q1", "-2026", new string('a', 65) })
            Assert.Throws<ArgumentException>(() => PseudonymPeriod.Validate(period));
        Assert.Equal("2026-Q1", PseudonymPeriod.Validate("2026-Q1"));
        Assert.Equal("fixed", PseudonymPeriod.Validate("fixed"));

        var builder = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => builder.AddDeedbox(b => b.PseudonymPrefix("per son:")));
        Assert.Throws<ArgumentException>(() => builder.AddDeedbox(b => b.PseudonymPrefix(new string('p', 75))));
    }

    [Fact]
    public async Task An_admin_written_for_an_older_version_still_compiles_and_says_it_does_not_support_pseudonyms()
    {
        IEventStoreAdmin admin = new OlderAdmin();

        await Assert.ThrowsAsync<NotSupportedException>(() => admin.EraseIdentityAsync("github:alice"));
        await Assert.ThrowsAsync<NotSupportedException>(() => admin.DestroyPseudonymPeriodAsync("2026-Q1"));
    }

    private sealed class OlderAdmin : IEventStoreAdmin
    {
        public Task<StoreStatus> GetStatusAsync(CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<JobInfo?> GetJobAsync(Guid jobId, CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<Guid> RebuildAsync(string projection, CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<Guid> SkipAsync(string consumer, Guid eventId, CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<Guid> EraseSubjectAsync(string subjectId, string tenantId = "", CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<Guid> RebuildSnapshotsAsync(string streamType, CancellationToken ct = default) => throw new InvalidOperationException();

        public Task<int> RewrapKeysAsync(IMasterKeyProvider target, CancellationToken ct = default) => throw new InvalidOperationException();

        public Task ShredTenantAsync(string tenantId, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
