<!-- snippet: transaction-efcore -->
```cs
// Both contexts share one DbConnection. Deedbox enlists them, calls SaveChanges on each,
// and commits everything at once. With no transaction open, it opens and commits one.
shop.CartSummaries.Add(new CartSummaryRow("cart-42"));
await store.UseDbContext(shop, billing).Execute<Cart>("cart-42", cart => CartDecider.Add(cart, "apple", 1));
```
<!-- endSnippet -->
