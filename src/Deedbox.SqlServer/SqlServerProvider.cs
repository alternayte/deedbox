using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Deedbox.SqlServer;

internal sealed partial class SqlServerProvider : DeedboxProvider
{
    internal static readonly IReadOnlyList<Migration> AllMigrations =
        Migration.LoadEmbedded(typeof(SqlServerProvider).Assembly, "Deedbox.SqlServer.Schema.");

    private readonly string _connectionString;

    public SqlServerProvider(string connectionString, string schema)
        : base(schema)
    {
        _connectionString = connectionString;
        Sql = new Statements(Schema);
    }

    public override string Name => "sqlserver";

    public override IReadOnlyList<Migration> Migrations => AllMigrations;

    private Statements Sql { get; }

    public override DbConnection CreateConnection() => new SqlConnection(_connectionString);

    public override IEnumerable<string> Batches(string script) =>
        GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b));

    public override async Task AcquireSchemaLock(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = -1;
            SELECT @result;
            """);
        Add(command, "resource", "deedbox:schema:" + Schema);
        var result = (int)(await command.ExecuteScalarAsync(ct))!;
        if (result < 0)
            throw new InvalidOperationException($"sp_getapplock returned {result} for the Deedbox schema lock.");
    }

    public override async Task<int> ReadSchemaVersion(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using (var exists = Command(connection, transaction, "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END"))
        {
            Add(exists, "table", $"[{Schema}].[schema_version]");
            if ((int)(await exists.ExecuteScalarAsync(ct))! == 0)
                return 0;
        }

        await using var command = Command(connection, transaction, Sql.SchemaVersion);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    private sealed class Statements(string s)
    {
        public readonly string SchemaVersion = $"SELECT ISNULL(MAX(version), 0) FROM [{s}].[schema_version]";
    }
}
