<!-- snippet: queuebox-new-contract -->
```cs
// A breaking change goes to a new topic. From this release, nothing in the app writes the old topic.
q.Publish<ItemPriced>((e, p) => new QueueBoxMessage("cart.item_added.v2", new
{
    sku = e.Sku,
    quantity = e.Qty,
    unitPrice = new { amount = e.Price, currency = "EUR" },
}));
```
<!-- endSnippet -->
