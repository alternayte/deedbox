using Deedbox.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Evolution;

public sealed class RegistrationTests
{
    [Fact]
    public void A_version_without_an_upcaster_for_every_step_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => Register(s => s.Event<ItemAdded>(version: 3, up => up.From(1, _ => { }))));

        Assert.Equal("DBX018", error.Code);
        Assert.Contains("no upcaster from version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_upcaster_must_be_the_last_step()
    {
        var error = Assert.Throws<DeedboxException>(() => Register(s => s.Event<ItemAdded>(version: 2, up => up
            .Upcast<ItemAddedV2, ItemAdded>(o => new ItemAdded(o.Sku, o.Qty))
            .From(1, _ => { }))));

        Assert.Equal("DBX018", error.Code);
    }

    [Fact]
    public void An_upcaster_beyond_the_current_version_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => Register(s => s.Event<ItemAdded>(version: 2, up => up.From(1, _ => { }).From(2, _ => { }))));

        Assert.Equal("DBX018", error.Code);
    }

    [Fact]
    public void An_alias_that_collides_with_another_name_fails_at_startup()
    {
        var error = Assert.Throws<DeedboxException>(() => Register(s => s
            .Events<CheckedOut>()
            .Event<ItemAdded>(e => e.Alias("cart.checked_out"))));

        Assert.Equal("DBX005", error.Code);
    }

    [Fact]
    public void A_version_1_event_needs_no_upcasters()
    {
        Register(s => s.Event<ItemAdded>(e => e.Name("cart.added").Alias("cart.old_added")));
    }

    private static void Register(Action<StreamBuilder<Cart>> events) =>
        new ServiceCollection().AddDeedbox(b => b.UsePostgres("Host=unused").Stream(events));
}
