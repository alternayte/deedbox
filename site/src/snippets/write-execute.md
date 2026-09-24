<!-- snippet: write-execute -->
```cs
// Load, decide, evolve and append in one transaction.
var result = await store.Execute<Cart>(cartId, cart => CartDecider.Add(cart, sku, qty));

// result.State is the new state; result.Version the new version; result.Events the appended envelopes.
```
<!-- endSnippet -->
