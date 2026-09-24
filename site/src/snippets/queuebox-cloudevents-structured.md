<!-- snippet: queuebox-cloudevents-structured -->
```cs
// Structured mode: the payload is the whole CloudEvent.
q.Publish<CheckedOut>((e, p) => new QueueBoxMessage("cart.checked_out", new
{
    specversion = "1.0",
    id = p.EventId,
    source = "/shop/carts",
    type = p.EventType,
    subject = p.StreamId,
    time = p.OccurredAt,
    datacontenttype = "application/json",
    data = e,
})
{
    Headers = new Dictionary<string, string> { ["content-type"] = "application/cloudevents+json" },
});
```
<!-- endSnippet -->
