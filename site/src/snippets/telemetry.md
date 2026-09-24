<!-- snippet: telemetry -->
```cs
// With OpenTelemetry: subscribe to the "Deedbox" ActivitySource and Meter.
//   .WithTracing(t => t.AddSource("Deedbox"))
//   .WithMetrics(m => m.AddMeter("Deedbox"))
const string SourceAndMeter = "Deedbox";
```
<!-- endSnippet -->
