using System.Data.Common;

namespace Deedbox;

internal sealed class EventStore(DeedboxRuntime runtime, TransactionSource transactions) : IEventStore
{
    private const int MaxStreamIdLength = 200;

    private readonly string _tenantId = "";

    private DeedboxProvider Provider => runtime.Provider;

    public IEventStore UseTransaction(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return new EventStore(runtime, new CallerTransaction(transaction));
    }

    internal EventStore With(TransactionSource source) => new(runtime, source);

    public async Task<LoadResult<TState>> Load<TState>(string streamId, CancellationToken ct = default) where TState : IState<TState>
    {
        ValidateStreamId(streamId);
        var stream = runtime.Registry.ForState(typeof(TState));

        await using var lease = await transactions.BeginRead(Provider, ct);
        var loaded = await LoadCore(lease.Connection, lease.Transaction, stream, streamId, forUpdate: false, ct);
        return new LoadResult<TState>((TState)loaded.State, loaded.Version);
    }

    public async Task<AppendResult> Append(string streamId, ExpectedVersion expected, IEnumerable<object> events, CancellationToken ct = default)
    {
        ValidateStreamId(streamId);
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Append needs at least one event.", nameof(events));

        var stream = StreamOf(list, null);

        await using var lease = await transactions.BeginWrite(Provider, ct);
        while (true)
        {
            var row = await Provider.ReadStream(lease.Connection, lease.Transaction, _tenantId, streamId, forUpdate: true, withState: stream.Snapshots.Enabled, ct);
            var written = await Write(lease, stream, streamId, expected, row, known: null, list, ct);
            if (written is null)
                continue; // Lost a stream-creation race under ExpectedVersion.Any; the row now exists.

            await Commit(lease, written, ct);
            return new AppendResult(written.Version, written.Envelopes);
        }
    }

    public async Task<ExecuteResult<TState>> Execute<TState>(string streamId, Func<TState, IEnumerable<object>> decide, CancellationToken ct = default)
        where TState : IState<TState>
    {
        ValidateStreamId(streamId);
        ArgumentNullException.ThrowIfNull(decide);
        var stream = runtime.Registry.ForState(typeof(TState));

        await using var lease = await transactions.BeginWrite(Provider, ct);
        for (var attempt = 0; ; attempt++)
        {
            var loaded = await LoadCore(lease.Connection, lease.Transaction, stream, streamId, forUpdate: true, ct);
            var events = decide((TState)loaded.State).ToList();
            if (events.Count == 0)
            {
                await lease.Complete(ct);
                return new ExecuteResult<TState>((TState)loaded.State, loaded.Version, []);
            }

            StreamOf(events, stream);
            try
            {
                var written = (await Write(lease, stream, streamId, ExpectedVersion.Exact(loaded.Version), loaded.Row, loaded.State, events, ct))!;
                await Commit(lease, written, ct);
                return new ExecuteResult<TState>((TState)written.State!, written.Version, written.Envelopes);
            }
            catch (ConcurrencyException) when (attempt < runtime.Options.ExecuteRetries)
            {
                // Only stream creation can race here: an existing row is locked from load to commit.
            }
        }
    }

    /// <summary>
    /// Writes one append after the stream row was read with a lock. Returns null when a new stream lost a
    /// creation race and <paramref name="expected"/> is Any, so the caller reads again.
    /// </summary>
    private async Task<Written?> Write(
        Lease lease, StreamRegistration stream, string streamId, ExpectedVersion expected, StreamRow? row,
        object? known, List<object> events, CancellationToken ct)
    {
        var connection = lease.Connection;
        var transaction = lease.WriteTransaction;

        if (row is not null)
            CheckStreamType(row, stream, streamId);

        var current = row?.Version ?? 0;
        if (!expected.Matches(current))
            throw new ConcurrencyException(streamId, expected, current);

        var newVersion = current + events.Count;
        var snapshotUsable = row is not null && SnapshotUsable(row, stream);
        var snapshotDue = stream.Snapshots.IsDue(current, newVersion) || (stream.Snapshots.Enabled && !snapshotUsable);

        object? newState = null;
        Snapshot? snapshot = null;
        if (snapshotDue || known is not null)
        {
            var state = known ?? (row is null ? stream.Initial() : await Replay(connection, transaction, stream, streamId, row, ct));
            newState = stream.Fold(state, events);
            if (snapshotDue)
                snapshot = new Snapshot(DeedboxJson.Serialize(newState, stream.StateJson), stream.StateVersion, newVersion);
        }

        if (row is null)
        {
            if (!await Provider.InsertStream(connection, transaction, _tenantId, streamId, stream.Name, newVersion, snapshot, ct))
            {
                if (expected.IsAny && known is null)
                    return null;
                throw new ConcurrencyException(streamId, expected, await CurrentVersion(lease, streamId, ct));
            }
        }
        else if (!await Provider.UpdateStream(connection, transaction, _tenantId, streamId, current, newVersion, snapshot, ct))
        {
            throw new ConcurrencyException(streamId, expected, await CurrentVersion(lease, streamId, ct));
        }

        var occurredAt = runtime.Clock.GetUtcNow();
        var rows = new List<NewEvent>(events.Count);
        var registrations = new List<EventRegistration>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var registration = runtime.Registry.ForEvent(events[i].GetType());
            registrations.Add(registration);
            rows.Add(new NewEvent(Uuid7.New(), current + i + 1, registration.Name, registration.Version,
                DeedboxJson.Serialize(events[i], registration.Json), "{}"));
        }

        var newTypes = registrations
            .Select(r => new EventTypeRow(stream.Name, r.Name, r.Version))
            .Distinct()
            .Where(t => !runtime.KnownEventTypes.ContainsKey(t))
            .ToList();
        if (newTypes.Count > 0)
            await Provider.RecordEventTypes(connection, transaction, newTypes, ct);

        await lease.BeforeCounter(ct);
        var last = await Provider.InsertEvents(connection, transaction, _tenantId, streamId, stream.Name, occurredAt, rows, ct);

        var envelopes = new EventEnvelope[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            envelopes[i] = new EventEnvelope(rows[i].EventId, _tenantId, streamId, stream.Name, rows[i].Version,
                last - rows.Count + 1 + i, rows[i].EventType, rows[i].EventVersion, events[i], occurredAt);
        }

        return new Written(newVersion, newState, envelopes, newTypes);
    }

    private async Task Commit(Lease lease, Written written, CancellationToken ct)
    {
        await lease.Complete(ct);

        // Only a commit Deedbox made proves the rows exist; in a caller's transaction they are recorded again next time.
        if (lease.Commits)
        {
            foreach (var type in written.NewTypes)
                runtime.KnownEventTypes.TryAdd(type, true);
        }
    }

    private async Task<Loaded> LoadCore(DbConnection connection, DbTransaction? transaction, StreamRegistration stream, string streamId, bool forUpdate, CancellationToken ct)
    {
        var row = await Provider.ReadStream(connection, transaction, _tenantId, streamId, forUpdate, withState: stream.Snapshots.Enabled, ct);
        if (row is null)
            return new Loaded(stream.Initial(), 0, null);

        CheckStreamType(row, stream, streamId);
        var state = await Replay(connection, transaction, stream, streamId, row, ct);

        // A missing or outdated snapshot is rebuilt lazily. A write rebuilds it anyway, so only a plain load saves it here.
        if (!forUpdate && stream.Snapshots.Enabled && !SnapshotUsable(row, stream))
        {
            var snapshot = new Snapshot(DeedboxJson.Serialize(state, stream.StateJson), stream.StateVersion, row.Version);
            await Provider.SaveSnapshot(connection, transaction, _tenantId, streamId, snapshot, ct);
        }

        return new Loaded(state, row.Version, row);
    }

    /// <summary>The state at the row's version: the snapshot plus the events after it, or every event.</summary>
    private async Task<object> Replay(DbConnection connection, DbTransaction? transaction, StreamRegistration stream, string streamId, StreamRow row, CancellationToken ct)
    {
        object state;
        long after;
        if (SnapshotUsable(row, stream))
        {
            state = DeedboxJson.Deserialize(row.State!, stream.StateJson);
            after = row.StateAt;
        }
        else
        {
            state = stream.Initial();
            after = 0;
        }

        if (after >= row.Version)
            return state;

        await foreach (var stored in Provider.ReadStreamEvents(connection, transaction, _tenantId, streamId, after, row.Version, ct))
        {
            state = stream.Evolve(state, runtime.Registry.Decode(stored.EventType, stored.EventVersion, stored.Payload));
        }

        return state;
    }

    private async Task<long> CurrentVersion(Lease lease, string streamId, CancellationToken ct)
    {
        var row = await Provider.ReadStream(lease.Connection, lease.Transaction, _tenantId, streamId, forUpdate: false, withState: false, ct);
        return row?.Version ?? 0;
    }

    private static bool SnapshotUsable(StreamRow row, StreamRegistration stream) =>
        row.State is not null && row.StateVersion == stream.StateVersion;

    private static void CheckStreamType(StreamRow row, StreamRegistration stream, string streamId)
    {
        if (!string.Equals(row.StreamType, stream.Name, StringComparison.Ordinal))
        {
            throw new DeedboxException(Errors.StreamTypeMismatch,
                $"Stream '{streamId}' is a '{row.StreamType}' stream, not a '{stream.Name}' stream. Use the state type registered for '{row.StreamType}', or another stream ID.");
        }
    }

    private StreamRegistration StreamOf(List<object> events, StreamRegistration? expected)
    {
        var stream = expected;
        foreach (var e in events)
        {
            ArgumentNullException.ThrowIfNull(e, nameof(events));
            var registration = runtime.Registry.ForEvent(e.GetType());
            stream ??= registration.Stream;
            if (registration.Stream != stream)
            {
                throw new DeedboxException(Errors.MixedStreamTypes,
                    $"Event {e.GetType().Name} belongs to stream type '{registration.Stream.Name}', but this append is for '{stream.Name}'. One append writes one stream.");
            }
        }

        return stream!;
    }

    private static void ValidateStreamId(string streamId)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        if (streamId.Length is 0 or > MaxStreamIdLength || char.IsWhiteSpace(streamId[0]) || char.IsWhiteSpace(streamId[^1]))
        {
            throw new ArgumentException(
                $"Stream ID '{streamId}' is not valid. Use 1 to {MaxStreamIdLength} characters with no leading or trailing white space.",
                nameof(streamId));
        }
    }

    private sealed record Loaded(object State, long Version, StreamRow? Row);

    private sealed record Written(long Version, object? State, EventEnvelope[] Envelopes, List<EventTypeRow> NewTypes);
}
