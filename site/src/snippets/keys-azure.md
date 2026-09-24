<!-- snippet: keys-azure -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys.UseAzureKeyVault(
        new Uri("https://my-vault.vault.azure.net/keys/deedbox"),
        new DefaultAzureCredential()))
    .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
```
<!-- endSnippet -->
