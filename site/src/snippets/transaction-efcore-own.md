<!-- snippet: transaction-efcore-own -->
```cs
await using var transaction = await shop.Database.BeginTransactionAsync();
await store.UseDbContext(shop).Append("cart-42", ExpectedVersion.Any, [new CheckedOut(DateTimeOffset.UtcNow)]);
await transaction.CommitAsync(); // your transaction, your commit
```
<!-- endSnippet -->
