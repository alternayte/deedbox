using Microsoft.Extensions.DependencyInjection;

namespace Shop;

/// <summary>The new shape of the cart summary, in new tables.</summary>
public sealed class CartSummaryV2Projection : Projection
{
    public CartSummaryV2Projection()
    {
        On<ItemAdded>(async (e, ctx) =>
        {
            await using var command = ctx.Connection.CreateCommand();
            command.Transaction = ctx.Transaction;
            command.CommandText = "INSERT INTO cart_summary_v2 (cart_id, items) VALUES (@cart, @qty) " +
                                  "ON CONFLICT (cart_id) DO UPDATE SET items = cart_summary_v2.items + @qty";
            foreach (var (name, value) in new (string, object)[] { ("cart", ctx.StreamId), ("qty", e.Qty) })
            {
                var parameter = command.CreateParameter();
                (parameter.ParameterName, parameter.Value) = (name, value);
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync(ctx.CancellationToken);
        });
    }

    protected override async Task ResetAsync(WriteContext context)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = "DELETE FROM cart_summary_v2";
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}

public static class BlueGreen
{
    public static void Register(IServiceCollection services, string connStr)
    {
        // begin-snippet: blue-green-register
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Projection<CartSummaryProjection>("cart_summary", Run.Inline)        // serves reads until the switch
            .Projection<CartSummaryV2Projection>("cart_summary_v2", Run.Async));  // fills the new tables from the first event
        // end-snippet
    }

    public static async Task<bool> Ready(IEventStoreAdmin admin)
    {
        // begin-snippet: blue-green-ready
        var status = await admin.GetStatusAsync();
        var next = status.Consumers.Single(c => c.Name == "cart_summary_v2");
        var ready = next.Status == "running" && next.Lag == 0;  // caught up with every event
        // end-snippet
        return ready;
    }
}
