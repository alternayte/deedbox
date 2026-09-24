<!-- snippet: register-snapshots -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s
        .Events<ItemAdded, CheckedOut>()
        .StateVersion(2)                         // raise it when you change the Cart record
        .Snapshots(SnapshotPolicy.Every(50))));  // or EveryAppend (default) or Never
```
<!-- endSnippet -->
