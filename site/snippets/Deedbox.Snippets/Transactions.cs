using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Shop;

public sealed class BillingDb(DbContextOptions<BillingDb> options) : DbContext(options);

public static class Transactions
{
    public static async Task Dapper(IEventStore store, NpgsqlDataSource dataSource)
    {
        // begin-snippet: transaction-dapper
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Your own writes, with Dapper or plain ADO.NET, on the same connection and transaction...
        await store.UseTransaction(transaction).Append("cart-42", ExpectedVersion.Any, [new ItemAdded("apple", 1)]);

        await transaction.CommitAsync(); // Deedbox never commits your transaction.
        // end-snippet
    }

    public static async Task EfCore(IEventStore store, ShopDb shop, BillingDb billing)
    {
        // begin-snippet: transaction-efcore
        // Both contexts share one DbConnection. Deedbox enlists them, calls SaveChanges on each,
        // and commits everything at once. With no transaction open, it opens and commits one.
        shop.CartSummaries.Add(new CartSummaryRow("cart-42"));
        await store.UseDbContext(shop, billing).Execute<Cart>("cart-42", cart => CartDecider.Add(cart, "apple", 1));
        // end-snippet
    }

    public static async Task EfCoreOwnTransaction(IEventStore store, ShopDb shop)
    {
        // begin-snippet: transaction-efcore-own
        await using var transaction = await shop.Database.BeginTransactionAsync();
        await store.UseDbContext(shop).Append("cart-42", ExpectedVersion.Any, [new CheckedOut(DateTimeOffset.UtcNow)]);
        await transaction.CommitAsync(); // your transaction, your commit
        // end-snippet
    }
}
