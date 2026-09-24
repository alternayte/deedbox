<!-- snippet: register-postgres -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .ApplySchemaOnStartup()
    .Stream<Cart>(s => s                   // stream type "cart"
        .Events<ItemAdded, CheckedOut>())); // cart.item_added, cart.checked_out
```
<!-- endSnippet -->
