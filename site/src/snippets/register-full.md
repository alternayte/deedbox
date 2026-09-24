<!-- snippet: register-full -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
    .Projection<CartSummaryProjection>("cart_summary", Run.Inline)
    .Projection<CartTotals>("cart_totals", Run.Async)
    .Subscription<SendReceipt>("receipt_email"));
```
<!-- endSnippet -->
