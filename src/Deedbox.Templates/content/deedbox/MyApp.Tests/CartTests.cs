using Deedbox.Testing;
using Xunit;

namespace MyApp.Tests;

public class CartTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cart_with_items_checks_out() =>
        Decider.Given<Cart>(new ItemAdded("apple", 2))
            .When(cart => CartDecider.CheckOut(cart, Now))
            .Then(new CheckedOut(Now));

    [Fact]
    public void A_checked_out_cart_takes_no_items() =>
        Decider.Given<Cart>(new ItemAdded("apple", 2), new CheckedOut(Now))
            .When(cart => CartDecider.Add(cart, "pear", 1))
            .ThenThrows<InvalidOperationException>();
}

/// <summary>Fails when an event change would break stored events. Commit events.lock.</summary>
public class EventContractTests
{
    [Fact]
    public void Event_contracts_are_stable() =>
        EventContracts.Verify(DeedboxSetup.Configure("unused"), "events.lock");
}
