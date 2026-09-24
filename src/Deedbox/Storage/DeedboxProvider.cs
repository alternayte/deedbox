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

    /// <summary>Releases resources the provider created, such as a data source it built from a connection string.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
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

internal sealed record StoredEvent(
    long GlobalPosition, Guid EventId, string TenantId, string StreamId, long Version, string StreamType,
    string EventType, int EventVersion, string Payload, string Metadata, DateTimeOffset OccurredAt);

internal sealed record EventTypeRow(string StreamType, string EventType, int EventVersion);
