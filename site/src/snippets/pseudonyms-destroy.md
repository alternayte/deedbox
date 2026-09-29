<!-- snippet: pseudonyms-destroy -->
```cs
// The app no longer writes in 2026-Q1. Nobody can link its subject IDs to a person again.
var destroyed = await admin.DestroyPseudonymPeriodAsync("2026-Q1", tenantId: "acme");
```
<!-- endSnippet -->
