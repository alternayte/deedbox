using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Deedbox.Tests.Infrastructure.Databases))]

namespace Deedbox.Tests.Infrastructure;

public enum Db
{
    Postgres,
    SqlServer,
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

    public string ConnectionString(Db db) => db == Db.Postgres ? Postgres : SqlServer;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(StartPostgres(), StartSqlServer());
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
            return;
        }

        _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _sqlServer.StartAsync();
        SqlServer = _sqlServer.GetConnectionString() + ";Max Pool Size=200";
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_sqlServer is not null) await _sqlServer.DisposeAsync();
    }
}
