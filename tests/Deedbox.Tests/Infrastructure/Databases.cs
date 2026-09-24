using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Deedbox.Tests.Infrastructure.Databases))]

namespace Deedbox.Tests.Infrastructure;

public enum Db
{
    Postgres,
    SqlServer,

    /// <summary>SQL Server with READ_COMMITTED_SNAPSHOT on, the Azure SQL default.</summary>
    SqlServerRcsi,
}

/// <summary>
/// One Postgres and one SQL Server for the whole test run. Set DEEDBOX_TEST_POSTGRES or
/// DEEDBOX_TEST_SQLSERVER to a connection string to use an existing server instead of a container.
/// </summary>
public sealed class Databases : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private MsSqlContainer? _sqlServer;

    public string Postgres { get; private set; } = "";
    public string SqlServer { get; private set; } = "";

    public string SqlServerRcsi { get; private set; } = "";

    public string ConnectionString(Db db) => db switch
    {
        Db.Postgres => Postgres,
        Db.SqlServer => SqlServer,
        _ => SqlServerRcsi,
    };

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(StartPostgres(), StartSqlServer());

        // Tables the EF Core tests share, created once so test classes never race to create them.
        await using (var pg = new Npgsql.NpgsqlConnection(Postgres))
        {
            await pg.OpenAsync();
            await EfTables.Ensure(pg, Db.Postgres, CancellationToken.None);
        }

        await using var sql = new SqlConnection(SqlServer);
        await sql.OpenAsync();
        await EfTables.Ensure(sql, Db.SqlServer, CancellationToken.None);
    }

    private async Task StartPostgres()
    {
        var external = Environment.GetEnvironmentVariable("DEEDBOX_TEST_POSTGRES");
        if (!string.IsNullOrEmpty(external))
        {
            Postgres = external;
            return;
        }

        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "max_connections=500")
            .Build();
        await _postgres.StartAsync();
        Postgres = _postgres.GetConnectionString() + ";Maximum Pool Size=200";
    }

    private async Task StartSqlServer()
    {
        var external = Environment.GetEnvironmentVariable("DEEDBOX_TEST_SQLSERVER");
        if (!string.IsNullOrEmpty(external))
        {
            SqlServer = external;
        }
        else
        {
            _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _sqlServer.StartAsync();
            SqlServer = _sqlServer.GetConnectionString() + ";Max Pool Size=200";
        }

        await using var connection = new SqlConnection(SqlServer);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID('deedbox_rcsi') IS NULL
            BEGIN
                CREATE DATABASE deedbox_rcsi;
                ALTER DATABASE deedbox_rcsi SET READ_COMMITTED_SNAPSHOT ON;
            END
            """;
        await command.ExecuteNonQueryAsync();
        SqlServerRcsi = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = "deedbox_rcsi" }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_sqlServer is not null) await _sqlServer.DisposeAsync();
    }
}
