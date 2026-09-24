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

    public PostgresProvider(NpgsqlDataSource dataSource, bool ownsDataSource, string schema)
        : base(schema)
    {
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        Sql = new Statements(Schema);
        _streamLockSpace = (int)LockKey("deedbox:streams:" + Schema);
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

        public readonly string SaveSnapshot = $"""
            UPDATE {s}.streams SET state = @state, state_version = @state_version, state_at = @state_at
            WHERE tenant_id = @tenant AND stream_id = @stream AND version = @state_at
            """;
    }
}
