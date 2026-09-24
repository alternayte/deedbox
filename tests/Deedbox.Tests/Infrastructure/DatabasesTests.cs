using Microsoft.Data.SqlClient;
using Npgsql;

namespace Deedbox.Tests.Infrastructure;

public sealed class DatabasesTests(Databases databases)
{
    [Fact]
    public async Task Both_providers_accept_connections()
    {
        await using (var pg = new NpgsqlConnection(databases.Postgres))
        {
            await pg.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using var sql = new SqlConnection(databases.SqlServer);
        await sql.OpenAsync(TestContext.Current.CancellationToken);
    }
}
