using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Deedbox.SqlServer;

internal sealed partial class SqlServerProvider : DeedboxProvider
{
    internal static readonly IReadOnlyList<Migration> AllMigrations =
        Migration.LoadEmbedded(typeof(SqlServerProvider).Assembly, "Deedbox.SqlServer.Schema.");

    private readonly string _connectionString;

    public SqlServerProvider(string connectionString, string schema)
        : base(schema)
    {
        _connectionString = connectionString;
        Sql = new Statements(Schema);
    }

    public override string Name => "sqlserver";

    public override IReadOnlyList<Migration> Migrations => AllMigrations;

    private Statements Sql { get; }

    public override DbConnection CreateConnection() => new SqlConnection(_connectionString);

    public override IEnumerable<string> Batches(string script) =>
        GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b));

    public override async Task AcquireSchemaLock(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = -1;
            SELECT @result;
            """);
        Add(command, "resource", "deedbox:schema:" + Schema);
        var result = (int)(await command.ExecuteScalarAsync(ct))!;
        if (result < 0)
            throw new InvalidOperationException($"sp_getapplock returned {result} for the Deedbox schema lock.");
    }

    public override async Task<int> ReadSchemaVersion(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using (var exists = Command(connection, transaction, "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END"))
        {
            Add(exists, "table", $"[{Schema}].[schema_version]");
            if ((int)(await exists.ExecuteScalarAsync(ct))! == 0)
                return 0;
        }

        await using var command = Command(connection, transaction, Sql.SchemaVersion);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async Task<StreamRow?> ReadStream(
        DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, bool forUpdate, bool withState, CancellationToken ct)
    {
        var sql = (forUpdate, withState) switch
        {
            (true, true) => Sql.LockStreamWithState,
            (true, false) => Sql.LockStream,
            (false, true) => Sql.ReadStreamWithState,
            (false, false) => Sql.ReadStream,
        };
        await using var command = Command(connection, transaction, sql);
        AddKey(command, tenantId, streamId);
        await using var reader = await command.ExecuteReaderAsync(ct);
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
        AddKey(command, tenantId, streamId);
        AddText(command, "stream_type", streamType, 200);
        Add(command, "version", version);
        AddSnapshot(command, snapshot);
        try
        {
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public override async Task<bool> UpdateStream(
        DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long expectedVersion, long newVersion, Snapshot? snapshot, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, snapshot is null ? Sql.UpdateStream : Sql.UpdateStreamWithSnapshot);
        AddKey(command, tenantId, streamId);
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
        AddKey(command, tenantId, streamId);
        AddText(command, "stream_type", streamType, 200);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("occurred_at", SqlDbType.DateTimeOffset) { Value = occurredAt });
        ((SqlCommand)command).Parameters.Add(new SqlParameter("events", SqlDbType.NVarChar, -1) { Value = EventsJson(events) });
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async IAsyncEnumerable<StoredEvent> ReadStreamEvents(
        DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, long afterVersion, long toVersion,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadStreamEvents);
        AddKey(command, tenantId, streamId);
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
        AddKey(command, tenantId, streamId);
        AddSnapshot(command, snapshot);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task RecordEventTypes(DbConnection connection, DbTransaction transaction, IReadOnlyList<EventTypeRow> types, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.RecordEventTypes);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("types", SqlDbType.NVarChar, -1)
        {
            Value = JsonSerializer.Serialize(types.Select(t => new[] { t.StreamType, t.EventType, t.EventVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(), SqlServerJson.Default.StringArrayArray),
        });
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

    /// <summary>The events as one JSON array for OPENJSON, so an append of any size is one parameter.</summary>
    private static string EventsJson(IReadOnlyList<NewEvent> events)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                json.WriteStartObject();
                json.WriteNumber("o", i + 1);
                json.WriteString("i", e.EventId);
                json.WriteNumber("v", e.Version);
                json.WriteString("t", e.EventType);
                json.WriteNumber("tv", e.EventVersion);
                json.WriteString("p", e.Payload);
                json.WriteString("m", e.Metadata);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void AddKey(DbCommand command, string tenantId, string streamId)
    {
        AddText(command, "tenant", tenantId, 100);
        AddText(command, "stream", streamId, 200);
    }

    private static void AddText(DbCommand command, string name, string value, int size) =>
        ((SqlCommand)command).Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size) { Value = value });

    private static void AddSnapshot(DbCommand command, Snapshot? snapshot)
    {
        ((SqlCommand)command).Parameters.Add(new SqlParameter("state", SqlDbType.NVarChar, -1) { Value = (object?)snapshot?.State ?? DBNull.Value });
        Add(command, "state_version", snapshot?.StateVersion ?? 0);
        Add(command, "state_at", snapshot?.At ?? 0L);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    private sealed class Statements(string s)
    {
        private const string Key = "tenant_id = @tenant AND stream_id = @stream";

        public readonly string SchemaVersion = $"SELECT ISNULL(MAX(version), 0) FROM [{s}].[schema_version]";

        public readonly string ReadStream =
            $"SELECT stream_type, version, CAST(NULL AS nvarchar(max)), state_version, state_at, deleted_at FROM [{s}].[streams] WHERE {Key}";

        public readonly string ReadStreamWithState =
            $"SELECT stream_type, version, state, state_version, state_at, deleted_at FROM [{s}].[streams] WHERE {Key}";

        // UPDLOCK + HOLDLOCK locks the row, or the key range when the row does not exist, so a concurrent creator waits.
        public readonly string LockStream =
            $"SELECT stream_type, version, CAST(NULL AS nvarchar(max)), state_version, state_at, deleted_at FROM [{s}].[streams] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE {Key}";

        public readonly string LockStreamWithState =
            $"SELECT stream_type, version, state, state_version, state_at, deleted_at FROM [{s}].[streams] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE {Key}";

        public readonly string InsertStream = $"""
            INSERT INTO [{s}].[streams] (tenant_id, stream_id, stream_type, version, state, state_version, state_at)
            VALUES (@tenant, @stream, @stream_type, @version, @state, @state_version, @state_at)
            """;

        public readonly string UpdateStream = $"""
            UPDATE [{s}].[streams] SET version = @version, updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
            WHERE {Key} AND version = @expected
            """;

        public readonly string UpdateStreamWithSnapshot = $"""
            UPDATE [{s}].[streams] SET version = @version, state = @state, state_version = @state_version, state_at = @state_at,
                updated_at = TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
            WHERE {Key} AND version = @expected
            """;

        // The counter update and the insert are one batch, so the counter lock is held for as short a time as possible.
        public readonly string InsertEvents = $"""
            SET NOCOUNT ON;
            DECLARE @end bigint;
            UPDATE [{s}].[position] SET @end = value = value + @n;
            INSERT INTO [{s}].[events] (global_position, event_id, tenant_id, stream_id, version, stream_type, event_type, event_version, payload, metadata, occurred_at)
            SELECT @end - @n + e.o, e.i, @tenant, @stream, e.v, @stream_type, e.t, e.tv, e.p, e.m, @occurred_at
            FROM OPENJSON(@events) WITH (
                o bigint '$.o', i uniqueidentifier '$.i', v bigint '$.v', t nvarchar(200) '$.t',
                tv int '$.tv', p nvarchar(max) '$.p', m nvarchar(max) '$.m') AS e;
            SELECT @end;
            """;

        public readonly string ReadStreamEvents = $"""
            SELECT global_position, event_id, version, stream_type, event_type, event_version, payload, metadata, occurred_at
            FROM [{s}].[events]
            WHERE {Key} AND version > @after AND version <= @to
            ORDER BY version
            """;

        // The primary key ignores duplicates, so concurrent first appends of one event type both succeed.
        public readonly string RecordEventTypes = $"""
            INSERT INTO [{s}].[event_types] (stream_type, event_type, event_version)
            SELECT JSON_VALUE(t.value, '$[0]'), JSON_VALUE(t.value, '$[1]'), CAST(JSON_VALUE(t.value, '$[2]') AS int)
            FROM OPENJSON(@types) AS t
            """;

        public readonly string ReadEventTypes =
            $"SELECT stream_type, event_type, event_version FROM [{s}].[event_types] ORDER BY stream_type, event_type, event_version";

        public readonly string SaveSnapshot = $"""
            UPDATE [{s}].[streams] SET state = @state, state_version = @state_version, state_at = @state_at
            WHERE {Key} AND version = @state_at
            """;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[][]))]
internal sealed partial class SqlServerJson : System.Text.Json.Serialization.JsonSerializerContext;
