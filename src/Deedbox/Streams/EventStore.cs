using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox;

internal sealed class EventStore(
    DeedboxRuntime runtime, TransactionSource transactions, IServiceProvider services, DeedboxContext context, Func<EventMetadata, EventMetadata>? metadata)
    : IEventStore
{
    private const int MaxStreamIdLength = 200;

    private DeedboxProvider Provider => runtime.Provider;

    private string TenantId => DeedboxContext.ValidTenant(context.TenantId);

    public IEventStore UseTransaction(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return With(new CallerTransaction(transaction));
    }

    public IEventStore WithMetadata(Func<EventMetadata, EventMetadata> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = metadata;
        return new EventStore(runtime, transactions, services, context, previous is null ? change : m => change(previous(m)));
    }

    internal EventStore With(TransactionSource source) => new(runtime, source, services, context, metadata);

    public async Task<LoadResult<TState>> Load<TState>(string streamId, CancellationToken ct = default) where TState : IState<TState>
    {
        ValidateStreamId(streamId);
        var stream = runtime.Registry.ForState(typeof(TState));

        var tenantId = TenantId;
        using var activity = StartActivity("deedbox.load", stream.Name, streamId);
        await using var lease = await transactions.BeginRead(Provider, ct);
        var loaded = await LoadCore(lease.Connection, lease.Transaction, tenantId, stream, streamId, forUpdate: false, ct);
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
        var tenantId = TenantId;
        using var activity = StartActivity("deedbox.append", stream.Name, streamId);
        activity?.SetTag("deedbox.events", list.Count);
        var started = Stopwatch.GetTimestamp();

        try
        {
            await using var lease = await transactions.BeginWrite(Provider, ct);
            while (true)
            {
                var row = await Provider.ReadStream(lease.Connection, lease.Transaction, tenantId, streamId, forUpdate: true, withState: stream.Snapshots.Enabled, ct);
                var written = await Write(lease, tenantId, stream, streamId, expected, row, known: null, list, ct);
                if (written is null)
                    continue; // Lost a stream-creation race under ExpectedVersion.Any; the row now exists.

                await Commit(lease, written, ct);
                Appended(stream, written, started);
                return new AppendResult(written.Version, written.Envelopes);
            }
        }
        catch (ConcurrencyException)
        {
            DeedboxDiagnostics.Conflicts.Add(1, DeedboxDiagnostics.Tag("deedbox.stream_type", stream.Name));
            throw;
        }
    }

    public async Task<ExecuteResult<TState>> Execute<TState>(string streamId, Func<TState, IEnumerable<object>> decide, CancellationToken ct = default)
        where TState : IState<TState>
    {
        ValidateStreamId(streamId);
        ArgumentNullException.ThrowIfNull(decide);
        var stream = runtime.Registry.ForState(typeof(TState));
        var tenantId = TenantId;
        using var activity = StartActivity("deedbox.execute", stream.Name, streamId);
        var started = Stopwatch.GetTimestamp();

        await using var lease = await transactions.BeginWrite(Provider, ct);
        for (var attempt = 0; ; attempt++)
        {
            var loaded = await LoadCore(lease.Connection, lease.Transaction, tenantId, stream, streamId, forUpdate: true, ct);
            var events = decide((TState)loaded.State).ToList();
            if (events.Count == 0)
            {
                await lease.Complete(ct);
                return new ExecuteResult<TState>((TState)loaded.State, loaded.Version, []);
            }

            StreamOf(events, stream);
            try
            {
                var written = (await Write(lease, tenantId, stream, streamId, ExpectedVersion.Exact(loaded.Version), loaded.Row, loaded.State, events, ct))!;
                await Commit(lease, written, ct);
                Appended(stream, written, started);
                return new ExecuteResult<TState>((TState)written.State!, written.Version, written.Envelopes);
            }
            catch (ConcurrencyException)
            {
                DeedboxDiagnostics.Conflicts.Add(1, DeedboxDiagnostics.Tag("deedbox.stream_type", stream.Name));
                if (attempt >= runtime.Options.ExecuteRetries)
                    throw;

                // Only stream creation can race here: an existing row is locked from load to commit.
                DeedboxDiagnostics.ExecuteRetries.Add(1, DeedboxDiagnostics.Tag("deedbox.stream_type", stream.Name));
            }
        }
    }

    public async Task DeleteStream(string streamId, CancellationToken ct = default)
    {
        ValidateStreamId(streamId);
        var tenantId = TenantId;
        using var activity = StartActivity("deedbox.delete_stream", null, streamId);

        await using var lease = await transactions.BeginWrite(Provider, ct);
        var row = await Provider.ReadStream(lease.Connection, lease.Transaction, tenantId, streamId, forUpdate: true, withState: false, ct);
        if (row is null || row.DeletedAt is not null)
        {
            await lease.Complete(ct);
            return;
        }

        var stream = RegisteredStream(row.StreamType);
        var tombstone = row.Version + 1;
        var written = await Write(lease, tenantId, stream, streamId, ExpectedVersion.Exact(row.Version), row, known: null, [new StreamDeleted()], ct,
            beforeCounter: () => Provider.DeleteStreamData(lease.Connection, lease.WriteTransaction, tenantId, streamId, tombstone, ct),
            skipSnapshot: true);
        await Commit(lease, written!, ct);
    }

    /// <summary>
    /// One stream's part of an erasure: rebuild its state from the now-redacted events, append SubjectErased, and store
    /// the new state, in one transaction. Removing the subject pair first makes a repeated run a no-op.
    /// </summary>
    internal async Task EraseFromStream(string streamId, string subjectId, CancellationToken ct)
    {
        var tenantId = TenantId;
        using var activity = StartActivity("deedbox.erase_stream", null, streamId);
        await using var lease = await transactions.BeginWrite(Provider, ct);
        if (await Provider.DeleteSubjectStream(lease.Connection, lease.WriteTransaction, tenantId, subjectId, streamId, ct) == 0)
        {
            await lease.Complete(ct);
            return;
        }

        var row = await Provider.ReadStream(lease.Connection, lease.Transaction, tenantId, streamId, forUpdate: true, withState: false, ct);
        var stream = row is null ? null : runtime.Registry.FindStream(row.StreamType);
        if (row is null || row.DeletedAt is not null || stream is null)
        {
            await lease.Complete(ct);
            return;
        }

        var unsnapshotted = row with { State = null };
        var state = await Replay(lease.Connection, lease.Transaction, tenantId, stream, streamId, unsnapshotted, ct);
        var written = await Write(lease, tenantId, stream, streamId, ExpectedVersion.Exact(row.Version), unsnapshotted, state, [new SubjectErased(subjectId)], ct);
        await Commit(lease, written!, ct);
    }

    /// <summary>Replaces a stream's stored state with one rebuilt from all of its events.</summary>
    internal async Task RebuildSnapshot(string streamId, CancellationToken ct)
    {
        var tenantId = TenantId;
        await using var lease = await transactions.BeginWrite(Provider, ct);
        var row = await Provider.ReadStream(lease.Connection, lease.Transaction, tenantId, streamId, forUpdate: true, withState: false, ct);
        var stream = row is null ? null : runtime.Registry.FindStream(row.StreamType);
        if (row is not null && row.DeletedAt is null && stream is { Snapshots.Enabled: true })
        {
            var state = await Replay(lease.Connection, lease.Transaction, tenantId, stream, streamId, row with { State = null }, ct);
            var snapshot = new Snapshot(await SealState(tenantId, streamId, stream, state, ct), stream.StateVersion, row.Version);
            await Provider.SaveSnapshot(lease.Connection, lease.Transaction, tenantId, streamId, snapshot, ct);
        }

        await lease.Complete(ct);
    }

    private static Activity? StartActivity(string name, string? streamType, string streamId)
    {
        var activity = DeedboxDiagnostics.Source.StartActivity(name);
        activity?.SetTag("deedbox.stream_type", streamType);
        activity?.SetTag("deedbox.stream_id", streamId);
        return activity;
    }

    private static void Appended(StreamRegistration stream, Written written, long started)
    {
        var tag = DeedboxDiagnostics.Tag("deedbox.stream_type", stream.Name);
        DeedboxDiagnostics.EventsAppended.Add(written.Envelopes.Length, tag);
        DeedboxDiagnostics.AppendDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tag);
    }

    private StreamRegistration RegisteredStream(string streamType) =>
        runtime.Registry.FindStream(streamType) ?? throw new DeedboxException(Errors.UnregisteredState,
            $"Stream type '{streamType}' is not registered, so Deedbox cannot run its projections. Register it before deleting its streams.");

    /// <summary>
    /// Writes one append after the stream row was read with a lock. Returns null when a new stream lost a
    /// creation race and <paramref name="expected"/> is Any, so the caller reads again.
    /// </summary>
    private async Task<Written?> Write(
        Lease lease, string tenantId, StreamRegistration stream, string streamId, ExpectedVersion expected, StreamRow? row,
        object? known, List<object> events, CancellationToken ct, Func<Task>? beforeCounter = null, bool skipSnapshot = false)
    {
        var connection = lease.Connection;
        var transaction = lease.WriteTransaction;

        if (row is not null)
        {
            CheckStreamType(row, stream, streamId);
            CheckNotDeleted(row, streamId);
        }

        var current = row?.Version ?? 0;
        if (!expected.Matches(current))
            throw new ConcurrencyException(streamId, expected, current);

        var newVersion = current + events.Count;
        var snapshotUsable = row is not null && SnapshotUsable(row, stream);
        var snapshotDue = !skipSnapshot && (stream.Snapshots.IsDue(current, newVersion) || (stream.Snapshots.Enabled && !snapshotUsable));

        object? newState = null;
        Snapshot? snapshot = null;
        if (snapshotDue || known is not null)
        {
            var state = known ?? (row is null ? stream.Initial() : await Replay(connection, transaction, tenantId, stream, streamId, row, ct));
            newState = stream.Fold(state, events);
            if (snapshotDue)
                snapshot = new Snapshot(await SealState(tenantId, streamId, stream, newState, ct), stream.StateVersion, newVersion);
        }

        if (row is null)
        {
            if (!await Provider.InsertStream(connection, transaction, tenantId, streamId, stream.Name, newVersion, snapshot, ct))
            {
                if (expected.IsAny && known is null)
                    return null;
                throw new ConcurrencyException(streamId, expected, await CurrentVersion(lease, tenantId, streamId, ct));
            }
        }
        else if (!await Provider.UpdateStream(connection, transaction, tenantId, streamId, current, newVersion, snapshot, ct))
        {
            throw new ConcurrencyException(streamId, expected, await CurrentVersion(lease, tenantId, streamId, ct));
        }

        var occurredAt = runtime.Clock.GetUtcNow();
        var eventMetadata = CurrentMetadata();
        var metadataJson = eventMetadata.ToJson();
        var rows = new List<NewEvent>(events.Count);
        var registrations = new List<EventRegistration>(events.Count);
        SubjectKeys? keys = null;
        var subjects = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var registration = runtime.Registry.ForEvent(events[i].GetType());
            registrations.Add(registration);
            string payload;
            if (registration.PersonalFields.Count > 0)
            {
                keys ??= new SubjectKeys(runtime.RequireKeys(), Provider, connection, transaction, tenantId);
                (payload, var eventSubjects) = await FieldCipher.Protect(events[i], registration, keys, ct);
                subjects.UnionWith(eventSubjects);
            }
            else
            {
                payload = DeedboxJson.Serialize(events[i], registration.Json);
            }

            rows.Add(new NewEvent(Uuid7.New(), current + i + 1, registration.Name, registration.Version, payload, metadataJson));
        }

        if (subjects.Count > 0)
            await Provider.RecordSubjectStreams(connection, transaction, tenantId, streamId, [.. subjects], ct);

        await using (var work = new TransactionWork(connection, transaction, services, lease.Participants, ct))
        {
            await RunInlineProjections(work, tenantId, stream, streamId, events, rows, eventMetadata, occurredAt);
            await RunHooks(work, tenantId, stream, streamId, events, rows, eventMetadata, occurredAt, ct);
            await lease.BeforeCounter(ct);
            await work.RunBeforeCounter();
        }

        if (beforeCounter is not null)
            await beforeCounter();

        var newTypes = registrations
            .Select(r => new EventTypeRow(stream.Name, r.Name, r.Version))
            .Distinct()
            .Where(t => !runtime.KnownEventTypes.ContainsKey(t))
            .ToList();
        if (newTypes.Count > 0)
            await Provider.RecordEventTypes(connection, transaction, newTypes, ct);

        var counterStarted = Stopwatch.GetTimestamp();
        var last = await Provider.InsertEvents(connection, transaction, tenantId, streamId, stream.Name, occurredAt, rows, ct);

        var envelopes = new EventEnvelope[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            envelopes[i] = new EventEnvelope(rows[i].EventId, tenantId, streamId, stream.Name, rows[i].Version,
                last - rows.Count + 1 + i, rows[i].EventType, rows[i].EventVersion, events[i], eventMetadata, occurredAt);
        }

        return new Written(newVersion, newState, envelopes, newTypes, counterStarted);
    }

    private async Task Commit(Lease lease, Written written, CancellationToken ct)
    {
        await lease.Complete(ct);
        DeedboxDiagnostics.CounterDuration.Record(Stopwatch.GetElapsedTime(written.CounterStarted).TotalMilliseconds);

        // Only a commit Deedbox made proves the rows exist; in a caller's transaction they are recorded again next time.
        if (lease.Commits)
        {
            foreach (var type in written.NewTypes)
                runtime.KnownEventTypes.TryAdd(type, true);
        }
    }

    private async Task RunInlineProjections(
        TransactionWork work, string tenantId, StreamRegistration stream, string streamId,
        List<object> events, List<NewEvent> rows, EventMetadata eventMetadata, DateTimeOffset occurredAt)
    {
        var projections = services.GetRequiredService<ProjectionSet>().Inline
            .Where(p => events.Any(e => p.Instance.Handles(e.GetType())))
            .ToList();
        if (projections.Count == 0)
            return;

        // A projection that is rebuilding or stalled catches up in the runner instead. The shared gate lock keeps a
        // rebuild from starting or finishing while this append is open.
        var statuses = await Provider.ReadInlineStatuses(work.Connection, work.Transaction, projections.Select(p => p.Name).ToList(), work.CancellationToken);
        projections.RemoveAll(p => statuses.GetValueOrDefault(p.Name, CheckpointStatus.Running) != CheckpointStatus.Running);

        foreach (var projection in projections)
        {
            // A projection that handles none of the appended types is skipped.
            for (var i = 0; i < events.Count; i++)
            {
                if (!projection.Instance.Handles(events[i].GetType()))
                    continue;
                await projection.Instance.Handle(events[i], new ProjectionInvocation(work)
                {
                    EventId = rows[i].EventId,
                    TenantId = tenantId,
                    StreamId = streamId,
                    StreamType = stream.Name,
                    Version = rows[i].Version,
                    Metadata = eventMetadata,
                    OccurredAt = occurredAt,
                });
            }
        }
    }

    private async Task RunHooks(
        TransactionWork work, string tenantId, StreamRegistration stream, string streamId,
        List<object> events, List<NewEvent> rows, EventMetadata eventMetadata, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var hooks = services.GetServices<IAppendingHook>().ToList();
        if (hooks.Count == 0)
            return;

        var pending = rows.Select((r, i) => new PendingEvent(r.EventId, r.Version, r.EventType, r.EventVersion, events[i], eventMetadata, occurredAt)).ToList();
        var appending = new AppendingContext(tenantId, streamId, stream.Name, pending, work);
        foreach (var hook in hooks)
            await hook.OnAppending(appending, ct);
    }

    /// <summary>The scope's metadata, this store's override, and the current trace context.</summary>
    private EventMetadata CurrentMetadata()
    {
        var current = context.Metadata;
        if (metadata is not null)
            current = metadata(current);
        if (current.TraceParent is null && Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
            current = current with { TraceParent = activity.Id };
        return current;
    }

    private async Task<Loaded> LoadCore(DbConnection connection, DbTransaction? transaction, string tenantId, StreamRegistration stream, string streamId, bool forUpdate, CancellationToken ct)
    {
        var row = await Provider.ReadStream(connection, transaction, tenantId, streamId, forUpdate, withState: stream.Snapshots.Enabled, ct);
        if (row is null)
            return new Loaded(stream.Initial(), 0, null);

        CheckStreamType(row, stream, streamId);
        CheckNotDeleted(row, streamId);
        var state = await Replay(connection, transaction, tenantId, stream, streamId, row, ct);

        // A missing or outdated snapshot is rebuilt lazily. A write rebuilds it anyway, so only a plain load saves it here.
        if (!forUpdate && stream.Snapshots.Enabled && !SnapshotUsable(row, stream))
        {
            var snapshot = new Snapshot(await SealState(tenantId, streamId, stream, state, ct), stream.StateVersion, row.Version);
            await Provider.SaveSnapshot(connection, transaction, tenantId, streamId, snapshot, ct);
        }

        return new Loaded(state, row.Version, row);
    }

    /// <summary>The state at the row's version: the snapshot plus the events after it, or every event.</summary>
    private async Task<object> Replay(DbConnection connection, DbTransaction? transaction, string tenantId, StreamRegistration stream, string streamId, StreamRow row, CancellationToken ct)
    {
        object state = stream.Initial();
        long after = 0;
        if (SnapshotUsable(row, stream) && await OpenState(tenantId, streamId, row.State!, ct) is { } json)
        {
            state = DeedboxJson.Deserialize(json, stream.StateJson);
            after = row.StateAt;
        }

        if (after >= row.Version)
            return state;

        // Buffered first: the reader must close before personal-data keys are read on the same connection.
        var stored = new List<StoredEvent>();
        await foreach (var e in Provider.ReadStreamEvents(connection, transaction, tenantId, streamId, after, row.Version, ct))
            stored.Add(e);

        foreach (var decoded in await EventDecoding.Decode(runtime, connection, transaction, stored, ct))
            state = stream.Evolve(state, decoded.Event);

        return state;
    }

    private async Task<long> CurrentVersion(Lease lease, string tenantId, string streamId, CancellationToken ct)
    {
        var row = await Provider.ReadStream(lease.Connection, lease.Transaction, tenantId, streamId, forUpdate: false, withState: false, ct);
        return row?.Version ?? 0;
    }

    /// <summary>The state as stored JSON: sealed with the tenant key when the stream type carries personal data.</summary>
    private async Task<string> SealState(string tenantId, string streamId, StreamRegistration stream, object state, CancellationToken ct)
    {
        var json = DeedboxJson.Serialize(state, stream.StateJson);
        if (!stream.HasPersonalData)
            return json;

        var (version, key) = await runtime.RequireKeys().Current(tenantId, ct);
        return new System.Text.Json.Nodes.JsonObject { [Crypto.StateMarker] = Crypto.SealState(key, version, tenantId, streamId, json) }.ToJsonString();
    }

    /// <summary>The stored state's JSON, or null when it is sealed with a tenant key that was deleted; the state is then rebuilt from events.</summary>
    private async Task<string?> OpenState(string tenantId, string streamId, string stored, CancellationToken ct)
    {
        if (!stored.Contains("\"$state\"", StringComparison.Ordinal) || System.Text.Json.Nodes.JsonNode.Parse(stored)?[Crypto.StateMarker]?.GetValue<string>() is not { } marker)
            return stored;

        var key = await runtime.RequireKeys().Find(tenantId, Crypto.StateKeyVersion(marker), ct);
        if (key is null)
            return null;
        try
        {
            return Crypto.OpenState(key, tenantId, streamId, marker);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new DeedboxException(Errors.KeyMaterialCorrupt, $"The stored state of stream '{streamId}' does not verify. It was altered or copied from another stream.", ex);
        }
    }

    private static void CheckNotDeleted(StreamRow row, string streamId)
    {
        if (row.DeletedAt is not null)
        {
            throw new DeedboxException(Errors.StreamDeleted,
                $"Stream '{streamId}' was deleted at {row.DeletedAt:O}. Its ID is not reused; use another stream ID.");
        }
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
            if (registration.Stream is null)
            {
                throw new DeedboxException(Errors.BuiltInEvent,
                    $"{e.GetType().Name} is a built-in event that Deedbox appends itself. Use DeleteStream or ISubjectErasure instead.");
            }

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

    private sealed record Written(long Version, object? State, EventEnvelope[] Envelopes, List<EventTypeRow> NewTypes, long CounterStarted);
}
