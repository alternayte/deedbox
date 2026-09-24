<!-- snippet: register-runner -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
    .Runner(r =>
    {
        r.BatchSize = 500;
        r.MaxPollDelay = TimeSpan.FromSeconds(5);
        r.HandlerRetries = 5;
        r.StallAfter = TimeSpan.FromMinutes(10);
    }));
```
<!-- endSnippet -->
