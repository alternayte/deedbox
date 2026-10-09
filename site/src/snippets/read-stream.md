<!-- snippet: read-stream -->
```cs
// One page at a time; the last version of a page is the cursor for the next one.
long after = 0;
while (await store.ReadStream(cartId, afterVersion: after, limit: 100) is { Count: > 0 } page)
{
    foreach (var e in page)
        Console.WriteLine($"{e.Version} {e.EventType} at {e.OccurredAt:O} by {e.Metadata.Actor}");
    after = page[^1].Version;
}
```
<!-- endSnippet -->
