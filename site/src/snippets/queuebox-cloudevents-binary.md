<!-- snippet: queuebox-cloudevents-binary -->
```cs
// Binary mode: the attributes are headers, and the payload is the event.
q.Publish<CheckedOut>((e, p) => new QueueBoxMessage("cart.checked_out", e)
{
    Headers = new Dictionary<string, string>
    {
        ["ce-specversion"] = "1.0",
        ["ce-id"] = p.EventId.ToString(),
        ["ce-source"] = "/shop/carts",
        ["ce-type"] = p.EventType,
        ["ce-subject"] = p.StreamId,
        ["ce-time"] = p.OccurredAt.ToString("O"),
        ["content-type"] = "application/json",
    },
});
```
<!-- endSnippet -->
