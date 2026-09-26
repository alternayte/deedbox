<!-- snippet: external-call-before -->
```cs
// Call the service first. If it throws, Execute does not run and nothing is written.
var quote = await pricing.QuoteAsync(skus, ct);

// The decision gets the quote as a value, so it stays pure and safe to run again.
await store.Execute<Order>(orderId, order => OrderDecider.Place(order, quote), ct);
```
<!-- endSnippet -->
