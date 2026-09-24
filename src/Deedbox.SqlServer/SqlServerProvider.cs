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

    /// <summary>The columns that hold JSON, as (table, column, nullable).</summary>
    private static readonly (string Table, string Column, bool Nullable)[] JsonColumns =
    [
        ("streams", "state", true),
        ("events", "payload", false),
        ("events", "metadata", false),
        ("checkpoints", "error", true),
        ("jobs", "args", false),
        ("jobs", "progress", true),
    ];

    private readonly string _connectionString;

    public SqlServerProvider(string connectionString, string schema, bool nativeJson = false)
        : base(schema)
    {
        _connectionString = connectionString;
        NativeJson = nativeJson;
        Sql = new Statements(Schema);
    }

    public bool NativeJson { get; }

    public override string? StorageScript => NativeJson ? NativeJsonScript(Schema) : null;

    /// <summary>
    /// Converts every JSON column that is still nvarchar(max) to the json type, in one batch. It stops before any change
    /// when the server has no json type. Converting a column rewrites its rows.
    /// </summary>
    internal static string NativeJsonScript(string schema)
    {
        var sql = new StringBuilder();
        sql.AppendLine("IF TYPE_ID(N'json') IS NULL");
        sql.AppendLine("BEGIN;");
        sql.Append("    THROW 51000, N'").Append(NoJsonType.Replace("'", "''", StringComparison.Ordinal)).AppendLine("', 1;");
        sql.AppendLine("END;");
        foreach (var (table, column, nullable) in JsonColumns)
        {
            sql.Append("IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[").Append(schema).Append("].[").Append(table)
                .Append("]') AND name = N'").Append(column).AppendLine("' AND TYPE_NAME(user_type_id) <> N'json')");
            sql.Append("    EXEC(N'ALTER TABLE [").Append(schema).Append("].[").Append(table).Append("] ALTER COLUMN ").Append(column)
                .Append(" json ").Append(nullable ? "NULL" : "NOT NULL").AppendLine("');");
        }

        sql.AppendLine("GO");
        return sql.ToString();
    }

    private const string NoJsonType =
        "Native json columns need a server with the json type: SQL Server 2025, Azure SQL Database or Azure SQL Managed Instance. " +
        "Use such a server, or turn NativeJson off.";

    public override async Task<string?> StorageProblem(DbConnection connection, DbTransaction? transaction, bool supportOnly, CancellationToken ct)
    {
        if (!NativeJson)
            return null;

        var pending = string.Join(" OR ", JsonColumns.Select(c =>
            $"(object_id = OBJECT_ID(N'[{Schema}].[{c.Table}]') AND name = N'{c.Column}')"));
        await using var command = Command(connection, transaction, $"""
            SELECT CASE WHEN TYPE_ID(N'json') IS NULL THEN -1
                        ELSE (SELECT COUNT(*) FROM sys.columns WHERE ({pending}) AND TYPE_NAME(user_type_id) <> N'json') END
            """);
        var result = (int)(await command.ExecuteScalarAsync(ct))!;
        if (result < 0)
            return NoJsonType;
        if (result == 0 || supportOnly)
            return null;

        return $"NativeJson is on, but {result} JSON column(s) in schema '{Schema}' are still nvarchar(max). " +
            $"Convert them with 'deedbox schema apply --provider sqlserver --schema {Schema} --native-json --connection <connection string>', " +
            $"or print the SQL with 'deedbox schema script --provider sqlserver --schema {Schema} --native-json', or call ApplySchemaOnStartup() in AddDeedbox.";
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

    public override async Task EnsureCheckpoints(DbConnection connection, IReadOnlyList<(string Name, string Mode)> checkpoints, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.EnsureCheckpoints);
        AddJson(command, "checkpoints", checkpoints.Select(c => new[] { c.Name, c.Mode }).ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<CheckpointRow>> ReadCheckpoints(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, null, Sql.ReadCheckpoints);
        return await ReadCheckpointRows(command, ct);
    }

    public override async Task<CheckpointRow?> LockCheckpoint(DbConnection connection, DbTransaction transaction, string name, CheckpointLock mode, CancellationToken ct)
    {
        // A batch lock skips a row another runner holds; an exclusive lock waits for it.
        await using var command = Command(connection, transaction, mode == CheckpointLock.Batch ? Sql.LockCheckpointBatch : Sql.LockCheckpointExclusive);
        AddText(command, "name", name, 200);
        return (await ReadCheckpointRows(command, ct)).FirstOrDefault();
    }

    public override async Task UpdateCheckpoint(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.UpdateCheckpoint);
        AddText(command, "name", row.Name, 200);
        Add(command, "position", row.Position);
        AddText(command, "mode", row.Mode, 20);
        AddText(command, "status", row.Status, 20);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("error", SqlDbType.NVarChar, -1) { Value = (object?)row.Error ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<Dictionary<string, string>> ReadInlineStatuses(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> names, CancellationToken ct)
    {
        var ordered = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var sql = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            sql.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"EXEC sp_getapplock @Resource = @r{i}, @LockMode = 'Shared', @LockOwner = 'Transaction', @LockTimeout = -1;\n");
        }

        sql.Append(Sql.ReadStatuses);
        await using var command = Command(connection, transaction, sql.ToString());
        for (var i = 0; i < ordered.Count; i++)
            AddText(command, "r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), GateResource(ordered[i]), 255);
        AddJson(command, "names", ordered.Select(n => new[] { n }).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
            statuses[reader.GetString(0)] = reader.GetString(1);
        return statuses;
    }

    public override async Task LockInlineGate(DbConnection connection, DbTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = -1;");
        AddText(command, "resource", GateResource(name), 255);
        await command.ExecuteNonQueryAsync(ct);
    }

    private string GateResource(string name) => $"deedbox:{Schema}:inline:{name}";

    public override async Task<List<StoredEvent>> ReadEventsAfter(
        DbConnection connection, DbTransaction? transaction, long after, int limit, IReadOnlyList<string>? payloadTypes, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, payloadTypes is null ? Sql.ReadEventsAfter : Sql.ReadEventsAfterFiltered);
        Add(command, "after", after);
        Add(command, "limit", limit);
        if (payloadTypes is not null)
            AddJson(command, "types", payloadTypes.Select(t => new[] { t }).ToArray());
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
        await using var command = Command(connection, transaction, Sql.LockCounter);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async Task InsertJob(DbConnection connection, DbTransaction? transaction, JobRow job, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertJob);
        AddJob(command, job);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<JobRow?> ClaimJob(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ClaimJob);
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

    public override Task Listen(Action wake, CancellationToken ct) => Task.CompletedTask;

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
        await using var command = Command(connection, transaction, $"DELETE FROM [{Schema}].[master_keys] WHERE tenant_id = @tenant AND key_version = @version");
        AddText(command, "tenant", tenantId, 100);
        Add(command, "version", keyVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<SubjectKeyRow?> ReadSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectKey);
        AddText(command, "tenant", tenantId, 100);
        AddText(command, "subject", subjectId, 100);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new SubjectKeyRow(tenantId, subjectId, reader.GetString(0), (byte[])reader.GetValue(1)) : null;
    }

    public override async Task InsertSubjectKey(DbConnection connection, DbTransaction transaction, SubjectKeyRow row, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.InsertSubjectKey);
        AddText(command, "tenant", row.TenantId, 100);
        AddText(command, "subject", row.SubjectId, 100);
        AddText(command, "key_id", row.KeyId, 100);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("wrapped", SqlDbType.VarBinary, -1) { Value = row.WrappedKey });
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<Dictionary<string, byte[]>> ReadSubjectKeysById(DbConnection connection, DbTransaction? transaction, string tenantId, IReadOnlyList<string> keyIds, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectKeysById);
        AddText(command, "tenant", tenantId, 100);
        AddJson(command, "ids", keyIds.Select(k => new[] { k }).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
            keys[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return keys;
    }

    public override async Task<int> DeleteSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteSubjectKey);
        AddText(command, "tenant", tenantId, 100);
        AddText(command, "subject", subjectId, 100);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task RecordSubjectStreams(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, IReadOnlyList<string> subjectIds, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.RecordSubjectStreams);
        AddKey(command, tenantId, streamId);
        AddJson(command, "subjects", subjectIds.Select(s => new[] { s }).ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<List<string>> ReadSubjectStreams(DbConnection connection, DbTransaction? transaction, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.ReadSubjectStreams);
        AddText(command, "tenant", tenantId, 100);
        AddText(command, "subject", subjectId, 100);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var streams = new List<string>();
        while (await reader.ReadAsync(ct))
            streams.Add(reader.GetString(0));
        return streams;
    }

    public override async Task<int> DeleteSubjectStream(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, string streamId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteSubjectStream);
        AddKey(command, tenantId, streamId);
        AddText(command, "subject", subjectId, 100);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task DeleteStreamData(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long keepFromVersion, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, Sql.DeleteStreamData);
        AddKey(command, tenantId, streamId);
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
        AddText(command, "stream_type", streamType, 200);
        AddText(command, "after_tenant", afterTenant, 100);
        AddText(command, "after_stream", afterStream, 200);
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
        AddText(command, "tenant", tenantId, 100);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddMasterKey(DbCommand command, MasterKeyRow row)
    {
        AddText(command, "tenant", row.TenantId, 100);
        Add(command, "version", row.KeyVersion);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("wrapped", SqlDbType.VarBinary, -1) { Value = row.WrappedKey });
        AddText(command, "wrapped_by", row.WrappedBy, 200);
    }

    private static void AddJob(DbCommand command, JobRow job)
    {
        Add(command, "id", job.Id);
        AddText(command, "kind", job.Kind, 100);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("args", SqlDbType.NVarChar, -1) { Value = job.Args });
        AddText(command, "status", job.Status, 20);
        ((SqlCommand)command).Parameters.Add(new SqlParameter("progress", SqlDbType.NVarChar, -1) { Value = (object?)job.Progress ?? DBNull.Value });
        ((SqlCommand)command).Parameters.Add(new SqlParameter("started_at", SqlDbType.DateTimeOffset) { Value = (object?)job.StartedAt ?? DBNull.Value });
        ((SqlCommand)command).Parameters.Add(new SqlParameter("finished_at", SqlDbType.DateTimeOffset) { Value = (object?)job.FinishedAt ?? DBNull.Value });
    }

    private static void AddJson(DbCommand command, string name, string[][] rows) =>
        ((SqlCommand)command).Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(rows, SqlServerJson.Default.StringArrayArray) });

    private static async Task<List<CheckpointRow>> ReadCheckpointRows(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<CheckpointRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CheckpointRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
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

        private const string CheckpointColumns = "name, position, mode, status, error, updated_at";

        private const string JobColumns = "id, kind, args, status, progress, created_at, started_at, finished_at";

        private const string EventColumns = "global_position, event_id, tenant_id, stream_id, version, stream_type, event_type, event_version";

        private const string Now = "TODATETIMEOFFSET(SYSUTCDATETIME(), 0)";

        public readonly string EnsureCheckpoints = $"""
            INSERT INTO [{s}].[checkpoints] (name, mode, status)
            SELECT c.n, c.m, CASE WHEN c.m = N'inline' AND (SELECT value FROM [{s}].[position]) > 0 THEN N'rebuilding' ELSE N'running' END
            FROM OPENJSON(@checkpoints) WITH (n nvarchar(200) '$[0]', m nvarchar(20) '$[1]') AS c
            WHERE NOT EXISTS (SELECT 1 FROM [{s}].[checkpoints] WITH (UPDLOCK, HOLDLOCK) WHERE name = c.n)
            """;

        public readonly string ReadCheckpoints = $"SELECT {CheckpointColumns} FROM [{s}].[checkpoints] ORDER BY name";

        public readonly string LockCheckpointBatch =
            $"SELECT {CheckpointColumns} FROM [{s}].[checkpoints] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE name = @name";

        public readonly string LockCheckpointExclusive =
            $"SELECT {CheckpointColumns} FROM [{s}].[checkpoints] WITH (XLOCK, ROWLOCK) WHERE name = @name";

        public readonly string UpdateCheckpoint = $"""
            UPDATE [{s}].[checkpoints] SET position = @position, mode = @mode, status = @status, error = @error, updated_at = {Now}
            WHERE name = @name
            """;

        public readonly string ReadStatuses = $"""
            SELECT name, status FROM [{s}].[checkpoints]
            WHERE name IN (SELECT n FROM OPENJSON(@names) WITH (n nvarchar(200) '$[0]'))
            """;

        // The head is read first. Under locking READ COMMITTED it waits for an append in flight to commit or roll back,
        // so every position the scan reads is committed and none can be reused behind it.
        private string Head => $"DECLARE @head bigint = (SELECT value FROM [{s}].[position]);";

        public string ReadEventsAfter => $"""
            {Head}
            SELECT TOP (@limit) {EventColumns}, payload, metadata, occurred_at
            FROM [{s}].[events] WHERE global_position > @after AND global_position <= @head ORDER BY global_position
            """;

        public string ReadEventsAfterFiltered => $"""
            {Head}
            SELECT TOP (@limit) {EventColumns},
                CASE WHEN t.n IS NOT NULL THEN payload END,
                CASE WHEN t.n IS NOT NULL THEN metadata END,
                occurred_at
            FROM [{s}].[events] e
            LEFT JOIN (SELECT DISTINCT n FROM OPENJSON(@types) WITH (n nvarchar(200) '$[0]')) t ON t.n = e.event_type COLLATE Latin1_General_100_BIN2
            WHERE global_position > @after AND global_position <= @head ORDER BY global_position
            """;

        public readonly string ReadHead = $"SELECT value FROM [{s}].[position]";

        public readonly string LockCounter = $"SELECT value FROM [{s}].[position] WITH (UPDLOCK, HOLDLOCK)";

        public readonly string InsertJob = $"""
            INSERT INTO [{s}].[jobs] (id, kind, args, status, progress, started_at, finished_at)
            VALUES (@id, @kind, @args, @status, @progress, @started_at, @finished_at)
            """;

        public readonly string ClaimJob =
            $"SELECT TOP (1) {JobColumns} FROM [{s}].[jobs] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE status = N'queued' ORDER BY created_at, id";

        public readonly string UpdateJob = $"""
            UPDATE [{s}].[jobs] SET status = @status, args = @args, progress = @progress, started_at = @started_at, finished_at = @finished_at,
                updated_at = {Now}, kind = @kind
            WHERE id = @id
            """;

        public readonly string ReadJob = $"SELECT {JobColumns} FROM [{s}].[jobs] WHERE id = @id";

        public readonly string ReadJobs = $"SELECT TOP (@limit) {JobColumns} FROM [{s}].[jobs] ORDER BY created_at DESC, id DESC";

        public readonly string ReadStreamKeys = $"""
            SELECT TOP (@limit) tenant_id, stream_id FROM [{s}].[streams]
            WHERE stream_type = @stream_type AND deleted_at IS NULL
                AND (tenant_id > @after_tenant OR (tenant_id = @after_tenant AND stream_id > @after_stream))
            ORDER BY tenant_id, stream_id
            """;

        public readonly string ShredTenant = $"""
            UPDATE [{s}].[master_keys] SET wrapped_key = 0x, wrapped_by = N'shredded' WHERE tenant_id = @tenant AND key_version > 0;
            DELETE FROM [{s}].[subject_keys] WHERE tenant_id = @tenant;
            DELETE FROM [{s}].[subject_streams] WHERE tenant_id = @tenant;
            UPDATE [{s}].[streams] SET state = NULL WHERE tenant_id = @tenant;
            """;

        public readonly string ReadMasterKeys = $"SELECT tenant_id, key_version, wrapped_key, wrapped_by FROM [{s}].[master_keys] ORDER BY tenant_id, key_version";

        public readonly string InsertMasterKey = $"""
            INSERT INTO [{s}].[master_keys] (tenant_id, key_version, wrapped_key, wrapped_by)
            SELECT @tenant, @version, @wrapped, @wrapped_by
            WHERE NOT EXISTS (SELECT 1 FROM [{s}].[master_keys] WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND key_version = @version)
            """;

        public readonly string UpdateMasterKey =
            $"UPDATE [{s}].[master_keys] SET wrapped_key = @wrapped, wrapped_by = @wrapped_by WHERE tenant_id = @tenant AND key_version = @version";

        public readonly string ReadSubjectKey =
            $"SELECT key_id, wrapped_key FROM [{s}].[subject_keys] WITH (HOLDLOCK, ROWLOCK) WHERE tenant_id = @tenant AND subject_id = @subject";

        public readonly string InsertSubjectKey = $"""
            INSERT INTO [{s}].[subject_keys] (tenant_id, subject_id, key_id, wrapped_key)
            SELECT @tenant, @subject, @key_id, @wrapped
            WHERE NOT EXISTS (SELECT 1 FROM [{s}].[subject_keys] WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND subject_id = @subject)
            """;

        public readonly string ReadSubjectKeysById = $"""
            SELECT key_id, wrapped_key FROM [{s}].[subject_keys]
            WHERE tenant_id = @tenant AND key_id IN (SELECT i FROM OPENJSON(@ids) WITH (i nvarchar(100) '$[0]'))
            """;

        public readonly string DeleteSubjectKey = $"""
            UPDATE [{s}].[streams] SET state = NULL
            WHERE tenant_id = @tenant AND stream_id IN (SELECT stream_id FROM [{s}].[subject_streams] WHERE tenant_id = @tenant AND subject_id = @subject);
            DELETE FROM [{s}].[subject_keys] WHERE tenant_id = @tenant AND subject_id = @subject;
            """;

        public readonly string RecordSubjectStreams = $"""
            INSERT INTO [{s}].[subject_streams] (tenant_id, subject_id, stream_id)
            SELECT DISTINCT @tenant, j.s, @stream FROM OPENJSON(@subjects) WITH (s nvarchar(100) '$[0]') AS j
            WHERE NOT EXISTS (
                SELECT 1 FROM [{s}].[subject_streams] WITH (UPDLOCK, HOLDLOCK)
                WHERE tenant_id = @tenant AND subject_id = j.s COLLATE Latin1_General_100_BIN2 AND stream_id = @stream)
            """;

        public readonly string ReadSubjectStreams =
            $"SELECT stream_id FROM [{s}].[subject_streams] WHERE tenant_id = @tenant AND subject_id = @subject ORDER BY stream_id";

        public readonly string DeleteSubjectStream =
            $"DELETE FROM [{s}].[subject_streams] WHERE {Key} AND subject_id = @subject";

        public readonly string DeleteStreamData = $"""
            DELETE FROM [{s}].[events] WHERE {Key} AND version < @keep;
            DELETE FROM [{s}].[subject_streams] WHERE {Key};
            UPDATE [{s}].[streams] SET state = NULL, state_version = 0, state_at = 0, deleted_at = {Now}, updated_at = {Now} WHERE {Key};
            """;

        public readonly string SaveSnapshot = $"""
            UPDATE [{s}].[streams] SET state = @state, state_version = @state_version, state_at = @state_at
            WHERE {Key} AND version = @state_at
            """;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[][]))]
internal sealed partial class SqlServerJson : System.Text.Json.Serialization.JsonSerializerContext;
