<!-- snippet: write-conflict -->
```cs
try
{
    await store.Append(cartId, ExpectedVersion.NoStream, [new ItemAdded("apple", 1)]);
}
catch (ConcurrencyException ex)
{
    // ex.Expected is NoStream; ex.Actual is the version the stream is at.
    Console.WriteLine($"Cart {ex.StreamId} already exists at version {ex.Actual}.");
}
```
<!-- endSnippet -->
