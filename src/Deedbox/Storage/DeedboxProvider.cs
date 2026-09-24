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
