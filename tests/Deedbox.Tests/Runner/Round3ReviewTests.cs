using System.Data.Common;
using System.Text.Json.Nodes;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deedbox.Tests.Runner;

public sealed class PostgresRound3ReviewTests(Databases databases) : Round3ReviewTests(databases, Db.Postgres);

public sealed class SqlServerRound3ReviewTests(Databases databases) : Round3ReviewTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiRound3ReviewTests(Databases databases) : Round3ReviewTests(databases, Db.SqlServerRcsi);

/// <summary>Switches for the consumers of these tests.</summary>
public sealed class Round3
{
    public TaskCompletionSource ResetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ResetRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The deployed version of a projection: it handles orders only. Its reset waits for the test.</summary>
public sealed class HeldResetV1 : Projection
{
    private readonly Round3 _switches;

    public HeldResetV1(Review review, Round3 switches)
    {
        _switches = switches;
        On<OrderPlaced>((_, ctx) => ShippedV1.Saw(review, "v1", ctx, "order.order_placed"));
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        _switches.ResetReached.TrySetResult();
        await _switches.ResetRelease.Task;
    }
}

public abstract class Round3ReviewTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private static async Task Sql(DbConnection connection, DbTransaction? transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<InstanceRow?> Instance(Microsoft.Extensions.Hosting.IHost host, Guid id)
    {
        await using var connection = await OpenConnection();
        return (await RuntimeOf(host).Provider.ReadInstances(connection, null, TimeSpan.FromSeconds(6), Ct)).FirstOrDefault(i => i.Id == id);
    }

    [Fact]
    public async Task An_instance_of_the_new_version_that_joins_while_the_old_version_ends_a_rebuild_keeps_the_old_version_from_passing_its_events()
    {
        // A rolling deploy: the old version handles orders, the new one also handles added items. A first pod of the
        // new version ran, so the checkpoint records both, and stopped again.
        var review = new Review();
        var switches = new Round3();
        var old = await StartHost(NewProbe(), b => b.Projection<HeldResetV1>("shipped", Run.Async), services: s => s.AddSingleton(review).AddSingleton(switches));
        var first = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), o => o.Enabled = false, services: s => s.AddSingleton(review));
        var before = await Checkpoint(old, "shipped");
        await StopHost(first);

        // The operator rebuilds the projection. The old instance takes the job. The test holds the job between its
        // last statement in Rebuild and its commit: a share lock on the job row makes the update of the row wait.
        var job = await Enqueue(old, Jobs.Rebuild, new JsonObject { ["projection"] = "shipped" });
        await switches.ResetReached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await using var hold = await OpenConnection();
        await using var held = await hold.BeginTransactionAsync(Ct);
        await Sql(hold, held, Db == Db.Postgres
            ? $"LOCK TABLE {Table("jobs")} IN SHARE MODE"
            : $"SELECT id FROM {Table("jobs")} WITH (HOLDLOCK, ROWLOCK) WHERE id = '{job:D}'");
        switches.ResetRelease.TrySetResult();
        var waiting = Db == Db.Postgres
            ? $"SELECT count(*) FROM pg_locks l JOIN pg_class c ON c.oid = l.relation JOIN pg_namespace n ON n.oid = c.relnamespace WHERE NOT l.granted AND l.locktype = 'relation' AND c.relname = 'jobs' AND n.nspname = '{Schema}'"
            : $"SELECT COUNT(*) FROM sys.dm_tran_locks l JOIN sys.partitions p ON p.hobt_id = l.resource_associated_entity_id WHERE l.resource_type = 'KEY' AND l.request_status IN ('WAIT', 'CONVERT') AND p.object_id = OBJECT_ID('{Table("jobs")}')";
        await WaitFor(async () => await Scalar<int>(waiting) > 0, "the rebuild to wait for its job row");

        // A second pod of the new version starts now. It either joins at once, or waits for the rebuild.
        var joining = StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), o => o.Enabled = false, services: s => s.AddSingleton(review));
        await Task.WhenAny(joining, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        var joinedBeforeCommit = joining.IsCompleted;

        await held.RollbackAsync(Ct);
        Assert.Equal("done", (await WaitForJob(old, job)).Status);
        var next = await joining;
        var rebuilt = await Checkpoint(old, "shipped");
        Assert.True((await Instance(old, RuntimeOf(next).InstanceId))?.Live, "The new instance is live.");

        // Both versions run. An item is added: only the new version handles it.
        await StoreOf(next).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
        try
        {
            await WaitFor(async () => (await Checkpoint(old, "shipped")).Position >= 1, "the old instance to move the checkpoint", seconds: 5);
        }
        catch (TimeoutException)
        {
        }

        var moved = await Checkpoint(old, "shipped");

        // The deploy finishes: only the new version runs.
        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), services: s => s.AddSingleton(review));
        await WaitForCaughtUp(after, "shipped");

        Assert.True(review.Seen.Any(s => s.Position == 1),
            $"No version applied cart.item_added at position 1, appended while an instance of the new version was live. The new instance joined before the rebuild committed: {joinedBeforeCommit}. " +
            $"The checkpoint recorded {before.Handles} before the rebuild and {rebuilt.Handles} after it. With both instances live, the old one moved the checkpoint to {moved.Position} ({moved.Status}). " +
            $"Applied: {string.Join(", ", review.Seen)}.");
    }

    [Fact]
    public async Task An_instance_that_joined_again_after_a_rebuild_by_the_old_version_keeps_the_old_version_from_passing_its_events()
    {
        // A rolling deploy: the old version handles orders, the new one also handles added items.
        var review = new Review();
        var old = await StartHost(NewProbe(), b => b.Projection<RebuiltV1>("shipped", Run.Async), services: s => s.AddSingleton(review));
        var next = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), o => o.Enabled = false, services: s => s.AddSingleton(review));
        var provider = RuntimeOf(old).Provider;
        var id = RuntimeOf(next).InstanceId;
        var before = await Checkpoint(old, "shipped");

        // The new instance loses its heartbeat row, as when a cut-over evicts it or the row ages out during a pause.
        // The test holds the position counter, so the instance cannot join again before the rebuild is done.
        await using var counter = await OpenConnection();
        await using var counted = await counter.BeginTransactionAsync(Ct);
        await provider.LockCounter(counter, counted, Ct);
        await using var gate = await OpenConnection();
        await using (var evict = await gate.BeginTransactionAsync(Ct))
        {
            await provider.EvictInstances(gate, evict, [id], Ct);
            await evict.CommitAsync(Ct);
        }

        // The operator rebuilds the projection; the old instance runs the job and sees no row of the new version.
        Assert.Equal("done", (await WaitForJob(old, await Enqueue(old, Jobs.Rebuild, new JsonObject { ["projection"] = "shipped" }))).Status);
        var rebuilt = await Checkpoint(old, "shipped");

        // The new instance joins again at its next heartbeat. Nothing records what its version handles a second
        // time, so the checkpoint's record stays narrow; the heartbeat rows must keep the old version back.
        await counted.RollbackAsync(Ct);
        await WaitFor(async () => (await Instance(old, id)) is { Live: true }, "the new instance to join again", seconds: 30);

        var joined = await Checkpoint(old, "shipped");
        Assert.True((await Instance(old, id))?.Live, "The new instance is live.");

        // The new instance appends an item in the app's own transaction. Only the new version handles it.
        await using (var connection = await OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await StoreOf(next).UseTransaction(transaction).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1)]);
            await transaction.CommitAsync(Ct);
        }

        try
        {
            await WaitFor(async () => (await Checkpoint(old, "shipped")).Position >= 1, "the old instance to move the checkpoint", seconds: 5);
        }
        catch (TimeoutException)
        {
        }

        var moved = await Checkpoint(old, "shipped");

        // The deploy finishes: only the new version runs.
        await StopHost(old);
        await StopHost(next);
        var after = await StartHost(NewProbe(), b => b.Projection<RebuiltV2>("shipped", Run.Async), services: s => s.AddSingleton(review));
        await WaitForCaughtUp(after, "shipped");

        Assert.True(review.Seen.Any(s => s.Position == 1),
            $"No version applied cart.item_added at position 1, which the new instance appended while it was live. " +
            $"The checkpoint recorded {before.Handles} at first, {rebuilt.Handles} after the rebuild, and {joined.Handles} after the new instance joined again. " +
            $"With both instances live, the old one moved the checkpoint to {moved.Position} ({moved.Status}). Applied: {string.Join(", ", review.Seen)}.");
    }
}
