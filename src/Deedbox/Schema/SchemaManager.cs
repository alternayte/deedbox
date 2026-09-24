using System.Text;

namespace Deedbox;

internal static class SchemaManager
{
    /// <summary>The SQL for every migration after <paramref name="fromVersion"/>, ready for a DBA or a migration tool.</summary>
    public static string Script(DeedboxProvider provider, int fromVersion) =>
        SchemaScript.Render(provider.Migrations, provider.Schema, fromVersion);

    /// <summary>
    /// Applies every pending migration in one transaction, under a lock, so concurrent pods apply it once.
    /// </summary>
    /// <returns>The schema version before and after.</returns>
    public static async Task<(int From, int To)> Apply(DeedboxProvider provider, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await provider.AcquireSchemaLock(connection, transaction, ct);
        var current = await provider.ReadSchemaVersion(connection, transaction, ct);

        foreach (var migration in provider.Migrations.Where(m => m.Version > current))
        {
            foreach (var batch in provider.Batches(migration.ScriptFor(provider.Schema)))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
#pragma warning disable CA2100 // Embedded migration script.
                command.CommandText = batch;
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
        return (current, Math.Max(current, provider.LatestSchemaVersion));
    }

    /// <summary>Fails with the exact fix when the database schema is older than this build needs.</summary>
    public static async Task Verify(DeedboxProvider provider, CancellationToken ct)
    {
        int current;
        await using (var connection = provider.CreateConnection())
        {
            await connection.OpenAsync(ct);
            current = await provider.ReadSchemaVersion(connection, null, ct);
        }

        var needed = provider.LatestSchemaVersion;
        if (current >= needed)
            return;

        var state = current == 0 ? $"does not exist" : $"is at version {current}";
        throw new DeedboxException(Errors.SchemaBehind,
            $"Deedbox schema '{provider.Schema}' {state}, and this build needs version {needed}. " +
            $"Apply it with 'deedbox schema apply --provider {provider.Name} --schema {provider.Schema} --connection <connection string>', " +
            $"or print the SQL with 'deedbox schema script --provider {provider.Name} --schema {provider.Schema} --from {current}', " +
            "or call ApplySchemaOnStartup() in AddDeedbox.");
    }
}

internal static class SchemaScript
{
    public static string Render(IReadOnlyList<Migration> migrations, string schema, int fromVersion)
    {
        var sql = new StringBuilder();
        foreach (var migration in migrations.Where(m => m.Version > fromVersion))
        {
            sql.Append("-- Deedbox migration ").Append(migration.Version).Append(": ").AppendLine(migration.Name);
            sql.AppendLine(migration.ScriptFor(schema).TrimEnd());
            sql.AppendLine();
        }

        return sql.ToString();
    }
}
