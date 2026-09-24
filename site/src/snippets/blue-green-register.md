<!-- snippet: blue-green-register -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
    .Projection<CartSummaryProjection>("cart_summary", Run.Inline)        // serves reads until the switch
    .Projection<CartSummaryV2Projection>("cart_summary_v2", Run.Async));  // fills the new tables from the first event
```
<!-- endSnippet -->
