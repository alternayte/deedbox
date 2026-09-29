<!-- snippet: pseudonyms-register -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Keys(keys => keys.FromEnvironment("DEEDBOX_MASTER_KEY"))
    .PseudonymPrefix("person:")  // the default; set it once
    .Stream<Session>(s => s.Events<CorrectionRecorded>()));
```
<!-- endSnippet -->
