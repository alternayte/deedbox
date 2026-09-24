<!-- snippet: projection-ef -->
```cs
// EF Core flavour: your DbContext, enlisted in the append's transaction. Deedbox calls SaveChanges.
public sealed class CartSummaryProjection : Projection<ShopDb>
{
    public CartSummaryProjection()
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            var row = await ctx.Db.CartSummaries.FindAsync([ctx.StreamId], ctx.CancellationToken)
                      ?? ctx.Db.CartSummaries.Add(new CartSummaryRow(ctx.StreamId)).Entity;
            row.ItemCount += e.Qty;
        });

        On<CheckedOut>(async (_, ctx) =>
        {
            var row = await ctx.Db.CartSummaries.FindAsync([ctx.StreamId], ctx.CancellationToken);
            row!.CheckedOut = true;
        });

        On<StreamDeleted>(async (_, ctx) =>
            await ctx.Db.CartSummaries.Where(r => r.Id == ctx.StreamId).ExecuteDeleteAsync(ctx.CancellationToken));
    }

    // A rebuild calls ResetAsync, then replays every event.
    protected override Task ResetAsync(WriteContext<ShopDb> context) =>
        context.Db.CartSummaries.ExecuteDeleteAsync(context.CancellationToken);
}
```
<!-- endSnippet -->
