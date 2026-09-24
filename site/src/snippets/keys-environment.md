<!-- snippet: keys-environment -->
```cs
// DEEDBOX_MASTER_KEY holds a key ring: v2:<base64 of 32 random bytes>,v1:<older key>
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys
        .FromEnvironment("DEEDBOX_MASTER_KEY")
        .RedactWith("[erased]"))
    .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
```
<!-- endSnippet -->
