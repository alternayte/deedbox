using Deedbox.SqlServer;

namespace Deedbox;

/// <summary>The Deedbox schema for SQL Server, as SQL for DBAs and migration tools.</summary>
public static class SqlServerSchema
{
    /// <summary>The schema version this build of Deedbox needs.</summary>
    public static int LatestVersion => SqlServerProvider.AllMigrations[^1].Version;

    /// <summary>
    /// The SQL for every migration after <paramref name="fromVersion"/>, with <c>GO</c> between batches.
    /// Pass it to <c>migrationBuilder.Sql(...)</c> in an EF Core migration, or to sqlcmd, DbUp or Flyway.
    /// The scripts are idempotent and record themselves in <c>schema_version</c>.
    /// </summary>
    /// <param name="fromVersion">The schema version the database has now; 0 for a new database.</param>
    /// <param name="schema">The schema name, <c>deedbox</c> by default.</param>
    public static string Script(int fromVersion = 0, string schema = "deedbox") =>
        SchemaScript.Render(SqlServerProvider.AllMigrations, SchemaName.Validate(schema), fromVersion);
}
