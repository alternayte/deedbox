<!-- snippet: upcast-json -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s
        .Event<ItemAdded>(version: 2, up => up
            .From(1, json => json["qty"] ??= 1))  // version 1 had no quantity
        .Event<CheckedOut>()));
```
<!-- endSnippet -->
