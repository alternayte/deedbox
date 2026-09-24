<!-- snippet: register-sqlserver -->
```cs
builder.Services.AddDeedbox(es => es
    .UseSqlServer(connStr)
    .ApplySchemaOnStartup()
    .Stream<Cart>(s => s
        .Events<ItemAdded, CheckedOut>()));
```
<!-- endSnippet -->
