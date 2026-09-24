<!-- snippet: upcast-typed -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s
        .Event<ItemPriced>(version: 3, up => up
            .Name("cart.item_added")
            .From(1, json => json["qty"] ??= 1)
            .Upcast<ItemAddedV2, ItemPriced>(old => new ItemPriced(old.Sku, old.Qty, 0m)))
        .Event<CheckedOut>()));
```
<!-- endSnippet -->
