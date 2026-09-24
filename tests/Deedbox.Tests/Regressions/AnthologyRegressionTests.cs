using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Deedbox.Tests.Regressions;

public sealed class PostgresAnthologyRegressionTests(Databases databases) : AnthologyRegressionTests(databases, Db.Postgres)
{
    /// <summary>Anthology sent NOTIFY from a trigger and again from code inside catch {}. Deedbox notifies from the trigger only.</summary>
    [Fact]
    [Trait("Regression", "Anthology: NOTIFY sent twice")]
    public async Task One_append_sends_one_notification()
    {
        var host = await StartHost(NewProbe(), _ => { });
        await using var listener = new NpgsqlConnection(ConnectionString);
        await listener.OpenAsync(Ct);
        var received = 0;
        listener.Notification += (_, _) => Interlocked.Increment(ref received);
        await using (var listen = new NpgsqlCommand($"LISTEN dbx_{Schema}", listener))
            await listen.ExecuteNonQueryAsync(Ct);

        await StoreOf(host).Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1), new ItemAdded("c", 1)]);
        await StoreOf(host).Append("cart-2", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
                await listener.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(2, received);
    }
}

public sealed class SqlServerAnthologyRegressionTests(Databases databases) : AnthologyRegressionTests(databases, Db.SqlServer);

public sealed class RenamedApplied(Probe probe) : Applied(probe, "async");

/// <summary>
/// Each Anthology bug from the design doc's lessons table, as a named test. Bugs already pinned elsewhere carry the
/// same trait: duplicate mapping (RegistryTests), snapshot shape change (SnapshotTests), inline plus async
/// registration (InlineProjectionTests), column types (SchemaTests) and endless poison retries (RunnerTests).
/// </summary>
public abstract class AnthologyRegressionTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    /// <summary>
    /// Anthology filtered on xid but checkpointed by global position: a transaction that took its xid first and its
    /// position later was skipped for ever. Here a transaction starts first and writes, another appends and commits,
    /// the runner moves past it, and the first transaction's later append must still be applied.
    /// </summary>
    [Fact]
    [Trait("Regression", "Anthology: xid before position skipped")]
    public async Task A_transaction_that_starts_first_but_appends_last_is_not_skipped()
    {
        var host = await StartHost(NewProbe(), b => b.Projection<AsyncApplied>("applied", Run.Async));
        await using var connection = await OpenConnection();
        await using var early = await connection.BeginTransactionAsync(Ct);
        await using (var write = connection.CreateCommand())
        {
            write.Transaction = early;
            write.CommandText = $"INSERT INTO ef_tests.audit (id, what) VALUES ('{Guid.NewGuid()}', 'takes a transaction ID first')";
            await write.ExecuteNonQueryAsync(Ct);
        }

        await StoreOf(host).Append("late-start", ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);
        await WaitFor(async () => await AppliedCount("async") == 1, "the later transaction's event");

        await StoreOf(host).UseTransaction(early).Append("early-start", ExpectedVersion.NoStream, [new ItemAdded("b", 1)]);
        await early.CommitAsync(Ct);

        await WaitFor(async () => await AppliedCount("async") == 2, "the earlier transaction's event");
    }

    /// <summary>Anthology keyed checkpoints by the projection's class name, so a rename reset its progress.</summary>
    [Fact]
    [Trait("Regression", "Anthology: class rename reset progress")]
    public async Task Renaming_a_projection_class_keeps_its_checkpoint()
    {
        var probe = NewProbe();
        var before = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(before).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        await WaitForCaughtUp(before, "applied");
        await StopHost(before);

        var after = await StartHost(probe, b => b.Projection<RenamedApplied>("applied", Run.Async));
        await StoreOf(after).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("c", 1)]);
        await WaitForCaughtUp(after, "applied");

        Assert.Equal(3, await AppliedCount("async"));
        Assert.Equal("running", (await Checkpoint(after, "applied")).Status);
    }

    /// <summary>Anthology registered Map&lt;ItemRated&gt; twice; the second silently replaced the first.</summary>
    [Fact]
    [Trait("Regression", "Anthology: duplicate mapping overwrote a name")]
    public void Registering_an_event_under_a_second_name_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .Stream<Cart>(s => s.Event<ItemAdded>("tracking.item.rated").Event<ItemAdded>("tracking.item.rerated"))));

        Assert.Equal("DBX005", error.Code);
    }
}
