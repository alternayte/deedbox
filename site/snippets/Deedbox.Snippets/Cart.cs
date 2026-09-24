using System.Collections.Immutable;

namespace Shop;

// begin-snippet: cart-events
// Events: plain records. No marker interface, no base class.
public record ItemAdded(string Sku, int Qty);

public record CheckedOut(DateTimeOffset At);
// end-snippet

// begin-snippet: cart-state
// State: Initial and Evolve. Nothing else.
public record Cart(ImmutableDictionary<string, int> Items, bool IsCheckedOut) : IState<Cart>
{
    public static Cart Initial { get; } = new(ImmutableDictionary<string, int>.Empty, false);

    public static Cart Evolve(Cart s, object e) => e switch
    {
        ItemAdded x => s with { Items = s.Items.SetItem(x.Sku, s.Items.GetValueOrDefault(x.Sku) + x.Qty) },
        CheckedOut => s with { IsCheckedOut = true },
        _ => s,
    };
}
// end-snippet

// begin-snippet: cart-decider
// Decisions: pure functions from state to new events.
public static class CartDecider
{
    public static IEnumerable<object> Add(Cart cart, string sku, int qty) =>
        cart.IsCheckedOut
            ? throw new InvalidOperationException("The cart is checked out.")
            : [new ItemAdded(sku, qty)];

    public static IEnumerable<object> CheckOut(Cart cart, DateTimeOffset now) =>
        cart.IsCheckedOut || cart.Items.IsEmpty ? [] : [new CheckedOut(now)];
}
// end-snippet
