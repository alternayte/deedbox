<!-- snippet: admin-api -->
```cs
var status = await admin.GetStatusAsync();
foreach (var consumer in status.Consumers)
    Console.WriteLine($"{consumer.Name}: {consumer.Status}, {consumer.Lag} behind");

var rebuild = await admin.RebuildAsync("cart_summary");
var skip = await admin.SkipAsync("cart_totals", stalledEventId);
var job = await admin.GetJobAsync(rebuild);
```
<!-- endSnippet -->
