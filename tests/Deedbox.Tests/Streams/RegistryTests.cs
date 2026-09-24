using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Streams;

public sealed class RegistryTests
{
    [Fact]
    public void Names_follow_the_convention_unless_overridden()
    {
        var registry = Build(b => b
            .Stream<Cart>(s => s.Events<ItemAdded>().Event<CheckedOut>("cart.done"))
            .Stream<Order>("purchase", s => s.Events<OrderPlaced>()));

        Assert.Equal("cart", registry.ForState(typeof(Cart)).Name);
        Assert.Equal("cart.item_added", registry.ForEvent(typeof(ItemAdded)).Name);
        Assert.Equal("cart.done", registry.ForEvent(typeof(CheckedOut)).Name);
        Assert.Equal("purchase", registry.ForState(typeof(Order)).Name);
        Assert.Equal("purchase.order_placed", registry.ForEvent(typeof(OrderPlaced)).Name);
    }

    [Fact]
    public void An_event_registered_twice_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => Build(b => b
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Stream<Order>(s => s.Events<ItemAdded>())));

        Assert.Equal("DBX005", error.Code);
        Assert.Contains("ItemAdded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_events_with_one_name_fail_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => Build(b => b
            .Stream<Cart>(s => s.Event<ItemAdded>("cart.x").Event<CheckedOut>("cart.x"))));

        Assert.Equal("DBX005", error.Code);
    }

    [Fact]
    public void A_state_or_stream_name_registered_twice_fails_at_startup()
    {
        var sameState = Assert.Throws<DeedboxException>(() => Build(b => b
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Stream<Cart>("cart2", s => s.Events<CheckedOut>())));
        var sameName = Assert.Throws<DeedboxException>(() => Build(b => b
            .Stream<Cart>(s => s.Events<ItemAdded>())
            .Stream<Order>("cart", s => s.Events<OrderPlaced>())));

        Assert.Equal(("DBX004", "DBX004"), (sameState.Code, sameName.Code));
    }

    [Fact]
    public void A_missing_provider_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => new ServiceCollection().AddDeedbox(b => DefaultStreams(b)));

        Assert.Equal("DBX002", error.Code);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    public void Invalid_names_fail_at_startup(string name)
    {
        var error = Assert.Throws<DeedboxException>(() => Build(b => b.Stream<Cart>(name, s => s.Events<ItemAdded>())));

        Assert.Equal("DBX015", error.Code);
    }

    [Fact]
    public void Deterministic_stream_ids_are_rfc_uuid_v5()
    {
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        var anthology = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Equal("2ed6657d-e927-568b-95e1-2665a8aea6a2", StreamId.Deterministic(dns, "www.example.com"));
        Assert.Equal("82c8f0f6-4a60-5208-a3a4-f3abaeff0794",
            StreamId.Deterministic(anthology, "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"));
    }

    private static void DefaultStreams(DeedboxBuilder b) => b.Stream<Cart>(s => s.Events<ItemAdded>());

    private static EventRegistry Build(Action<DeedboxBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddDeedbox(b => configure(b.UsePostgres("Host=unused")));
        return ((DeedboxRuntime)services.Single(d => d.ServiceType == typeof(DeedboxRuntime)).ImplementationFactory!(null!)).Registry;
    }
}
