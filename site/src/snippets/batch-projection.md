<!-- snippet: batch-projection -->
```cs
// A batch projection receives each batch of its events in one call, for bulk writes. It runs async only.
public sealed class CartArchive : BatchProjection
{
    public CartArchive() => Handles<CheckedOut>();

    protected override Task ApplyAsync(IReadOnlyList<EventEnvelope> events, WriteContext context)
    {
        // One bulk insert for the whole batch, through context.Connection and context.Transaction.
        return Task.CompletedTask;
    }
}
```
<!-- endSnippet -->
