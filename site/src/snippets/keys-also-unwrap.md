<!-- snippet: keys-also-unwrap -->
```cs
// Step 3: the new key wraps. The database key stays for unwrap only, until the re-wrap is done.
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys
        .FromEnvironment("DEEDBOX_NEW_MASTER_KEY")
        .AlsoUnwrapWith(old => old.StoreInDatabase()))
    .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
```
<!-- endSnippet -->
