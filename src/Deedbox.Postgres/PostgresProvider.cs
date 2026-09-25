using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Deedbox.Postgres;

internal sealed partial class PostgresProvider : DeedboxProvider
{
    internal static readonly IReadOnlyList<Migration> AllMigrations =
        Migration.LoadEmbedded(typeof(PostgresProvider).Assembly, "Deedbox.Postgres.Schema.");

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly int _streamLockSpace;
    private readonly int _inlineLockSpace;

    public PostgresProvider(NpgsqlDataSource dataSource, bool ownsDataSource, string schema)
        : base(schema)
    {
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        Sql = new Statements(Schema);
        _streamLockSpace = (int)LockKey("deedbox:streams:" + Schema);
        _inlineLockSpace = (int)LockKey("deedbox:inline:" + Schema);
    }


    public override string Name => "postgres";

    public override IReadOnlyList<Migration> Migrations => AllMigrations;

    private Statements Sql { get; }

    public override DbConnection CreateConnection() => _dataSource.CreateConnection();

    public override IEnumerable<string> Batches(string script) => [script];

    public override async Task AcquireSchemaLock(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(@key)");
        Add(command, "key", LockKey("deedbox:schema:" + Schema));
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<int> ReadSchemaVersion(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using (var exists = Command(connection, transaction, "SELECT to_regclass(@table) IS NOT NULL"))
        {
            Add(exists, "table", Schema + ".schema_version");
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
                return 0;
        }

        await using var command = Command(connection, transaction, Sql.SchemaVersion);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async Task<StreamRow?> ReadStream(
        DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, bool forUpdate, bool withState, CancellationToken ct)
    {
        var read = withState ? Sql.ReadStreamWithState : Sql.ReadStream;

        // A write first takes a transaction advisory lock on the stream's identity, so writers of one stream
        // queue up even while the row does not exist yet, as SQL Server's key-range lock does.
        var sql = forUpdate ? $"SELECT pg_advisory_xact_lock(@lock_space, @lock_key); {read} FOR UPDATE" : read;
        await using var command = Command(connection, transaction, sql);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        if (forUpdate)
        {
            Add(command, "lock_space", _streamLockSpace);
            Add(command, "lock_key", StreamLockKey(tenantId, streamId));
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (forUpdate)
            await reader.NextResultAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new StreamRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    public override async Task<bool> InsertStream(
        DbConnection connection, DbTransaction transaction, string tenantId, string streamId, string streamType, long version, Snapshot? snapshot, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertStream);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "stream_type", streamType);
        Add(command, "version", version);
        AddSnapshot(command, snapshot);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public override async Task<bool> UpdateStream(
        DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long expectedVersion, long newVersion, Snapshot? snapshot, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, snapshot is null ? Sql.UpdateStream : Sql.UpdateStreamWithSnapshot);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "expected", expectedVersion);
        Add(command, "version", newVersion);
        if (snapshot is not null)
            AddSnapshot(command, snapshot);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public override async Task<long> InsertEvents(
        DbConnection connection, DbTransaction transaction, string tenantId, string streamId, string streamType,
        DateTimeOffset occurredAt, IReadOnlyList<NewEvent> events, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertEvents);
        Add(command, "n", (long)events.Count);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "stream_type", streamType);
        Add(command, "occurred_at", occurredAt);
        Add(command, "ids", events.Select(e => e.EventId).ToArray());
        Add(command, "versions", events.Select(e => e.Version).ToArray());
        Add(command, "types", events.Select(e => e.EventType).ToArray());
        Add(command, "type_versions", events.Select(e => e.EventVersion).ToArray());
        command.Parameters.Add(new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = events.Select(e => e.Payload).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = events.Select(e => e.Metadata).ToArray() });
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async IAsyncEnumerable<StoredEvent> ReadStreamEvents(
        DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, long afterVersion, long toVersion,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadStreamEvents);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "after", afterVersion);
        Add(command, "to", toVersion);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new StoredEvent(
                reader.GetInt64(0), reader.GetGuid(1), tenantId, streamId, reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
        }
    }

    public override async Task SaveSnapshot(
        DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, Snapshot snapshot, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.SaveSnapshot);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        AddSnapshot(command, snapshot);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task RecordEventTypes(DbConnection connection, DbTransaction transaction, IReadOnlyList<EventTypeRow> types, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.RecordEventTypes);
        Add(command, "stream_types", types.Select(t => t.StreamType).ToArray());
        Add(command, "event_types", types.Select(t => t.EventType).ToArray());
        Add(command, "event_versions", types.Select(t => t.EventVersion).ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<EventTypeRow>> ReadEventTypes(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadEventTypes);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<EventTypeRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new EventTypeRow(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return rows;
    }

    public override async Task EnsureCheckpoints(DbConnection connection, IReadOnlyList<CheckpointSeed> checkpoints, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.EnsureCheckpoints);
        Add(command, "names", checkpoints.Select(c => c.Name).ToArray());
        Add(command, "modes", checkpoints.Select(c => c.Mode).ToArray());
        Add(command, "rebuild", checkpoints.Select(c => c.Rebuild).ToArray());
        command.Parameters.Add(new NpgsqlParameter("handles", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { Value = checkpoints.Select(c => c.Handles).ToArray() });
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task Beat(DbConnection connection, InstanceRow instance, TimeSpan liveFor, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.Beat);
        Add(command, "id", instance.Id);
        Add(command, "host", instance.Host);
        Add(command, "app", instance.App);
        command.Parameters.Add(new NpgsqlParameter("consumers", NpgsqlDbType.Jsonb) { Value = Instances.ListJson(instance.Consumers) });
        command.Parameters.Add(new NpgsqlParameter("inline", NpgsqlDbType.Jsonb) { Value = Instances.ListJson(instance.Inline) });
        command.Parameters.Add(new NpgsqlParameter("events", NpgsqlDbType.Jsonb) { Value = Instances.ListJson(instance.Events) });
        Add(command, "stale", liveFor * 10);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task Leave(DbConnection connection, Guid instanceId, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.Leave);
        Add(command, "id", instanceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<InstanceRow>> ReadLiveInstances(DbConnection connection, DbTransaction? transaction, TimeSpan liveFor, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadLiveInstances);
        Add(command, "live", liveFor);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<InstanceRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new InstanceRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Instances.ReadList(reader.GetString(3)),
                Instances.ReadList(reader.GetString(4)), Instances.ReadList(reader.GetString(5)), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    public override async Task<List<CheckpointRow>> ReadCheckpoints(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadCheckpoints);
        return await ReadCheckpointRows(command, ct);
    }

    public override async Task<CheckpointRow?> LockCheckpoint(DbConnection connection, DbTransaction transaction, string name, CheckpointLock mode, CancellationToken ct)
    {
        var sql = Sql.ReadCheckpoint + (mode == CheckpointLock.Batch ? " FOR UPDATE SKIP LOCKED" : " FOR UPDATE");
        await using var command = Command(connection, transaction, sql);
        Add(command, "name", name);
        return (await ReadCheckpointRows(command, ct)).FirstOrDefault();
    }

    public override async Task UpdateCheckpoint(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.UpdateCheckpoint);
        Add(command, "name", row.Name);
        Add(command, "position", row.Position);
        Add(command, "mode", row.Mode);
        Add(command, "status", row.Status);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Jsonb) { Value = (object?)row.Error ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<Dictionary<string, string>> ReadInlineStatuses(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> names, CancellationToken ct)
    {
        // Two statements: the read gets its own snapshot after the locks are held.
        await using var command = Command(connection, transaction,
            $"SELECT pg_advisory_xact_lock_shared(@space, k) FROM unnest(@keys) AS k ORDER BY k; {Sql.ReadStatuses}");
        Add(command, "space", _inlineLockSpace);
        Add(command, "keys", names.Select(n => GateKey(n)).Distinct().ToArray());
        Add(command, "names", names.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.NextResultAsync(ct);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
            statuses[reader.GetString(0)] = reader.GetString(1);
        return statuses;
    }

    public override async Task LockInlineGate(DbConnection connection, DbTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(@space, @key)");
        Add(command, "space", _inlineLockSpace);
        Add(command, "key", GateKey(name));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static int GateKey(string name) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0);

    public override async Task<List<StoredEvent>> ReadEventsAfter(
        DbConnection connection, DbTransaction? transaction, long after, int limit, IReadOnlyList<string>? payloadTypes, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, payloadTypes is null ? Sql.ReadEventsAfter : Sql.ReadEventsAfterFiltered);
        Add(command, "after", after);
        Add(command, "limit", limit);
        if (payloadTypes is not null)
            Add(command, "types", payloadTypes.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<StoredEvent>();
        while (await reader.ReadAsync(ct))
        {
            events.Add(new StoredEvent(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return events;
    }

    public override async Task<long> ReadHead(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadHead);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async Task<long> LockCounter(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadHead + " FOR UPDATE");
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async Task InsertJob(DbConnection connection, DbTransaction? transaction, JobRow job, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertJob);
        AddJob(command, job);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<JobRow?> ClaimJob(DbConnection connection, DbTransaction transaction, IReadOnlyCollection<Guid> except, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ClaimJob);
        Add(command, "except", except.ToArray());
        return (await ReadJobRows(command, ct)).FirstOrDefault();
    }

    public override async Task UpdateJob(DbConnection connection, DbTransaction transaction, JobRow job, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.UpdateJob);
        AddJob(command, job);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<JobRow?> ReadJob(DbConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadJob);
        Add(command, "id", id);
        return (await ReadJobRows(command, ct)).FirstOrDefault();
    }

    public override async Task Listen(Action wake, CancellationToken ct)
    {
        await using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync(ct);
        connection.Notification += (_, _) => wake();
        await using (var command = new NpgsqlCommand($"LISTEN dbx_{Schema}", connection))
            await command.ExecuteNonQueryAsync(ct);

        // Events may have committed before LISTEN took effect.
        wake();
        while (true)
            await connection.WaitAsync(ct);
    }

    public override async Task<List<MasterKeyRow>> ReadMasterKeys(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadMasterKeys);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<MasterKeyRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new MasterKeyRow(reader.GetString(0), reader.GetInt32(1), (byte[])reader.GetValue(2), reader.GetString(3)));
        return rows;
    }

    public override async Task<bool> InsertMasterKey(DbConnection connection, DbTransaction? transaction, MasterKeyRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertMasterKey);
        AddMasterKey(command, row);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public override async Task UpdateMasterKey(DbConnection connection, DbTransaction transaction, MasterKeyRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.UpdateMasterKey);
        AddMasterKey(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task DeleteMasterKey(DbConnection connection, DbTransaction transaction, string tenantId, int keyVersion, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, $"DELETE FROM {Schema}.master_keys WHERE tenant_id = @tenant AND key_version = @version");
        Add(command, "tenant", tenantId);
        Add(command, "version", keyVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<SubjectKeyRow?> ReadSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectKey);
        Add(command, "tenant", tenantId);
        Add(command, "subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new SubjectKeyRow(tenantId, subjectId, reader.GetString(0), (byte[])reader.GetValue(1)) : null;
    }

    public override async Task InsertSubjectKey(DbConnection connection, DbTransaction transaction, SubjectKeyRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertSubjectKey);
        Add(command, "tenant", row.TenantId);
        Add(command, "subject", row.SubjectId);
        Add(command, "key_id", row.KeyId);
        Add(command, "wrapped", row.WrappedKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<Dictionary<string, byte[]>> ReadSubjectKeysById(DbConnection connection, DbTransaction? transaction, string tenantId, IReadOnlyList<string> keyIds, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectKeysById);
        Add(command, "tenant", tenantId);
        Add(command, "ids", keyIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
            keys[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return keys;
    }

    public override async Task<int> DeleteSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteSubjectKey);
        Add(command, "tenant", tenantId);
        Add(command, "subject", subjectId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task RecordSubjectStreams(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, IReadOnlyList<string> subjectIds, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.RecordSubjectStreams);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "subjects", subjectIds.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<string>> ReadSubjectStreams(DbConnection connection, DbTransaction? transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectStreams);
        Add(command, "tenant", tenantId);
        Add(command, "subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var streams = new List<string>();
        while (await reader.ReadAsync(ct))
            streams.Add(reader.GetString(0));
        return streams;
    }

    public override async Task<int> DeleteSubjectStream(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, string streamId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteSubjectStream);
        Add(command, "tenant", tenantId);
        Add(command, "subject", subjectId);
        Add(command, "stream", streamId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task DeleteStreamData(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long keepFromVersion, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteStreamData);
        Add(command, "tenant", tenantId);
        Add(command, "stream", streamId);
        Add(command, "keep", keepFromVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<JobRow>> ReadJobs(DbConnection connection, int limit, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadJobs);
        Add(command, "limit", limit);
        return await ReadJobRows(command, ct);
    }

    public override async Task<List<(string TenantId, string StreamId)>> ReadStreamKeys(
        DbConnection connection, string streamType, string afterTenant, string afterStream, int limit, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadStreamKeys);
        Add(command, "stream_type", streamType);
        Add(command, "after_tenant", afterTenant);
        Add(command, "after_stream", afterStream);
        Add(command, "limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var keys = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
            keys.Add((reader.GetString(0), reader.GetString(1)));
        return keys;
    }

    public override async Task ShredTenant(DbConnection connection, DbTransaction transaction, string tenantId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ShredTenant);
        Add(command, "tenant", tenantId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddMasterKey(DbCommand command, MasterKeyRow row)
    {
        Add(command, "tenant", row.TenantId);
        Add(command, "version", row.KeyVersion);
        Add(command, "wrapped", row.WrappedKey);
        Add(command, "wrapped_by", row.WrappedBy);
    }

    private static void AddJob(DbCommand command, JobRow job)
    {
        Add(command, "id", job.Id);
        Add(command, "kind", job.Kind);
        command.Parameters.Add(new NpgsqlParameter("args", NpgsqlDbType.Jsonb) { Value = job.Args });
        Add(command, "status", job.Status);
        command.Parameters.Add(new NpgsqlParameter("progress", NpgsqlDbType.Jsonb) { Value = (object?)job.Progress ?? DBNull.Value });
        Add(command, "started_at", (object?)job.StartedAt ?? DBNull.Value);
        Add(command, "finished_at", (object?)job.FinishedAt ?? DBNull.Value);
    }

    private static async Task<List<CheckpointRow>> ReadCheckpointRows(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CheckpointRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CheckpointRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<List<JobRow>> ReadJobRows(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<JobRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JobRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return rows;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _dataSource.DisposeAsync();
    }

    private static void AddSnapshot(DbCommand command, Snapshot? snapshot)
    {
        command.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Jsonb) { Value = (object?)snapshot?.State ?? DBNull.Value });
        Add(command, "state_version", snapshot?.StateVersion ?? 0);
        Add(command, "state_at", snapshot?.At ?? 0L);
    }

    /// <summary>A stable 64-bit advisory lock key for a name.</summary>
    private static long LockKey(string name) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0);

    /// <summary>A stable 32-bit key for a stream. A collision only makes two streams' writers queue together.</summary>
    private static int StreamLockKey(string tenantId, string streamId) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId + "\0" + streamId)), 0);

    private sealed class Statements(string s)
    {
        public readonly string SchemaVersion = $"SELECT coalesce(max(version), 0) FROM {s}.schema_version";

        public readonly string ReadStream =
            $"SELECT stream_type, version, NULL::text, state_version, state_at, deleted_at FROM {s}.streams WHERE tenant_id = @tenant AND stream_id = @stream";

        public readonly string ReadStreamWithState =
            $"SELECT stream_type, version, state::text, state_version, state_at, deleted_at FROM {s}.streams WHERE tenant_id = @tenant AND stream_id = @stream";

        public readonly string InsertStream = $"""
            INSERT INTO {s}.streams (tenant_id, stream_id, stream_type, version, state, state_version, state_at)
            VALUES (@tenant, @stream, @stream_type, @version, @state, @state_version, @state_at)
            ON CONFLICT (tenant_id, stream_id) DO NOTHING
            """;

        public readonly string UpdateStream = $"""
            UPDATE {s}.streams SET version = @version, updated_at = now()
            WHERE tenant_id = @tenant AND stream_id = @stream AND version = @expected
            """;

        public readonly string UpdateStreamWithSnapshot = $"""
            UPDATE {s}.streams SET version = @version, state = @state, state_version = @state_version, state_at = @state_at, updated_at = now()
            WHERE tenant_id = @tenant AND stream_id = @stream AND version = @expected
            """;

        // The counter update and the insert are one statement, so the counter lock is held for as short a time as possible.
        public readonly string InsertEvents = $"""
            WITH counter AS (
                UPDATE {s}.position SET value = value + @n RETURNING value
            ), inserted AS (
                INSERT INTO {s}.events (global_position, event_id, tenant_id, stream_id, version, stream_type, event_type, event_version, payload, metadata, occurred_at)
                SELECT counter.value - @n + e.ord, e.event_id, @tenant, @stream, e.version, @stream_type, e.event_type, e.event_version, e.payload, e.metadata, @occurred_at
                FROM counter, unnest(@ids, @versions, @types, @type_versions, @payloads, @metadata)
                    WITH ORDINALITY AS e(event_id, version, event_type, event_version, payload, metadata, ord)
            )
            SELECT value FROM counter
            """;

        public readonly string ReadStreamEvents = $"""
            SELECT global_position, event_id, version, stream_type, event_type, event_version, payload::text, metadata::text, occurred_at
            FROM {s}.events
            WHERE tenant_id = @tenant AND stream_id = @stream AND version > @after AND version <= @to
            ORDER BY version
            """;

        public readonly string RecordEventTypes = $"""
            INSERT INTO {s}.event_types (stream_type, event_type, event_version)
            SELECT * FROM unnest(@stream_types, @event_types, @event_versions)
            ON CONFLICT DO NOTHING
            """;

        public readonly string ReadEventTypes =
            $"SELECT stream_type, event_type, event_version FROM {s}.event_types ORDER BY stream_type, event_type, event_version";

        private const string CheckpointColumns = "name, position, mode, status, error::text, updated_at, handles::text";

        private const string JobColumns = "id, kind, args::text, status, progress::text, created_at, started_at, finished_at";

        private const string EventColumns = "global_position, event_id, tenant_id, stream_id, version, stream_type, event_type, event_version";

        public readonly string EnsureCheckpoints = $"""
            INSERT INTO {s}.checkpoints (name, mode, status, handles)
            SELECT n, m, CASE WHEN m = 'inline' AND (r OR (SELECT value FROM {s}.position) > 0) THEN 'rebuilding' ELSE 'running' END, h
            FROM unnest(@names, @modes, @rebuild, @handles) AS c(n, m, r, h)
            ON CONFLICT (name) DO UPDATE SET handles = EXCLUDED.handles
            """;

        public readonly string Beat = $"""
            INSERT INTO {s}.instances (instance_id, host, app, consumers, inline_projections, event_types)
            VALUES (@id, @host, @app, @consumers, @inline, @events)
            ON CONFLICT (instance_id) DO UPDATE SET
                consumers = EXCLUDED.consumers, inline_projections = EXCLUDED.inline_projections,
                event_types = EXCLUDED.event_types, seen_at = clock_timestamp();
            DELETE FROM {s}.instances WHERE seen_at < clock_timestamp() - @stale;
            """;

        public readonly string Leave = $"DELETE FROM {s}.instances WHERE instance_id = @id";

        public readonly string ReadLiveInstances = $"""
            SELECT instance_id, host, app, consumers::text, inline_projections::text, event_types::text, seen_at
            FROM {s}.instances WHERE seen_at > clock_timestamp() - @live ORDER BY started_at
            """;

        public readonly string ReadCheckpoints = $"SELECT {CheckpointColumns} FROM {s}.checkpoints ORDER BY name";

        public readonly string ReadCheckpoint = $"SELECT {CheckpointColumns} FROM {s}.checkpoints WHERE name = @name";

        public readonly string UpdateCheckpoint = $"""
            UPDATE {s}.checkpoints SET position = @position, mode = @mode, status = @status, error = @error, updated_at = now()
            WHERE name = @name
            """;

        public readonly string ReadStatuses = $"SELECT name, status FROM {s}.checkpoints WHERE name = ANY(@names)";

        public readonly string ReadEventsAfter = $"""
            SELECT {EventColumns}, payload::text, metadata::text, occurred_at
            FROM {s}.events WHERE global_position > @after AND global_position <= (SELECT value FROM {s}.position)
            ORDER BY global_position LIMIT @limit
            """;

        public readonly string ReadEventsAfterFiltered = $"""
            SELECT {EventColumns},
                CASE WHEN event_type = ANY(@types) THEN payload::text END,
                CASE WHEN event_type = ANY(@types) THEN metadata::text END,
                occurred_at
            FROM {s}.events WHERE global_position > @after AND global_position <= (SELECT value FROM {s}.position)
            ORDER BY global_position LIMIT @limit
            """;

        public readonly string ReadHead = $"SELECT value FROM {s}.position";

        public readonly string InsertJob = $"""
            INSERT INTO {s}.jobs (id, kind, args, status, progress, started_at, finished_at)
            VALUES (@id, @kind, @args, @status, @progress, @started_at, @finished_at)
            """;

        public readonly string ClaimJob = $"""
            SELECT {JobColumns} FROM {s}.jobs WHERE status = 'queued' AND NOT (id = ANY(@except))
            ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """;

        public readonly string UpdateJob = $"""
            UPDATE {s}.jobs SET status = @status, args = @args, progress = @progress, started_at = @started_at, finished_at = @finished_at,
                updated_at = now(), kind = @kind
            WHERE id = @id
            """;

        public readonly string ReadJob = $"SELECT {JobColumns} FROM {s}.jobs WHERE id = @id";

        public readonly string ReadJobs = $"SELECT {JobColumns} FROM {s}.jobs ORDER BY created_at DESC, id DESC LIMIT @limit";

        public readonly string ReadStreamKeys = $"""
            SELECT tenant_id, stream_id FROM {s}.streams
            WHERE stream_type = @stream_type AND deleted_at IS NULL AND (tenant_id, stream_id) > (@after_tenant, @after_stream)
            ORDER BY tenant_id, stream_id LIMIT @limit
            """;

        public readonly string ShredTenant = $"""
            UPDATE {s}.master_keys SET wrapped_key = '\x'::bytea, wrapped_by = 'shredded' WHERE tenant_id = @tenant AND key_version > 0;
            DELETE FROM {s}.subject_keys WHERE tenant_id = @tenant;
            DELETE FROM {s}.subject_streams WHERE tenant_id = @tenant;
            UPDATE {s}.streams SET state = NULL WHERE tenant_id = @tenant;
            """;

        public readonly string ReadMasterKeys = $"SELECT tenant_id, key_version, wrapped_key, wrapped_by FROM {s}.master_keys ORDER BY tenant_id, key_version";

        public readonly string InsertMasterKey = $"""
            INSERT INTO {s}.master_keys (tenant_id, key_version, wrapped_key, wrapped_by) VALUES (@tenant, @version, @wrapped, @wrapped_by)
            ON CONFLICT (tenant_id, key_version) DO NOTHING
            """;

        public readonly string UpdateMasterKey =
            $"UPDATE {s}.master_keys SET wrapped_key = @wrapped, wrapped_by = @wrapped_by WHERE tenant_id = @tenant AND key_version = @version";

        public readonly string ReadSubjectKey =
            $"SELECT key_id, wrapped_key FROM {s}.subject_keys WHERE tenant_id = @tenant AND subject_id = @subject FOR SHARE";

        public readonly string InsertSubjectKey = $"""
            INSERT INTO {s}.subject_keys (tenant_id, subject_id, key_id, wrapped_key) VALUES (@tenant, @subject, @key_id, @wrapped)
            ON CONFLICT (tenant_id, subject_id) DO NOTHING
            """;

        public readonly string ReadSubjectKeysById =
            $"SELECT key_id, wrapped_key FROM {s}.subject_keys WHERE tenant_id = @tenant AND key_id = ANY(@ids)";

        public readonly string DeleteSubjectKey = $"""
            UPDATE {s}.streams SET state = NULL
            WHERE tenant_id = @tenant AND stream_id IN (SELECT stream_id FROM {s}.subject_streams WHERE tenant_id = @tenant AND subject_id = @subject);
            DELETE FROM {s}.subject_keys WHERE tenant_id = @tenant AND subject_id = @subject;
            """;

        public readonly string RecordSubjectStreams = $"""
            INSERT INTO {s}.subject_streams (tenant_id, subject_id, stream_id)
            SELECT @tenant, subject, @stream FROM unnest(@subjects) AS subject
            ON CONFLICT DO NOTHING
            """;

        public readonly string ReadSubjectStreams =
            $"SELECT stream_id FROM {s}.subject_streams WHERE tenant_id = @tenant AND subject_id = @subject ORDER BY stream_id";

        public readonly string DeleteSubjectStream =
            $"DELETE FROM {s}.subject_streams WHERE tenant_id = @tenant AND subject_id = @subject AND stream_id = @stream";

        public readonly string DeleteStreamData = $"""
            DELETE FROM {s}.events WHERE tenant_id = @tenant AND stream_id = @stream AND version < @keep;
            DELETE FROM {s}.subject_streams WHERE tenant_id = @tenant AND stream_id = @stream;
            UPDATE {s}.streams SET state = NULL, state_version = 0, state_at = 0, deleted_at = now(), updated_at = now()
            WHERE tenant_id = @tenant AND stream_id = @stream;
            """;

        public readonly string SaveSnapshot = $"""
            UPDATE {s}.streams SET state = @state, state_version = @state_version, state_at = @state_at
            WHERE tenant_id = @tenant AND stream_id = @stream AND version = @state_at
            """;
    }
}
