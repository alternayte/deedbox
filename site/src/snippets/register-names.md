<!-- snippet: register-names -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>("shopping_cart", s => s                   // stored as "shopping_cart"
        .Event<ItemAdded>(name: "shopping_cart.line_added") // an explicit event name
        .Event<CheckedOut>()));                             // shopping_cart.checked_out
```
<!-- endSnippet -->
