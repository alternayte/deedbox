using System.Data.Common;
using Deedbox.Postgres;
using Deedbox.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Deedbox.Tests.Infrastructure;

/// <summary>
/// A test that runs against one provider. Each test class instance gets its own schema, so tests
/// share a server without sharing state.
/// </summary>
public abstract class DatabaseTest(Databases databases, Db db)
{
    protected Db Db { get; } = db;

    protected string Schema { get; } = "t" + Guid.NewGuid().ToString("N")[..16];

    protected string ConnectionString => databases.ConnectionString(Db);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal DeedboxProvider CreateProvider(string? schema = null) => Db == Db.Postgres
        ? new PostgresProvider(NpgsqlDataSource.Create(ConnectionString), ownsDataSource: true, schema ?? Schema)
        : new SqlServerProvider(ConnectionString, schema ?? Schema);

    protected DeedboxBuilder UseDatabase(DeedboxBuilder builder) => Db == Db.Postgres
        ? builder.UsePostgres(ConnectionString).Schema(Schema)
        : builder.UseSqlServer(ConnectionString).Schema(Schema);

    protected async Task<DbConnection> OpenConnection()
    {
        DbConnection connection = Db == Db.Postgres ? new NpgsqlConnection(ConnectionString) : new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    protected async Task<T> Scalar<T>(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A table name qualified with the test schema, quoted for the provider.</summary>
    protected string Table(string name) => Db == Db.Postgres ? $"{Schema}.{name}" : $"[{Schema}].[{name}]";
}
