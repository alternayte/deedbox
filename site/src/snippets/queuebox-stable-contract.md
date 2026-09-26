<!-- snippet: queuebox-stable-contract -->
```cs
// cart.item_added is at version 3 in the store, and upcasting gives the callback that shape for old events too.
// The message keeps the fields consumers already read, and adds price as a new field they can ignore.
q.Publish<ItemPriced>((e, p) => new QueueBoxMessage("cart.item_added", new { sku = e.Sku, qty = e.Qty, price = e.Price }));
```
<!-- endSnippet -->
