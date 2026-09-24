using System.CommandLine;
using Deedbox.Postgres;
using Deedbox.SqlServer;
using Npgsql;

namespace Deedbox.Cli;

internal static class CliApp
{
    private const string Postgres = "postgres";
    private const string SqlServer = "sqlserver";

    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            return await Build(output).Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });
        }
        catch (DeedboxException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static RootCommand Build(TextWriter output)
    {
        var provider = new Option<string>("--provider")
        {
            Description = "The database: postgres or sqlserver.",
            Required = true,
        };
        provider.AcceptOnlyFromAmong(Postgres, SqlServer);

        var schema = new Option<string>("--schema")
        {
            Description = "The Deedbox schema name.",
            DefaultValueFactory = _ => SchemaName.Default,
        };

        var from = new Option<int>("--from")
        {
            Description = "The schema version the database has now; 0 for a new database.",
            DefaultValueFactory = _ => 0,
        };

        var connection = new Option<string?>("--connection")
        {
            Description = "The connection string. Defaults to the DEEDBOX_CONNECTION environment variable.",
        };

        var script = new Command("script", "Print the SQL for every migration after --from.") { provider, schema, from };
        script.SetAction(async (result, ct) =>
        {
            var name = SchemaName.Validate(result.GetValue(schema)!);
            var migrations = result.GetValue(provider) == Postgres ? PostgresProvider.AllMigrations : SqlServerProvider.AllMigrations;
            await output.WriteAsync(SchemaScript.Render(migrations, name, result.GetValue(from)));
            return 0;
        });

        var apply = new Command("apply", "Apply pending migrations under a database lock.") { provider, schema, connection };
        apply.SetAction(async (result, ct) =>
        {
            var connectionString = result.GetValue(connection) ?? Environment.GetEnvironmentVariable("DEEDBOX_CONNECTION");
            if (string.IsNullOrEmpty(connectionString))
            {
                await result.InvocationConfiguration.Error.WriteLineAsync("Pass --connection or set DEEDBOX_CONNECTION.");
                return 1;
            }

            var name = SchemaName.Validate(result.GetValue(schema)!);
            await using DeedboxProvider target = result.GetValue(provider) == Postgres
                ? new PostgresProvider(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, name)
                : new SqlServerProvider(connectionString, name);

            var (before, after) = await SchemaManager.Apply(target, ct);
            await output.WriteLineAsync(before == after
                ? $"Schema '{name}' is up to date at version {after}."
                : $"Schema '{name}' migrated from version {before} to {after}.");
            return 0;
        });

        return new RootCommand("The Deedbox command-line tool.")
        {
            new Command("schema", "Print or apply the Deedbox schema.") { script, apply },
        };
    }
}
