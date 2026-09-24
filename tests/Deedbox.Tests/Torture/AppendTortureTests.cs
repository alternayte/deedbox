using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Torture;

public sealed class PostgresAppendTortureTests(Databases databases) : AppendTortureTests(databases, Db.Postgres);

public sealed class SqlServerAppendTortureTests(Databases databases) : AppendTortureTests(databases, Db.SqlServer);

public sealed class SqlServerRcsiAppendTortureTests(Databases databases) : AppendTortureTests(databases, Db.SqlServerRcsi);

/// <summary>
/// Many writers append to shared streams in every transaction mode, with random rollbacks and long
/// transactions that hold the position counter, while observers tail the global order. Afterwards the
/// store must hold exactly the committed events, at gapless positions in commit order, in per-stream order.
/// DEEDBOX_TORTURE_SCALE multiplies the work; DEEDBOX_TORTURE_SEED replays a run.
/// </summary>
[Trait("Category", "Torture")]
public abstract class AppendTortureTests(Databases databases, Db db) : StoreTest(databases, db)
{
    private const int Writers = 16;
    private const int Observers = 3;
    private const int Streams = 24;

    private static int Scale => int.TryParse(Environment.GetEnvironmentVariable("DEEDBOX_TORTURE_SCALE"), out var s) ? s : 1;

    [Fact(Timeout = 900_000)]
    public async Task Concurrent_appends_with_rollbacks_and_long_transactions_keep_every_guarantee()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("DEEDBOX_TORTURE_SEED"), out var fixedSeed) ? fixedSeed : Random.Shared.Next();
        var operations = 40 * Scale;
        Log($"{Db}: seed {seed}, {Writers} writers x {operations} operations over {Streams} streams, {Observers} observers");

        var store = await Store();
        var streams = Enumerable.Range(0, Streams).Select(i => $"torture-{i}").ToArray();
        var run = new Run();
        var clock = Stopwatch.StartNew();

        using var stop = new CancellationTokenSource();
        var observers = Enumerable.Range(0, Observers).Select(_ => Observe(run, stop.Token)).ToList();
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Write(store, streams, new Random(seed + w), operations, run)));
        var writeTime = clock.Elapsed;
        await stop.CancelAsync();
        var observed = await Task.WhenAll(observers);

        Log($"{run.Committed.Count} appends committed, {run.Discarded.Count} discarded ({run.Counts}), {writeTime.TotalSeconds:F1}s, " +
            $"{run.Committed.Count / writeTime.TotalSeconds:F0} commits/s");
        foreach (var violation in run.Violations)
            Log(violation);
        if (!run.Violations.IsEmpty)
        {
            // Which appends wrote the positions around each gap: one append, or several transactions.
            var byPosition = (await ReadAllEvents()).ToDictionary(e => e.Position);
            foreach (var gap in run.Gaps)
            {
                for (var p = gap.After; p <= gap.Seen; p++)
                {
                    if (byPosition.TryGetValue(p, out var e))
                        Log($"  position {p}: stream {e.StreamId} version {e.Version} event {e.EventId}");
                }
            }
        }

        Assert.Empty(run.Violations);
        Assert.True(run.Discarded.Count > 0 && run.LongTransactions > 0, "The run must include rollbacks and long transactions.");

        var table = await ReadAllEvents();
        var committed = run.Committed.SelectMany(a => a).ToList();

        // Positions are gapless: 1..N, and the counter is N.
        Assert.Equal(Enumerable.Range(1, table.Count).Select(p => (long)p), table.Select(e => e.Position));
        Assert.Equal((long)table.Count, await Scalar<long>($"SELECT value FROM {Table("position")}"));

        // Exactly the committed events are stored, at the positions and versions their envelopes reported.
        var stored = table.ToDictionary(e => e.EventId);
        Assert.Equal(committed.Count, table.Count);
        foreach (var e in committed)
            Assert.Equal((e.StreamId, e.Version, e.Position), (stored[e.EventId].StreamId, stored[e.EventId].Version, stored[e.EventId].Position));
        Assert.DoesNotContain(run.Discarded.SelectMany(d => d), stored.ContainsKey);

        // An append's events sit together, in order.
        foreach (var append in run.Committed)
            Assert.Equal(Enumerable.Range(0, append.Length).Select(i => append[0].Position + i), append.Select(e => e.Position));

        // Per-stream order: in global order, each stream's versions run 1..n.
        foreach (var stream in table.GroupBy(e => e.StreamId))
            Assert.Equal(Enumerable.Range(1, stream.Count()).Select(v => (long)v), stream.Select(e => e.Version));

        // Every observer saw the final order as it grew, never with a gap.
        foreach (var sequence in observed)
            Assert.Equal(table.Select(e => e.EventId), sequence);

        // Stored state matches the events.
        foreach (var id in streams)
        {
            var events = committed.Where(e => e.StreamId == id).ToList();
            var (state, version) = await store.Load<Counter>(id);
            Assert.Equal((events.Count, events.Sum(e => (long)e.By)), (state.Count, state.Total));
            Assert.Equal(events.Count, version);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task A_long_transaction_holds_later_appends_and_its_rollback_leaves_no_gap()
    {
        var store = await Store();
        await using var connection = await OpenConnection();
        var transaction = await connection.BeginTransactionAsync(Ct);
        var held = await store.UseTransaction(transaction).Append("held", ExpectedVersion.NoStream, [new Incremented(1), new Incremented(2)]);
        Assert.Equal([1L, 2L], held.Events.Select(e => e.GlobalPosition));

        var waiting = store.Append("waiting", ExpectedVersion.NoStream, [new Incremented(3)]);
        await Task.Delay(500, Ct);
        Assert.False(waiting.IsCompleted, "An append must wait while another transaction holds the position counter.");

        await transaction.RollbackAsync(Ct);
        await transaction.DisposeAsync();
        var appended = await waiting;

        Assert.Equal(1, appended.Events[0].GlobalPosition);
        Assert.Equal(0, (await store.Load<Counter>("held")).Version);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_long_transaction_holds_later_appends_and_its_commit_orders_them_after_it()
    {
        var store = await Store();
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await store.UseTransaction(transaction).Append("held", ExpectedVersion.NoStream, [new Incremented(1), new Incremented(2)]);

        var waiting = store.Append("waiting", ExpectedVersion.NoStream, [new Incremented(3)]);
        await Task.Delay(500, Ct);
        Assert.False(waiting.IsCompleted, "An append must wait while another transaction holds the position counter.");

        await transaction.CommitAsync(Ct);
        var appended = await waiting;

        Assert.Equal(3, appended.Events[0].GlobalPosition);
    }

    private async Task Write(IEventStore store, string[] streams, Random random, int operations, Run run)
    {
        for (var i = 0; i < operations; i++)
        {
            var stream = streams[random.Next(streams.Length)];
            var events = Enumerable.Range(0, random.Next(1, 5)).Select(_ => new Incremented(random.Next(1, 100))).ToList();
            var roll = random.Next(100);
            try
            {
                if (roll < 25)
                {
                    await OwnedExecute(store, stream, events, fail: random.Next(10) == 0, run);
                }
                else if (roll < 45)
                {
                    run.Commit((await store.Append(stream, ExpectedVersion.Any, events)).Events, events);
                    run.Count("owned append");
                }
                else if (roll < 95)
                {
                    await InCallerTransaction(store, stream, events, rollback: random.Next(4) == 0, hold: TimeSpan.Zero, useExecute: roll >= 85, run);
                }
                else
                {
                    Interlocked.Increment(ref run.LongTransactions);
                    await InCallerTransaction(store, stream, events, rollback: random.Next(2) == 0,
                        hold: TimeSpan.FromMilliseconds(random.Next(50, 250)), useExecute: false, run);
                }
            }
            catch (Exception ex)
            {
                run.Violations.Enqueue($"Writer failed: {ex}");
                return;
            }
        }
    }

    private static async Task OwnedExecute(IEventStore store, string stream, List<Incremented> events, bool fail, Run run)
    {
        if (!fail)
        {
            run.Commit((await store.Execute<Counter>(stream, _ => events)).Events, events);
            run.Count("owned execute");
            return;
        }

        // A decision that throws after the stream row is locked must leave nothing behind.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Execute<Counter>(stream, _ => throw new InvalidOperationException("decide failed")));
        run.Count("failed execute");
    }

    private async Task InCallerTransaction(IEventStore store, string stream, List<Incremented> events, bool rollback, TimeSpan hold, bool useExecute, Run run)
    {
        await using var connection = await OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var bound = store.UseTransaction(transaction);
        var envelopes = useExecute
            ? (await bound.Execute<Counter>(stream, _ => events)).Events
            : (await bound.Append(stream, ExpectedVersion.Any, events)).Events;

        if (hold > TimeSpan.Zero)
            await Task.Delay(hold, Ct);

        if (rollback)
        {
            await transaction.RollbackAsync(Ct);
            run.Discarded.Add([.. envelopes.Select(e => e.EventId)]);
            run.Count(hold > TimeSpan.Zero ? "long rollback" : "rollback");
            return;
        }

        await transaction.CommitAsync(Ct);
        run.Commit(envelopes, events);
        run.Count(hold > TimeSpan.Zero ? "long commit" : useExecute ? "caller execute" : "caller append");

        // Commit order is position order: once this commit returns, every lower position is committed.
        var last = envelopes[^1].GlobalPosition;
        var below = await Scalar<long>($"SELECT COUNT(*) FROM {Table("events")} WHERE global_position <= {last.ToString(CultureInfo.InvariantCulture)}");
        if (below != last)
            run.Violations.Enqueue($"After committing position {last}, only {below} positions at or below it were committed.");
    }

    /// <summary>Tails the global order the way an async projection would, and records any gap it sees.</summary>
    private async Task<List<Guid>> Observe(Run run, CancellationToken stop)
    {
        var seen = new List<Guid>();
        long last = 0;
        await using var connection = await OpenConnection();
        await using var provider = CreateProvider();
        var draining = false;
        while (true)
        {
            var batch = await ReadAfter(provider, connection, last);
            foreach (var (position, eventId) in batch)
            {
                if (position != last + 1)
                {
                    run.Violations.Enqueue($"Observer saw position {position} right after {last}.");
                    run.Gaps.Enqueue((last, position));
                }
                last = position;
                seen.Add(eventId);
            }

            if (batch.Count > 0)
                continue;
            if (draining)
                return seen;
            if (stop.IsCancellationRequested)
                draining = true;
            else
                await Task.Delay(2, CancellationToken.None);
        }
    }

    // Observers read the way the runner does: through the provider, which bounds the read by the committed head.
    // A raw range scan under SQL Server's locking READ COMMITTED can skip positions that an append reuses after a rollback.
    private static async Task<List<(long Position, Guid EventId)>> ReadAfter(DeedboxProvider provider, DbConnection connection, long after)
    {
        var events = await provider.ReadEventsAfter(connection, null, after, 500, [], CancellationToken.None);
        return [.. events.Select(e => (e.GlobalPosition, e.EventId))];
    }

    private async Task<List<(long Position, Guid EventId, string StreamId, long Version)>> ReadAllEvents()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT global_position, event_id, stream_id, version FROM {Table("events")} ORDER BY global_position";
        var rows = new List<(long, Guid, string, long)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3)));
        return rows;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    private sealed record CommittedEvent(Guid EventId, string StreamId, long Version, long Position, int By);

    private sealed class Run
    {
        public ConcurrentQueue<(long After, long Seen)> Gaps { get; } = new();

        public int LongTransactions;
        private readonly ConcurrentDictionary<string, int> _counts = new();

        public ConcurrentBag<CommittedEvent[]> Committed { get; } = [];
        public ConcurrentBag<Guid[]> Discarded { get; } = [];
        public ConcurrentQueue<string> Violations { get; } = new();

        public string Counts => string.Join(", ", _counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"));

        public void Count(string kind) => _counts.AddOrUpdate(kind, 1, (_, n) => n + 1);

        public void Commit(IReadOnlyList<EventEnvelope> envelopes, List<Incremented> events) =>
            Committed.Add([.. envelopes.Select((e, i) => new CommittedEvent(e.EventId, e.StreamId, e.Version, e.GlobalPosition, events[i].By))]);
    }
}
