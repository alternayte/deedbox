<!-- snippet: write-explicit -->
```cs
var (cart, version) = await store.Load<Cart>(cartId);
var events = CartDecider.CheckOut(cart, now).ToList();
if (events.Count > 0)
    await store.Append(cartId, ExpectedVersion.Exact(version), events);
```
<!-- endSnippet -->
