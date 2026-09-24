using System.Data.Common;
using System.Text;

namespace Deedbox;

internal static class SchemaManager
{
    /// <summary>The SQL for every migration after <paramref name="fromVersion"/>, ready for a DBA or a migration tool.</summary>
    public static string Script(DeedboxProvider provider, int fromVersion) =>
        SchemaScript.Render(provider.Migrations, provider.Schema, fromVersion, provider.StorageScript);

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

        if (provider.StorageScript is not null && await provider.StorageProblem(connection, transaction, supportOnly: true, ct) is { } problem)
            throw new DeedboxException(Errors.StorageOptions, problem);

        foreach (var migration in provider.Migrations.Where(m => m.Version > current))
            await Run(provider, connection, transaction, migration.ScriptFor(provider.Schema), ct);

        if (provider.StorageScript is { } storage)
            await Run(provider, connection, transaction, storage, ct);

        await transaction.CommitAsync(ct);
        return (current, Math.Max(current, provider.LatestSchemaVersion));
    }

    private static async Task Run(DeedboxProvider provider, DbConnection connection, DbTransaction transaction, string script, CancellationToken ct)
    {
        foreach (var batch in provider.Batches(script))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // Embedded migration script.
            command.CommandText = batch;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Fails with the exact fix when the database schema is older than this build needs, or does not have the
    /// provider's storage options.
    /// </summary>
    public static async Task Verify(DeedboxProvider provider, CancellationToken ct)
    {
        int current;
        string? storage;
        await using (var connection = provider.CreateConnection())
        {
            await connection.OpenAsync(ct);
            current = await provider.ReadSchemaVersion(connection, null, ct);
            storage = current == 0 ? null : await provider.StorageProblem(connection, null, supportOnly: false, ct);
        }

        var needed = provider.LatestSchemaVersion;
        if (current >= needed)
        {
            if (storage is not null)
                throw new DeedboxException(Errors.StorageOptions, storage);
            return;
        }

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
    public static string Render(IReadOnlyList<Migration> migrations, string schema, int fromVersion, string? storage = null)
    {
        var sql = new StringBuilder();
        foreach (var migration in migrations.Where(m => m.Version > fromVersion))
        {
            sql.Append("-- Deedbox migration ").Append(migration.Version).Append(": ").AppendLine(migration.Name);
            sql.AppendLine(migration.ScriptFor(schema).TrimEnd());
            sql.AppendLine();
        }

        if (storage is not null)
        {
            sql.AppendLine("-- Deedbox storage options. Runs after the migrations, every time; it changes only what is not in place yet.");
            sql.AppendLine(storage.TrimEnd());
            sql.AppendLine();
        }

        return sql.ToString();
    }
}
