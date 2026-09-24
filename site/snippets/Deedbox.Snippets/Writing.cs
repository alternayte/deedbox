namespace Shop;

public static class Writing
{
    public static async Task<object> Execute(IEventStore store, string cartId, string sku, int qty)
    {
        // begin-snippet: write-execute
        // Load, decide, evolve and append in one transaction.
        var result = await store.Execute<Cart>(cartId, cart => CartDecider.Add(cart, sku, qty));

        // result.State is the new state; result.Version the new version; result.Events the appended envelopes.
        // end-snippet
        return result;
    }

    public static async Task Explicit(IEventStore store, string cartId, DateTimeOffset now)
    {
        // begin-snippet: write-explicit
        var (cart, version) = await store.Load<Cart>(cartId);
        var events = CartDecider.CheckOut(cart, now).ToList();
        if (events.Count > 0)
            await store.Append(cartId, ExpectedVersion.Exact(version), events);
        // end-snippet
    }

    public static async Task Conflict(IEventStore store, string cartId)
    {
        // begin-snippet: write-conflict
        try
        {
            await store.Append(cartId, ExpectedVersion.NoStream, [new ItemAdded("apple", 1)]);
        }
        catch (ConcurrencyException ex)
        {
            // ex.Expected is NoStream; ex.Actual is the version the stream is at.
            Console.WriteLine($"Cart {ex.StreamId} already exists at version {ex.Actual}.");
        }
        // end-snippet
    }

    public static void Ids(Guid userId, Guid titleId)
    {
        // begin-snippet: stream-ids
        var fromGuid = StreamId.From(Guid.NewGuid());   // "0f8fad5b-d9cb-469f-a165-70867728950e"

        var ns = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var forPair = StreamId.Deterministic(ns, userId.ToString(), titleId.ToString()); // the same pair gives the same ID
        // end-snippet
        _ = (fromGuid, forPair);
    }

    public static async Task Delete(IEventStore store)
    {
        // begin-snippet: delete-stream
        await store.DeleteStream("cart-42");
        // end-snippet
    }
}
