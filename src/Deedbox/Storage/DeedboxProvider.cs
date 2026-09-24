using System.Data.Common;

namespace Deedbox;

/// <summary>
/// The SQL a database needs. Every algorithm lives in the core; a provider only supplies statements
/// for one database, bound to one schema.
/// </summary>
internal abstract class DeedboxProvider : IAsyncDisposable
{
    protected DeedboxProvider(string schema)
    {
        Schema = SchemaName.Validate(schema);
    }

    public string Schema { get; }

    /// <summary>The provider name the CLI and error messages use: postgres or sqlserver.</summary>
    public abstract string Name { get; }

    public abstract IReadOnlyList<Migration> Migrations { get; }

    public int LatestSchemaVersion => Migrations[^1].Version;

    /// <summary>A new, closed connection.</summary>
    public abstract DbConnection CreateConnection();

    /// <summary>Splits one migration script into the batches the database executes one by one.</summary>
    public abstract IEnumerable<string> Batches(string script);

    /// <summary>Takes the schema migration lock for the rest of <paramref name="transaction"/>.</summary>
    public abstract Task AcquireSchemaLock(DbConnection connection, DbTransaction transaction, CancellationToken ct);

    /// <summary>The highest applied migration, or 0 when the schema does not exist.</summary>
    public abstract Task<int> ReadSchemaVersion(DbConnection connection, DbTransaction? transaction, CancellationToken ct);

    /// <summary>
    /// Reads a stream row. With <paramref name="forUpdate"/>, the row stays locked until the transaction ends;
    /// when the row does not exist, a provider that can lock the gap (SQL Server) does so, and one that cannot
    /// (Postgres) relies on <see cref="InsertStream"/> reporting a lost race.
    /// </summary>
    public abstract Task<StreamRow?> ReadStream(DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, bool forUpdate, bool withState, CancellationToken ct);

    /// <summary>Creates a stream row. Returns false when another transaction created it first.</summary>
    public abstract Task<bool> InsertStream(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, string streamType, long version, Snapshot? snapshot, CancellationToken ct);

    /// <summary>Moves a stream from <paramref name="expectedVersion"/> to <paramref name="newVersion"/>. Returns false when the version did not match.</summary>
    public abstract Task<bool> UpdateStream(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long expectedVersion, long newVersion, Snapshot? snapshot, CancellationToken ct);

    /// <summary>
    /// Advances the position counter by the event count and inserts the events at the new positions, in one
    /// round trip. This is the last statement of an append: the counter row stays locked until commit.
    /// </summary>
    /// <returns>The position of the last event.</returns>
    public abstract Task<long> InsertEvents(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, string streamType, DateTimeOffset occurredAt, IReadOnlyList<NewEvent> events, CancellationToken ct);

    /// <summary>The stream's events with <paramref name="afterVersion"/> &lt; version &lt;= <paramref name="toVersion"/>, in version order.</summary>
    public abstract IAsyncEnumerable<StoredEvent> ReadStreamEvents(DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, long afterVersion, long toVersion, CancellationToken ct);

    /// <summary>Stores a snapshot, but only while the stream is still at <see cref="Snapshot.At"/>.</summary>
    public abstract Task SaveSnapshot(DbConnection connection, DbTransaction? transaction, string tenantId, string streamId, Snapshot snapshot, CancellationToken ct);

    /// <summary>Adds (stream type, event type, event version) rows to event_types; existing rows are left alone.</summary>
    public abstract Task RecordEventTypes(DbConnection connection, DbTransaction transaction, IReadOnlyList<EventTypeRow> types, CancellationToken ct);

    /// <summary>Every (stream type, event type, event version) the store has ever held.</summary>
    public abstract Task<List<EventTypeRow>> ReadEventTypes(DbConnection connection, CancellationToken ct);

    // ---- Async runner ----

    /// <summary>Adds checkpoint rows that do not exist yet, at position 0 and status running.</summary>
    public abstract Task EnsureCheckpoints(DbConnection connection, IReadOnlyList<(string Name, string Mode)> checkpoints, CancellationToken ct);

    /// <summary>Every checkpoint row.</summary>
    public abstract Task<List<CheckpointRow>> ReadCheckpoints(DbConnection connection, CancellationToken ct);

    /// <summary>
    /// Locks one checkpoint row for the rest of the transaction. <see cref="CheckpointLock.Batch"/> skips a row another
    /// runner holds and returns null; it does not block appends that read the row's status.
    /// <see cref="CheckpointLock.Exclusive"/> waits, and blocks appends that read the status until commit.
    /// </summary>
    public abstract Task<CheckpointRow?> LockCheckpoint(DbConnection connection, DbTransaction transaction, string name, CheckpointLock mode, CancellationToken ct);

    public abstract Task UpdateCheckpoint(DbConnection connection, DbTransaction transaction, CheckpointRow row, CancellationToken ct);

    /// <summary>
    /// Takes the shared gate lock of each inline projection, then reads their statuses in a later statement, so the
    /// read sees any status change whose exclusive gate lock it waited for. The shared locks last until commit.
    /// </summary>
    public abstract Task<Dictionary<string, string>> ReadInlineStatuses(DbConnection connection, DbTransaction transaction, IReadOnlyList<string> names, CancellationToken ct);

    /// <summary>
    /// Takes an inline projection's gate lock exclusively until commit. It waits for open appends that read the
    /// projection's status, and holds back new ones, so a status change never races an append.
    /// </summary>
    public abstract Task LockInlineGate(DbConnection connection, DbTransaction transaction, string name, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="limit"/> committed events after <paramref name="after"/>, in position order. Payload and
    /// metadata are read only for events whose type is in <paramref name="payloadTypes"/>; null means every type.
    /// </summary>
    public abstract Task<List<StoredEvent>> ReadEventsAfter(DbConnection connection, DbTransaction? transaction, long after, int limit, IReadOnlyList<string>? payloadTypes, CancellationToken ct);

    /// <summary>The highest committed position.</summary>
    public abstract Task<long> ReadHead(DbConnection connection, DbTransaction? transaction, CancellationToken ct);

    /// <summary>Locks the position counter until the transaction ends, so no append can commit meanwhile.</summary>
    public abstract Task<long> LockCounter(DbConnection connection, DbTransaction transaction, CancellationToken ct);

    public abstract Task InsertJob(DbConnection connection, DbTransaction? transaction, JobRow job, CancellationToken ct);

    /// <summary>Locks the oldest queued job, skipping jobs another runner holds.</summary>
    public abstract Task<JobRow?> ClaimJob(DbConnection connection, DbTransaction transaction, CancellationToken ct);

    public abstract Task UpdateJob(DbConnection connection, DbTransaction transaction, JobRow job, CancellationToken ct);

    public abstract Task<JobRow?> ReadJob(DbConnection connection, Guid id, CancellationToken ct);

    /// <summary>
    /// Calls <paramref name="wake"/> whenever events are appended, until cancelled. A provider without push
    /// notifications returns at once; the runner then relies on polling alone.
    /// </summary>
    public abstract Task Listen(Action wake, CancellationToken ct);

    // ---- Personal data ----

    /// <summary>Every master_keys row: tenant intermediate keys, and the database-mode master key at version 0 of the empty tenant.</summary>
    public abstract Task<List<MasterKeyRow>> ReadMasterKeys(DbConnection connection, DbTransaction? transaction, CancellationToken ct);

    /// <summary>Adds a master_keys row unless one with the same key exists. Returns false when it existed.</summary>
    public abstract Task<bool> InsertMasterKey(DbConnection connection, DbTransaction? transaction, MasterKeyRow row, CancellationToken ct);

    public abstract Task UpdateMasterKey(DbConnection connection, DbTransaction transaction, MasterKeyRow row, CancellationToken ct);

    public abstract Task DeleteMasterKey(DbConnection connection, DbTransaction transaction, string tenantId, int keyVersion, CancellationToken ct);

    /// <summary>A subject's key, locked until the transaction ends so an erasure cannot delete it mid-append.</summary>
    public abstract Task<SubjectKeyRow?> ReadSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct);

    /// <summary>Adds a subject key unless the subject already has one.</summary>
    public abstract Task InsertSubjectKey(DbConnection connection, DbTransaction transaction, SubjectKeyRow row, CancellationToken ct);

    /// <summary>The wrapped keys for these key IDs; IDs with no row (erased subjects) are absent.</summary>
    public abstract Task<Dictionary<string, byte[]>> ReadSubjectKeysById(DbConnection connection, DbTransaction? transaction, string tenantId, IReadOnlyList<string> keyIds, CancellationToken ct);

    /// <summary>
    /// Deletes a subject's key and clears the stored state of every stream that holds their data, so nothing reads
    /// their data after this commits: loads rebuild state from the now-redacted events.
    /// </summary>
    public abstract Task<int> DeleteSubjectKey(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, CancellationToken ct);

    /// <summary>Records which streams hold each subject's data; existing pairs are left alone.</summary>
    public abstract Task RecordSubjectStreams(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, IReadOnlyList<string> subjectIds, CancellationToken ct);

    public abstract Task<List<string>> ReadSubjectStreams(DbConnection connection, DbTransaction? transaction, string tenantId, string subjectId, CancellationToken ct);

    /// <summary>Removes one pair, returning 0 when another runner already removed it.</summary>
    public abstract Task<int> DeleteSubjectStream(DbConnection connection, DbTransaction transaction, string tenantId, string subjectId, string streamId, CancellationToken ct);

    /// <summary>
    /// Deletes a stream's events below <paramref name="keepFromVersion"/>, its subject pairs and its stored state, and
    /// marks the stream row deleted.
    /// </summary>
    public abstract Task DeleteStreamData(DbConnection connection, DbTransaction transaction, string tenantId, string streamId, long keepFromVersion, CancellationToken ct);

    /// <summary>Releases resources the provider created, such as a data source it built from a connection string.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Statements this provider has sent. The idle query budget test reads it.</summary>
    public long StatementCount => Interlocked.Read(ref _statements);

    private long _statements;

    protected DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        Interlocked.Increment(ref _statements);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // SQL text is built from provider constants and a validated schema name.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command;
    }

    protected static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

internal sealed record StreamRow(string StreamType, long Version, string? State, int StateVersion, long StateAt, DateTimeOffset? DeletedAt);

/// <summary>A stream's state as JSON, the state version it was written with, and the stream version it reflects.</summary>
internal sealed record Snapshot(string State, int StateVersion, long At);

internal sealed record NewEvent(Guid EventId, long Version, string EventType, int EventVersion, string Payload, string Metadata);

/// <summary>A stored event. Payload and Metadata are null when a filtered read skipped them.</summary>
internal sealed record StoredEvent(
    long GlobalPosition, Guid EventId, string TenantId, string StreamId, long Version, string StreamType,
    string EventType, int EventVersion, string? Payload, string? Metadata, DateTimeOffset OccurredAt);

internal sealed record EventTypeRow(string StreamType, string EventType, int EventVersion);

internal enum CheckpointLock
{
    Batch,
    Exclusive,
}

internal static class CheckpointStatus
{
    public const string Running = "running";
    public const string Stalled = "stalled";
    public const string Rebuilding = "rebuilding";
}

internal static class CheckpointMode
{
    public const string Inline = "inline";
    public const string Async = "async";
    public const string Subscription = "subscription";
}

internal sealed record CheckpointRow(string Name, long Position, string Mode, string Status, string? Error, DateTimeOffset UpdatedAt);

internal sealed record JobRow(Guid Id, string Kind, string Args, string Status, string? Progress, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

internal static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

internal sealed record MasterKeyRow(string TenantId, int KeyVersion, byte[] WrappedKey, string WrappedBy);

internal sealed record SubjectKeyRow(string TenantId, string SubjectId, string KeyId, byte[] WrappedKey);
