<!-- snippet: keys-database -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys.StoreInDatabase())
    .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
```
<!-- endSnippet -->
