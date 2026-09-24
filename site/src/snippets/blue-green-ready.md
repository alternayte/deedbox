<!-- snippet: blue-green-ready -->
```cs
var status = await admin.GetStatusAsync();
var next = status.Consumers.Single(c => c.Name == "cart_summary_v2");
var ready = next.Status == "running" && next.Lag == 0;  // caught up with every event
```
<!-- endSnippet -->
