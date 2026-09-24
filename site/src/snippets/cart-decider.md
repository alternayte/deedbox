<!-- snippet: cart-decider -->
```cs
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
```
<!-- endSnippet -->
