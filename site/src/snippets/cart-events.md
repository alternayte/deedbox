<!-- snippet: cart-events -->
```cs
// Events: plain records. No marker interface, no base class.
public record ItemAdded(string Sku, int Qty);

public record CheckedOut(DateTimeOffset At);
```
<!-- endSnippet -->
