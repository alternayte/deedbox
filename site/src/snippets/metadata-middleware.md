<!-- snippet: metadata-middleware -->
```cs
// Set the tenant and metadata once per request; every store in the request scope uses them.
app.Use(async (http, next) =>
{
    var deedbox = http.RequestServices.GetRequiredService<DeedboxContext>();
    deedbox.TenantId = http.Request.Headers["X-Tenant"].ToString();
    deedbox.Metadata = new EventMetadata
    {
        CorrelationId = http.TraceIdentifier,
        Actor = http.User.Identity?.Name is { } user ? $"user:{user}" : null,
    };
    await next(http);
});
```
<!-- endSnippet -->
