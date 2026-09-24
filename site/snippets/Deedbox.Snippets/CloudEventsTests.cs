using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Shop;

/// <summary>Runs the CloudEvents snippets of the "Wire QueueBox" guide and reads a valid CloudEvent back from the outbox.</summary>
public sealed class CloudEventsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Structured_mode_writes_the_whole_cloudevent_as_the_payload()
    {
        var (row, eventId) = await Publish("structured", Integrations.CloudEventsStructured);

        var cloudEvent = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(("1.0", eventId, "/shop/carts", "cart.checked_out", "cart-1"),
            (cloudEvent.GetProperty("specversion").GetString(), cloudEvent.GetProperty("id").GetGuid(), cloudEvent.GetProperty("source").GetString(),
             cloudEvent.GetProperty("type").GetString(), cloudEvent.GetProperty("subject").GetString()));
        Assert.Equal(DateTimeOffset.UnixEpoch, cloudEvent.GetProperty("data").GetProperty("at").GetDateTimeOffset());
        Assert.Equal("application/cloudevents+json", row.Headers["content-type"]);
    }

    [Fact]
    public async Task Binary_mode_writes_the_attributes_as_headers_and_the_event_as_the_payload()
    {
        var (row, eventId) = await Publish("binary", Integrations.CloudEventsBinary);

        Assert.Equal(("1.0", eventId.ToString(), "/shop/carts", "cart.checked_out", "cart-1"),
            (row.Headers["ce-specversion"], row.Headers["ce-id"], row.Headers["ce-source"], row.Headers["ce-type"], row.Headers["ce-subject"]));
        Assert.Equal(DateTimeOffset.UnixEpoch, JsonDocument.Parse(row.Payload).RootElement.GetProperty("at").GetDateTimeOffset());
        Assert.Equal("application/json", row.Headers["content-type"]);
    }

    private async Task<((string Payload, Dictionary<string, string> Headers) Row, Guid EventId)> Publish(string mode, Func<QueueBoxBuilder, QueueBoxBuilder> configure)
    {
        var ct = TestContext.Current.CancellationToken;
        var connStr = _postgres.GetConnectionString();
        var table = $"outbox_{mode}";
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        await using (var create = dataSource.CreateCommand(
            $"CREATE TABLE {table} (id uuid PRIMARY KEY, topic varchar(255) NOT NULL, key varchar(255), payload jsonb NOT NULL, " +
            "headers jsonb NOT NULL DEFAULT '{}', aggregate_type varchar(255))"))
            await create.ExecuteNonQueryAsync(ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Schema($"ce_{mode}")
            .ApplySchemaOnStartup()
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .UseQueueBox(q => configure(q.UseTable(table))));
        using var host = builder.Build();
        await host.StartAsync(ct);
        using var scope = host.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IEventStore>()
            .Append("cart-1", ExpectedVersion.NoStream, [new CheckedOut(DateTimeOffset.UnixEpoch)], ct);
        await host.StopAsync(ct);

        await using var read = dataSource.CreateCommand($"SELECT payload::text, headers::text FROM {table}");
        await using var reader = await read.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(1))!;
        return ((reader.GetString(0), headers), result.Events[0].EventId);
    }
}
