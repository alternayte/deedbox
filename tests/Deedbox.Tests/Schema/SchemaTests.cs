using Deedbox.SqlServer;
using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deedbox.Tests.Schema;

public sealed class PostgresSchemaTests(Databases databases) : SchemaTests(databases, Db.Postgres);

public sealed class SqlServerSchemaTests(Databases databases) : SchemaTests(databases, Db.SqlServer)
{
    private static readonly string[] JsonColumns = ["streams.state", "events.payload", "events.metadata", "checkpoints.error", "jobs.args", "jobs.progress"];

    [Fact]
    public async Task Native_json_converts_an_existing_store_on_a_server_with_the_type_and_fails_with_DBX034_elsewhere()
    {
        await using (var plain = new SqlServerProvider(ConnectionString, Schema, nativeJson: false))
            await SchemaManager.Apply(plain, Ct);
        var plainServices = new ServiceCollection().AddDeedbox(b => b.UseSqlServer(ConnectionString).Schema(Schema).Stream<Counter>(s => s.Events<Incremented>()));
        await using (var before = plainServices.BuildServiceProvider())
            await before.CreateScope().ServiceProvider.GetRequiredService<IEventStore>()
                .Append("counter-1", ExpectedVersion.NoStream, [new Incremented(2), new Incremented(3)]);
        var supported = await Scalar<int>("SELECT CASE WHEN TYPE_ID(N'json') IS NULL THEN 0 ELSE 1 END") == 1;

        using (var notApplied = BuildHost(b => b.UseSqlServer(ConnectionString, o => o.NativeJson = true).Schema(Schema).Stream<Counter>(s => s.Events<Incremented>())))
        {
            var error = await Assert.ThrowsAsync<DeedboxException>(() => notApplied.StartAsync(Ct));
            Assert.Equal(Errors.StorageOptions, error.Code);
            Assert.Contains(supported ? "still nvarchar(max)" : "SQL Server 2025", error.Message, StringComparison.Ordinal);
        }

        if (!supported)
        {
            await using var json = new SqlServerProvider(ConnectionString, Schema, nativeJson: true);
            Assert.Equal(Errors.StorageOptions, (await Assert.ThrowsAsync<DeedboxException>(() => SchemaManager.Apply(json, Ct))).Code);
            var plainTypes = await ColumnTypes();
            Assert.All(JsonColumns, c => Assert.Equal((c, "nvarchar"), (c, plainTypes[c])));
            return;
        }

        using var host = BuildHost(b => b.UseSqlServer(ConnectionString, o => o.NativeJson = true).Schema(Schema).ApplySchemaOnStartup()
            .Stream<Counter>(s => s.Events<Incremented>()));
        await host.StartAsync(Ct);
        var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

        Assert.Equal(new Counter(5, 2), (await store.Load<Counter>("counter-1")).State);
        await store.Append("counter-1", ExpectedVersion.Exact(2), [new Incremented(1)]);
        Assert.Equal(new Counter(6, 3), (await store.Load<Counter>("counter-1")).State);
        var types = await ColumnTypes();
        Assert.All(JsonColumns, c => Assert.Equal((c, "json"), (c, types[c])));
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Stream_ids_compare_case_sensitively()
    {
        await using var provider = CreateProvider();
        await SchemaManager.Apply(provider, Ct);

        foreach (var column in new[] { "streams.stream_id", "streams.tenant_id", "events.stream_id", "events.event_type", "subject_keys.subject_id" })
        {
            var parts = column.Split('.');
            var collation = await Scalar<string>(
                $"SELECT collation_name FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = '{parts[0]}' AND column_name = '{parts[1]}'");
            Assert.Equal((column, "Latin1_General_100_BIN2"), (column, collation));
        }
    }
}

public abstract class SchemaTests(Databases databases, Db db) : DatabaseTest(databases, db)
{
    private static readonly string[] Tables =
    [
        "checkpoints", "event_types", "events", "instances", "jobs", "master_keys", "position",
        "schema_version", "streams", "subject_keys", "subject_streams",
    ];

    [Fact]
    public async Task Apply_creates_every_table_at_the_latest_version()
    {
        await using var provider = CreateProvider();

        var (from, to) = await SchemaManager.Apply(provider, Ct);

        Assert.Equal(0, from);
        Assert.Equal(provider.LatestSchemaVersion, to);
        Assert.Equal(Tables, await TablesInSchema());
        Assert.Equal(0L, await Scalar<long>($"SELECT value FROM {Table("position")}"));
    }

    [Fact]
    public async Task Apply_again_changes_nothing()
    {
        await using var provider = CreateProvider();
        await SchemaManager.Apply(provider, Ct);

        var (from, to) = await SchemaManager.Apply(provider, Ct);

        Assert.Equal(provider.LatestSchemaVersion, from);
        Assert.Equal(from, to);
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("position")}"));
    }

    [Fact]
    public async Task Concurrent_apply_from_many_instances_applies_once()
    {
        var providers = Enumerable.Range(0, 8).Select(_ => CreateProvider()).ToList();

        await Task.WhenAll(providers.Select(p => SchemaManager.Apply(p, Ct)));

        Assert.Equal(providers[0].LatestSchemaVersion, await Scalar<int>($"SELECT MAX(version) FROM {Table("schema_version")}"));
        Assert.Equal(providers[0].LatestSchemaVersion, await Scalar<int>($"SELECT COUNT(*) FROM {Table("schema_version")}"));
        Assert.Equal(1, await Scalar<int>($"SELECT COUNT(*) FROM {Table("position")}"));
        foreach (var p in providers)
            await p.DisposeAsync();
    }

    [Fact]
    public async Task Printed_script_run_twice_by_hand_builds_the_schema()
    {
        await using var provider = CreateProvider();
        var script = SchemaManager.Script(provider, fromVersion: 0);

        for (var run = 0; run < 2; run++)
        {
            await using var connection = await OpenConnection();
            foreach (var batch in provider.Batches(script))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                await command.ExecuteNonQueryAsync(Ct);
            }
        }

        Assert.Equal(Tables, await TablesInSchema());
        await using var check = await OpenConnection();
        Assert.Equal(provider.LatestSchemaVersion, await provider.ReadSchemaVersion(check, null, Ct));
    }

    [Fact]
    public async Task Script_after_the_latest_version_is_empty()
    {
        await using var provider = CreateProvider();

        // Only the storage options batch remains, when the provider has one.
        Assert.Equal(provider.StorageScript is null ? "" : SchemaScript.Render([], provider.Schema, 0, provider.StorageScript),
            SchemaManager.Script(provider, provider.LatestSchemaVersion));
    }

    [Fact]
    [Trait("Regression", "Anthology: xid8 mapped to uint")]
    public async Task Columns_have_the_types_the_code_reads()
    {
        await using var provider = CreateProvider();
        await SchemaManager.Apply(provider, Ct);

        var expected = Db == Db.Postgres
            ? new Dictionary<string, string>
            {
                ["events.global_position"] = "bigint",
                ["events.version"] = "bigint",
                ["events.event_id"] = "uuid",
                ["events.event_version"] = "integer",
                ["events.payload"] = "jsonb",
                ["events.metadata"] = "jsonb",
                ["events.occurred_at"] = "timestamp with time zone",
                ["events.stream_id"] = "text",
                ["streams.version"] = "bigint",
                ["streams.state"] = "jsonb",
                ["streams.state_version"] = "integer",
                ["streams.state_at"] = "bigint",
                ["position.value"] = "bigint",
                ["checkpoints.position"] = "bigint",
                ["checkpoints.aux"] = "bigint",
            }
            : new Dictionary<string, string>
            {
                ["events.global_position"] = "bigint",
                ["events.version"] = "bigint",
                ["events.event_id"] = "uniqueidentifier",
                ["events.event_version"] = "int",
                ["events.payload"] = Databases.NativeJson ? "json" : "nvarchar",
                ["events.metadata"] = Databases.NativeJson ? "json" : "nvarchar",
                ["events.occurred_at"] = "datetimeoffset",
                ["events.stream_id"] = "nvarchar",
                ["streams.version"] = "bigint",
                ["streams.state"] = Databases.NativeJson ? "json" : "nvarchar",
                ["streams.state_version"] = "int",
                ["streams.state_at"] = "bigint",
                ["position.value"] = "bigint",
                ["checkpoints.position"] = "bigint",
                ["checkpoints.aux"] = "bigint",
            };

        var actual = await ColumnTypes();
        foreach (var (column, type) in expected)
            Assert.Equal((column, type), (column, actual[column]));
    }

    [Fact]
    public async Task Startup_fails_with_the_fix_when_the_schema_is_missing()
    {
        using var host = BuildHost(b => UseDatabase(b));

        var error = await Assert.ThrowsAsync<DeedboxException>(() => host.StartAsync(Ct));

        Assert.Equal("DBX001", error.Code);
        Assert.Contains("deedbox schema apply", error.Message, StringComparison.Ordinal);
        Assert.Contains($"--schema {Schema} --from 0", error.Message, StringComparison.Ordinal);
        Assert.Contains("ApplySchemaOnStartup()", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://deedbox-docs.pages.dev/reference/errors/dbx001/", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_applies_the_schema_when_opted_in()
    {
        using var host = BuildHost(b => UseDatabase(b).ApplySchemaOnStartup());

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);

        await using var provider = CreateProvider();
        await using var connection = await OpenConnection();
        Assert.Equal(provider.LatestSchemaVersion, await provider.ReadSchemaVersion(connection, null, Ct));
    }

    [Fact]
    public async Task Startup_passes_when_the_schema_is_current()
    {
        await using (var provider = CreateProvider())
            await SchemaManager.Apply(provider, Ct);

        using var host = BuildHost(b => UseDatabase(b));

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    private protected static IHost BuildHost(Action<DeedboxBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDeedbox(configure);
        return builder.Build();
    }

    private async Task<string[]> TablesInSchema()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{Schema}' ORDER BY table_name";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            names.Add(reader.GetString(0));
        return [.. names.Order(StringComparer.Ordinal)];
    }

    private protected async Task<Dictionary<string, string>> ColumnTypes()
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = '{Schema}'";
        var types = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            types[$"{reader.GetString(0)}.{reader.GetString(1)}"] = reader.GetString(2);
        return types;
    }
}
