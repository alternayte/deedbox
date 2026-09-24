using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Shop;

public static class Registration
{
    public static void Postgres(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-postgres
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .ApplySchemaOnStartup()
            .Stream<Cart>(s => s                   // stream type "cart"
                .Events<ItemAdded, CheckedOut>())); // cart.item_added, cart.checked_out
        // end-snippet
    }

    public static void SqlServer(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-sqlserver
        builder.Services.AddDeedbox(es => es
            .UseSqlServer(connStr)
            .ApplySchemaOnStartup()
            .Stream<Cart>(s => s
                .Events<ItemAdded, CheckedOut>()));
        // end-snippet
    }

    public static void Full(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-full
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Projection<CartSummaryProjection>("cart_summary", Run.Inline)
            .Projection<CartTotals>("cart_totals", Run.Async)
            .Subscription<SendReceipt>("receipt_email"));
        // end-snippet
    }

    public static void Names(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-names
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>("shopping_cart", s => s                   // stored as "shopping_cart"
                .Event<ItemAdded>(name: "shopping_cart.line_added") // an explicit event name
                .Event<CheckedOut>()));                             // shopping_cart.checked_out
        // end-snippet
    }

    public static void Snapshots(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-snapshots
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s
                .Events<ItemAdded, CheckedOut>()
                .StateVersion(2)                         // raise it when you change the Cart record
                .Snapshots(SnapshotPolicy.Every(50))));  // or EveryAppend (default) or Never
        // end-snippet
    }

    public static void Runner(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: register-runner
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Runner(r =>
            {
                r.BatchSize = 500;
                r.MaxPollDelay = TimeSpan.FromSeconds(5);
                r.HandlerRetries = 5;
                r.StallAfter = TimeSpan.FromMinutes(10);
            }));
        // end-snippet
    }
}
