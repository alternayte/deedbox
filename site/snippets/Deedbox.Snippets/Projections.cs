using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Shop;

public sealed class CartSummaryRow
{
    public CartSummaryRow(string id) => Id = id;

    public string Id { get; set; }

    public int ItemCount { get; set; }

    public bool CheckedOut { get; set; }
}

public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<CartSummaryRow> CartSummaries => Set<CartSummaryRow>();
}

// begin-snippet: projection-ef
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
// end-snippet

// begin-snippet: projection-ado
// ADO.NET or Dapper flavour: write through ctx.Connection and ctx.Transaction.
public sealed class CartTotals : Projection
{
    public CartTotals()
    {
        On<ItemAdded>((e, ctx) => Execute(ctx.Connection, ctx.Transaction,
            "UPDATE cart_totals SET items = items + @qty WHERE cart_id = @cart",
            ("qty", e.Qty), ("cart", ctx.StreamId)));
    }

    protected override Task ResetAsync(WriteContext context) =>
        Execute(context.Connection, context.Transaction, "DELETE FROM cart_totals");

    private static async Task Execute(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
// end-snippet

public interface IEmailSender
{
    Task SendReceipt(string cartId, Guid idempotencyKey, CancellationToken ct);
}

// begin-snippet: subscription
// A subscription does anything outside the database. Delivery is at least once,
// so pass the event ID on as an idempotency key.
public sealed class SendReceipt : Subscription
{
    // Subscriptions are created once; a scoped service comes from ctx.Services instead.
    public SendReceipt(IEmailSender email) =>
        On<CheckedOut>((_, ctx) => email.SendReceipt(ctx.Envelope.StreamId, ctx.Envelope.EventId, ctx.CancellationToken));
}
// end-snippet

// begin-snippet: batch-projection
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
// end-snippet
