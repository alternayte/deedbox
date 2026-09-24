using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Deedbox.Postgres;

internal sealed partial class PostgresProvider : DeedboxProvider
{
    internal static readonly IReadOnlyList<Migration> AllMigrations =
        Migration.LoadEmbedded(typeof(PostgresProvider).Assembly, "Deedbox.Postgres.Schema.");

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgresProvider(NpgsqlDataSource dataSource, bool ownsDataSource, string schema)
        : base(schema)
    {
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        Sql = new Statements(Schema);
    }

    public override string Name => "postgres";

    public override IReadOnlyList<Migration> Migrations => AllMigrations;

    private Statements Sql { get; }

    public override DbConnection CreateConnection() => _dataSource.CreateConnection();

    public override IEnumerable<string> Batches(string script) => [script];

    public override async Task AcquireSchemaLock(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(@key)");
        Add(command, "key", LockKey("deedbox:schema:" + Schema));
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<int> ReadSchemaVersion(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using (var exists = Command(connection, transaction, "SELECT to_regclass(@table) IS NOT NULL"))
        {
            Add(exists, "table", Schema + ".schema_version");
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
                return 0;
        }

        await using var command = Command(connection, transaction, Sql.SchemaVersion);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _dataSource.DisposeAsync();
    }

    /// <summary>A stable 64-bit advisory lock key for a name.</summary>
    private static long LockKey(string name) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0);

    private sealed class Statements(string s)
    {
        public readonly string SchemaVersion = $"SELECT coalesce(max(version), 0) FROM {s}.schema_version";
    }
}
