<!-- snippet: rename-alias -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s
        .Event<LineAdded>(e => e.Alias("cart.item_added")) // stored events keep their old name
        .Event<CheckedOut>()));
```
<!-- endSnippet -->
