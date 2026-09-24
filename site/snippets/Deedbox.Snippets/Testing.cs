using Deedbox.Testing;

namespace Shop;

// begin-snippet: decider-tests
public class CartDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cart_with_items_checks_out() =>
        Decider.Given<Cart>(new ItemAdded("apple", 2))
            .When(cart => CartDecider.CheckOut(cart, Now))
            .Then(new CheckedOut(Now));

    [Fact]
    public void An_empty_cart_does_not_check_out() =>
        Decider.Given<Cart>()
            .When(cart => CartDecider.CheckOut(cart, Now))
            .ThenNothing();

    [Fact]
    public void A_checked_out_cart_takes_no_items() =>
        Decider.Given<Cart>(new ItemAdded("apple", 2), new CheckedOut(Now))
            .When(cart => CartDecider.Add(cart, "pear", 1))
            .ThenThrows<InvalidOperationException>();
}
// end-snippet

// begin-snippet: lockfile-test
public class EventContractTests
{
    [Fact]
    public void Event_contracts_are_stable() =>
        EventContracts.Verify(es => es.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>()), "events.lock");
}
// end-snippet
