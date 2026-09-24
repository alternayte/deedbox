<!-- snippet: transaction-dapper -->
```cs
await using var connection = await dataSource.OpenConnectionAsync();
await using var transaction = await connection.BeginTransactionAsync();

// Your own writes, with Dapper or plain ADO.NET, on the same connection and transaction...
await store.UseTransaction(transaction).Append("cart-42", ExpectedVersion.Any, [new ItemAdded("apple", 1)]);

await transaction.CommitAsync(); // Deedbox never commits your transaction.
```
<!-- endSnippet -->
