using System.Text.Json;
using System.Text.Json.Serialization;
using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Deedbox.Tests.Streams;

public sealed class PostgresJsonTests(Databases databases) : JsonTests(databases, Db.Postgres)
{
    [Fact]
    public async Task An_app_owned_data_source_is_used_and_left_open()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var services = new ServiceCollection();
        services.AddDeedbox(b => b.UsePostgres(dataSource).Schema(Schema).Stream<Counter>(s => s.Events<Incremented>()));
        await using (var provider = services.BuildServiceProvider())
        {
            await SchemaManager.Apply(provider.GetRequiredService<DeedboxRuntime>().Provider, Ct);
            await StoreFrom(provider).Append(NewStreamId(), ExpectedVersion.NoStream, [new Incremented(1)]);
        }

        await using var connection = await dataSource.OpenConnectionAsync(Ct);
        Assert.Equal(1L, await new NpgsqlCommand($"SELECT value FROM {Schema}.position", connection).ExecuteScalarAsync(Ct));
    }
}

public sealed class SqlServerJsonTests(Databases databases) : JsonTests(databases, Db.SqlServer);

public abstract class JsonTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task A_connection_string_from_the_services_is_read_when_the_store_is_first_resolved()
    {
        var settings = new Dictionary<string, string> { ["db"] = "" };
        var services = new ServiceCollection().AddSingleton(settings);
        Func<IServiceProvider, string> connectionString = sp => sp.GetRequiredService<Dictionary<string, string>>()["db"];
        services.AddDeedbox(b => (Db == Db.Postgres ? b.UsePostgres(connectionString) : b.UseSqlServer(connectionString))
            .Schema(Schema).Stream<Counter>(s => s.Events<Incremented>()));

        settings["db"] = ConnectionString;
        await using var provider = services.BuildServiceProvider();
        await SchemaManager.Apply(provider.GetRequiredService<DeedboxRuntime>().Provider, Ct);
        await StoreFrom(provider).Append(NewStreamId(), ExpectedVersion.NoStream, [new Incremented(1)]);

        Assert.Equal(1L, await Scalar<long>($"SELECT value FROM {Table("position")}"));
    }

    [Fact]
    public async Task ConfigureJson_changes_how_events_and_state_are_stored()
    {
        var store = await Store(b => b
            .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper)
            .Stream<Counter>(s => s.Events<Incremented>()));
        var id = NewStreamId();

        await store.Append(id, ExpectedVersion.NoStream, [new Incremented(4)]);

        var payload = JsonDocument.Parse(await Scalar<string>($"SELECT payload FROM {Table("events")}"));
        var state = JsonDocument.Parse(await Scalar<string>($"SELECT state FROM {Table("streams")}"));
        Assert.Equal(4, payload.RootElement.GetProperty("BY").GetInt32());
        Assert.Equal(4, state.RootElement.GetProperty("TOTAL").GetInt64());
        Assert.Equal(new Counter(4, 1), (await store.Load<Counter>(id)).State);
    }

    [Fact]
    public async Task UseJsonContext_serializes_with_source_generated_contracts()
    {
        var store = await Store(b => b.UseJsonContext(CounterJsonContext.Default).Stream<Counter>(s => s.Events<Incremented>()));
        var id = NewStreamId();

        await store.Append(id, ExpectedVersion.NoStream, [new Incremented(2), new Incremented(3)]);
        await Execute($"UPDATE {Table("streams")} SET state = NULL");

        Assert.Equal(new Counter(5, 2), (await store.Load<Counter>(id)).State);
    }

    [Fact]
    public void A_context_without_a_registered_type_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => UseDatabase(b)
            .UseJsonContext(CounterJsonContext.Default)
            .Stream<Cart>(s => s.Events<ItemAdded>())));

        Assert.Equal("DBX011", error.Code);
        Assert.Contains("JsonSerializable(typeof(Cart))", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_versions_match_the_embedded_migrations()
    {
        Assert.Equal(PostgresSchema.LatestVersion, SqlServerSchema.LatestVersion);
        Assert.Contains($"-- Deedbox migration {PostgresSchema.LatestVersion}:", PostgresSchema.Script(), StringComparison.Ordinal);
        Assert.Contains($"-- Deedbox migration {SqlServerSchema.LatestVersion}:", SqlServerSchema.Script(), StringComparison.Ordinal);
    }

    private async Task Execute(string sql)
    {
        await using var connection = await OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Counter))]
[JsonSerializable(typeof(Incremented))]
internal sealed partial class CounterJsonContext : JsonSerializerContext;
