using System.Diagnostics;
using System.Text.Json;
using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Projections;

public sealed class PostgresMetadataTests(Databases databases) : MetadataTests(databases, Db.Postgres);

public sealed class SqlServerMetadataTests(Databases databases) : MetadataTests(databases, Db.SqlServer);

public abstract class MetadataTests(Databases databases, Db db) : StoreTest(databases, db)
{
    [Fact]
    public async Task The_scope_metadata_is_stored_with_every_event_and_returned_in_envelopes()
    {
        var services = await Services();
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DeedboxContext>();
        context.Metadata = new EventMetadata { CorrelationId = "req-1", Actor = "user:7", Headers = new Dictionary<string, string> { ["source"] = "api" } };
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var result = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);

        Assert.All(result.Events, e => Assert.Equal(context.Metadata, e.Metadata));
        var stored = JsonDocument.Parse(await Scalar<string>($"SELECT metadata FROM {Table("events")} WHERE global_position = 2")).RootElement;
        Assert.Equal("req-1", stored.GetProperty("correlationId").GetString());
        Assert.Equal("user:7", stored.GetProperty("actor").GetString());
        Assert.Equal("api", stored.GetProperty("headers").GetProperty("source").GetString());
        Assert.False(stored.TryGetProperty("causationId", out _));
    }

    [Fact]
    public async Task WithMetadata_overrides_the_scope_metadata_for_its_appends()
    {
        var services = await Services();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DeedboxContext>().Metadata = new EventMetadata { CorrelationId = "req-1", Actor = "user:7" };
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var result = await store
            .WithMetadata(m => m with { Actor = "system:import" })
            .WithMetadata(m => m with { Headers = new Dictionary<string, string> { ["batch"] = "9" } })
            .Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        var metadata = result.Events[0].Metadata;
        Assert.Equal(("req-1", "system:import", "9"), (metadata.CorrelationId, metadata.Actor, metadata.Headers["batch"]));
    }

    [Fact]
    public async Task The_current_trace_context_is_captured()
    {
        var store = await Store();
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();

        var result = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        Assert.Equal(activity.Id, result.Events[0].Metadata.TraceParent);
        Assert.StartsWith("00-" + activity.TraceId, result.Events[0].Metadata.TraceParent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CausedBy_carries_tenant_correlation_and_causation_from_a_handled_event()
    {
        var services = await Services();
        using var first = services.CreateScope();
        var firstContext = first.ServiceProvider.GetRequiredService<DeedboxContext>();
        firstContext.TenantId = "acme";
        firstContext.Metadata = new EventMetadata { CorrelationId = "req-9", Actor = "user:1" };
        var cause = (await first.ServiceProvider.GetRequiredService<IEventStore>().Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1)])).Events[0];

        using var second = services.CreateScope();
        second.ServiceProvider.GetRequiredService<DeedboxContext>().CausedBy(cause);
        var effect = (await second.ServiceProvider.GetRequiredService<IEventStore>().Append("order-1", ExpectedVersion.NoStream, [new OrderPlaced("x")])).Events[0];

        Assert.Equal(("acme", "req-9", cause.EventId.ToString("D"), "user:1"),
            (effect.TenantId, effect.Metadata.CorrelationId, effect.Metadata.CausationId, effect.Metadata.Actor));
    }

    [Fact]
    public async Task Events_without_metadata_store_an_empty_object()
    {
        var store = await Store();

        var result = await store.Append(NewStreamId(), ExpectedVersion.NoStream, [new ItemAdded("a", 1)]);

        Assert.Equal(EventMetadata.Empty, result.Events[0].Metadata);
        Assert.Empty(JsonDocument.Parse(await Scalar<string>($"SELECT metadata FROM {Table("events")}")).RootElement.EnumerateObject());
    }
}
