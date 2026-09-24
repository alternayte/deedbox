using Deedbox.Cli;
using Deedbox.Tests.Infrastructure;

namespace Deedbox.Tests.Schema;

public sealed class PostgresCliSchemaTests(Databases databases) : CliSchemaTests(databases, Db.Postgres)
{
    [Fact]
    public async Task Native_json_is_rejected_for_postgres()
    {
        var (code, _, error) = await Cli("schema", "script", "--provider", "postgres", "--native-json");

        Assert.Equal(1, code);
        Assert.Contains("--native-json is for SQL Server", error, StringComparison.Ordinal);
    }
}

public sealed class SqlServerCliSchemaTests(Databases databases) : CliSchemaTests(databases, Db.SqlServer)
{
    [Fact]
    public async Task Native_json_adds_the_conversion_to_the_script_and_apply_converts_or_fails_with_DBX034()
    {
        var (code, output, _) = await Cli("schema", "script", "--provider", "sqlserver", "--schema", Schema, "--native-json");
        Assert.Equal((0, SqlServerSchema.Script(0, Schema, nativeJson: true)), (code, output));
        Assert.Contains($"ALTER TABLE [{Schema}].[events] ALTER COLUMN payload json NOT NULL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("json NOT NULL", SqlServerSchema.Script(0, Schema), StringComparison.Ordinal);

        (code, output, var error) = await Cli("schema", "apply", "--provider", "sqlserver", "--schema", Schema, "--native-json", "--connection", ConnectionString);

        if (await Scalar<int>("SELECT CASE WHEN TYPE_ID(N'json') IS NULL THEN 0 ELSE 1 END") == 1)
        {
            Assert.Equal((0, ""), (code, error));
            Assert.Equal("json", await Scalar<string>(
                $"SELECT data_type FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'events' AND column_name = 'payload'"));
        }
        else
        {
            Assert.Equal(1, code);
            Assert.StartsWith("DBX034:", error, StringComparison.Ordinal);
        }
    }
}

public abstract class CliSchemaTests(Databases databases, Db db) : DatabaseTest(databases, db)
{
    private string ProviderName => Db == Db.Postgres ? "postgres" : "sqlserver";

    [Fact]
    public async Task Schema_script_prints_the_migrations_after_from()
    {
        var (code, output, _) = await Cli("schema", "script", "--provider", ProviderName, "--schema", Schema, "--from", "0");

        Assert.Equal(0, code);
        var expected = Db == Db.Postgres ? PostgresSchema.Script(0, Schema) : SqlServerSchema.Script(0, Schema);
        Assert.Equal(expected, output);
        Assert.Contains($"-- Deedbox migration 1: initial", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_apply_migrates_then_reports_up_to_date()
    {
        var (code, output, error) = await Cli("schema", "apply", "--provider", ProviderName, "--schema", Schema, "--connection", ConnectionString);
        Assert.Equal((0, ""), (code, error));
        Assert.Contains($"migrated from version 0 to", output, StringComparison.Ordinal);

        (code, output, _) = await Cli("schema", "apply", "--provider", ProviderName, "--schema", Schema, "--connection", ConnectionString);
        Assert.Equal(0, code);
        Assert.Contains("is up to date", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_schema_name_fails_with_its_code()
    {
        var (code, _, error) = await Cli("schema", "script", "--provider", ProviderName, "--schema", "Bad-Name");

        Assert.Equal(1, code);
        Assert.StartsWith("DBX003:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_provider_is_rejected()
    {
        var (code, _, error) = await Cli("schema", "script", "--provider", "mysql");

        Assert.NotEqual(0, code);
        Assert.Contains("mysql", error, StringComparison.Ordinal);
    }

    protected static async Task<(int Code, string Output, string Error)> Cli(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
