<!-- snippet: health-endpoints -->
```cs
// The probes: the process answers. They leave the Deedbox check out.
app.MapHealthChecks("/healthz", new() { Predicate = check => check.Name != "deedbox" });

// For alerts: unhealthy while a projection or subscription is stalled or stuck.
app.MapHealthChecks("/health/deedbox", new() { Predicate = check => check.Name == "deedbox" });
```
<!-- endSnippet -->
