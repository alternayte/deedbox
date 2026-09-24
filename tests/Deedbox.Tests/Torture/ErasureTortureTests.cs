using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.PersonalData;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Torture;

public sealed class PostgresErasureTortureTests(Databases databases) : ErasureTortureTests(databases, Db.Postgres);

public sealed class SqlServerErasureTortureTests(Databases databases) : ErasureTortureTests(databases, Db.SqlServer);

/// <summary>
/// Subjects are erased while other subjects' events keep arriving and the runners that execute erasure jobs are
/// killed and restarted. Afterwards nothing reads an erased subject's data, every stream they touched holds exactly
/// one SubjectErased per subject, and everyone else's data is intact.
/// </summary>
[Trait("Category", "Torture")]
public abstract class ErasureTortureTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private const int Subjects = 20;
    private const int Erased = 5;
    private const int Streams = 10;
    private const string RunnerApp = "deedbox-eraser";

    private static void Configure(DeedboxBuilder b)
    {
        Keys.Streams(b);
        b.Keys(k => k.StoreInDatabase());
    }

    [Fact(Timeout = 900_000)]
    public async Task Erasures_under_killed_runners_and_concurrent_appends_leave_nothing_readable()
    {
        var probe = NewProbe();
        var writer = await StartHost(probe, Configure, o => o.Enabled = false, applicationName: "deedbox-writer");
        var random = new Random();
        var touched = new HashSet<(int Subject, int Stream)>();
        var erasedEvents = 0;
        var keptBefore = 0;
        for (var i = 0; i < 120; i++)
        {
            var (subject, stream) = (random.Next(Subjects), random.Next(Streams));
            touched.Add((subject, stream));
            if (subject < Erased)
                erasedEvents++;
            else
                keptBefore++;
            await StoreOf(writer).Append($"m-{stream}", ExpectedVersion.Any, [Invite(subject, stream)]);
        }

        var runners = new List<IHost> { await StartRunner(probe), await StartRunner(probe) };
        var jobs = new List<Guid>();
        for (var s = 0; s < Erased; s++)
            jobs.Add(await writer.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync($"person:{s}"));

        using var stop = new CancellationTokenSource();
        var kept = 0;
        var appends = Task.Run(async () =>
        {
            var r = new Random(1);
            while (!stop.IsCancellationRequested)
            {
                var (subject, stream) = (Erased + r.Next(Subjects - Erased), r.Next(Streams));
                await StoreOf(writer).Append($"m-{stream}", ExpectedVersion.Any, [Invite(subject, stream)]);
                Interlocked.Increment(ref kept);
            }
        });
        var chaos = Task.Run(async () =>
        {
            var r = new Random(2);
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(r.Next(50, 250), CancellationToken.None);
                if (r.Next(2) == 0)
                {
                    await KillSessions();
                }
                else
                {
                    var victim = runners[r.Next(runners.Count)];
                    runners.Remove(victim);
                    await StopHost(victim);
                    runners.Add(await StartRunner(probe));
                }
            }
        });

        foreach (var job in jobs)
        {
            var row = await WaitForJob(writer, job);
            Assert.True(row.Status == "done", $"Erasure job {job} ended {row.Status}: {row.Progress}");
        }
        await stop.CancelAsync();
        await Task.WhenAll(appends, chaos);

        var store = StoreOf(writer);
        var names = new List<string?>();
        for (var stream = 0; stream < Streams; stream++)
        {
            await Execute($"UPDATE {Table("streams")} SET state = NULL WHERE stream_id = 'm-{stream}'");
            names.AddRange((await store.Load<Manuscript>($"m-{stream}")).State.Names);
        }

        Assert.DoesNotContain(names, n => n?.StartsWith("erase-", StringComparison.Ordinal) == true);
        Assert.Equal(keptBefore + kept, names.Count(n => n?.StartsWith("keep-", StringComparison.Ordinal) == true));
        Assert.Equal(erasedEvents, names.Count(n => n is null));
        var tombstones = await Pairs($"SELECT stream_id, CAST(payload AS varchar(400)) FROM {Table("events")} WHERE event_type = 'deedbox.subject_erased'");
        var expected = touched.Where(t => t.Subject < Erased).Select(t => ($"m-{t.Stream}", $"person:{t.Subject}")).Distinct().Order().ToList();
        Assert.Equal(expected, tombstones.Select(t => (t.Stream, System.Text.Json.Nodes.JsonNode.Parse(t.Payload)!["subjectId"]!.GetValue<string>())).Order().ToList());
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("subject_keys")} WHERE subject_id IN ({string.Join(',', Enumerable.Range(0, Erased).Select(s => $"'person:{s}'"))})"));
        TestContext.Current.TestOutputHelper?.WriteLine($"{Db}: {expected.Count} tombstones, {kept} appends during erasure.");
    }

    private static ReviewerInvited Invite(int subject, int stream) =>
        new($"m-{stream}", $"person:{subject}", (subject < Erased ? "erase-" : "keep-") + subject, null);

    private async Task<IHost> StartRunner(Probe probe)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartHost(probe, Configure, o => o.MaxPollDelay = TimeSpan.FromMilliseconds(50), RunnerApp);
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private async Task KillSessions()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = Db == Db.Postgres
            ? $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '{RunnerApp}'"
            : $"""
               DECLARE @sql nvarchar(max) = N'';
               SELECT @sql += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions
               WHERE program_name = N'{RunnerApp}' AND session_id <> @@SPID;
               EXEC (@sql);
               """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<(string Stream, string Payload)>> Pairs(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(Ct))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }
}
