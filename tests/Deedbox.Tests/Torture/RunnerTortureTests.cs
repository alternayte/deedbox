using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Torture;

public sealed class PostgresRunnerTortureTests(Databases databases) : RunnerTortureTests(databases, Db.Postgres);

public sealed class SqlServerRunnerTortureTests(Databases databases) : RunnerTortureTests(databases, Db.SqlServer);

public sealed class Dense : Projection
{
    public Dense(Probe probe) => On<ItemAdded>(async (_, ctx) =>
    {
        probe.MaybeFail(ctx.EventId);
        await TestTables.Insert(ctx.Connection, ctx.Transaction,
            $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, 'dense', @position)",
            ("id", ctx.EventId.ToString()), ("position", ctx.GlobalPosition!.Value));
    });
}

public sealed class Sparse : Projection
{
    public Sparse(Probe probe) => On<OrderPlaced>(async (_, ctx) =>
    {
        probe.MaybeFail(ctx.EventId);
        await TestTables.Insert(ctx.Connection, ctx.Transaction,
            $"INSERT INTO {probe.Table("applied")} (event_id, projection, position) VALUES (@id, 'sparse', @position)",
            ("id", ctx.EventId.ToString()), ("position", ctx.GlobalPosition!.Value));
    });
}

public sealed class Deliveries : Subscription
{
    public Deliveries(Probe probe) => On<ItemAdded>((_, ctx) =>
    {
        probe.MaybeFail(ctx.Envelope.EventId);
        probe.Deliveries.AddOrUpdate(ctx.Envelope.EventId, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    });
}

/// <summary>
/// Runners are stopped, restarted and have their database sessions killed mid-batch while three instances compete
/// for the same projections and handlers fail at random. Every event must still be applied exactly once by each
/// projection and delivered at least once by the subscription. An idle store must stay under its query budget.
/// </summary>
[Trait("Category", "Torture")]
public abstract class RunnerTortureTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private const string RunnerApp = "deedbox-runner";

    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("DEEDBOX_TORTURE_SCALE"), out var s) ? s : 1;

    private static void Consumers(DeedboxBuilder b) => b
        .Projection<Dense>("dense", Run.Async)
        .Projection<Sparse>("sparse", Run.Async)
        .Subscription<Deliveries>("deliveries");

    [Fact(Timeout = 900_000)]
    public async Task Killed_restarted_and_competing_runners_apply_every_event_exactly_once()
    {
        var probe = NewProbe();
        probe.FaultRate = 0.03;
        var writer = await StartHost(probe, Consumers, o => o.Enabled = false, applicationName: "deedbox-writer");
        var runners = new List<IHost>();
        for (var i = 0; i < 3; i++)
            runners.Add(await StartRunner(probe));

        var appends = 300 * Scale;
        var restarts = 0;
        var kills = 0;
        using var chaos = new CancellationTokenSource();
        var chaosTask = Task.Run(async () =>
        {
            var random = new Random();
            while (!chaos.IsCancellationRequested)
            {
                await Task.Delay(random.Next(100, 400), CancellationToken.None);
                if (random.Next(2) == 0)
                {
                    await KillRunnerSessions();
                    kills++;
                }
                else
                {
                    var victim = runners[random.Next(runners.Count)];
                    runners.Remove(victim);
                    await StopHost(victim);
                    runners.Add(await StartRunner(probe));
                    restarts++;
                }
            }
        });

        var items = 0;
        var orders = 0;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async w =>
        {
            var random = new Random(w);
            for (var i = 0; i < appends / 6; i++)
            {
                if (random.Next(100) < 3)
                {
                    await StoreOf(writer).Append($"order-{w}-{i}", ExpectedVersion.NoStream, [new OrderPlaced("x")]);
                    Interlocked.Increment(ref orders);
                }
                else
                {
                    var count = random.Next(1, 4);
                    await StoreOf(writer).Append($"cart-{w}", ExpectedVersion.Any, Enumerable.Range(0, count).Select(_ => (object)new ItemAdded("a", 1)));
                    Interlocked.Add(ref items, count);
                }

                await Task.Delay(random.Next(0, 15), CancellationToken.None);
            }
        }));

        await chaos.CancelAsync();
        await chaosTask;
        var head = await Head(writer);
        foreach (var name in new[] { "dense", "sparse", "deliveries" })
            await WaitForCheckpoint(writer, name, r => r.Position >= head && r.Status == "running");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Db}: {items} items, {orders} orders, {restarts} runner restarts, {kills} session kills, {probe.Faulted.Count} injected faults");
        Assert.True(orders > 0 && restarts + kills > 0);
        Assert.Equal(items, await AppliedCount("dense"));
        Assert.Equal(orders, await AppliedCount("sparse"));
        Assert.Equal(items, probe.Deliveries.Count);
        Assert.All(probe.Deliveries.Values, n => Assert.True(n >= 1));
    }

    [Fact(Timeout = 300_000)]
    public async Task An_idle_store_stays_under_the_query_budget()
    {
        // Budget: 30 statements per minute for each consumer, plus 30 for the jobs loop, with default polling.
        var host = await StartHost(NewProbe(), b =>
        {
            Consumers(b);
            b.Projection<InlineApplied>("inline", Run.Inline);
        }, o => (o.MinPollDelay, o.MaxPollDelay) = (new RunnerOptions().MinPollDelay, new RunnerOptions().MaxPollDelay));
        const int consumers = 4;

        await Task.Delay(TimeSpan.FromSeconds(12), Ct);
        var provider = RuntimeOf(host).Provider;
        var before = provider.StatementCount;
        await Task.Delay(TimeSpan.FromSeconds(30), Ct);
        var used = provider.StatementCount - before;

        var budget = (consumers + 1) * 30 / 2;
        TestContext.Current.TestOutputHelper?.WriteLine($"{Db}: {used} statements in 30 s while idle; budget {budget}.");
        Assert.InRange(used, 0, budget);
    }

    /// <summary>Starts a runner; a session kill can hit its start-up, so it retries.</summary>
    private async Task<IHost> StartRunner(Probe probe)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartHost(probe, Consumers, o => o.BatchSize = 20, RunnerApp);
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private async Task KillRunnerSessions()
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
}
