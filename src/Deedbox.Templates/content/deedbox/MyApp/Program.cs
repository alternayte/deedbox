using Deedbox;
#if (postgres)
using Npgsql;
#else
using Microsoft.Data.SqlClient;
#endif
using MyApp;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Deedbox")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Deedbox.");

builder.Services.AddDeedbox(DeedboxSetup.Configure(connectionString));

var app = builder.Build();

#if (postgres)
await using (var connection = new NpgsqlConnection(connectionString))
#else
await using (var connection = new SqlConnection(connectionString))
#endif
{
    await connection.OpenAsync();
    await CartSummary.CreateTable(connection);
}

app.MapPost("/carts/{id}/items", async (string id, AddItem request, IEventStore store) =>
{
    var result = await store.Execute<Cart>(id, cart => CartDecider.Add(cart, request.Sku, request.Qty));
    return Results.Ok(new { result.Version, result.State.Items });
});

app.MapPost("/carts/{id}/checkout", async (string id, IEventStore store) =>
{
    var result = await store.Execute<Cart>(id, cart => CartDecider.CheckOut(cart, DateTimeOffset.UtcNow));
    return Results.Ok(new { result.Version, result.State.IsCheckedOut });
});

app.MapGet("/carts/{id}", async (string id, IEventStore store) =>
{
    var (cart, version) = await store.Load<Cart>(id);
    return Results.Ok(new { version, cart.Items, cart.IsCheckedOut });
});

app.Run();

public record AddItem(string Sku, int Qty);

namespace MyApp
{
    /// <summary>The Deedbox configuration, shared by the app and the event-contract test.</summary>
    public static class DeedboxSetup
    {
        public static Action<DeedboxBuilder> Configure(string connectionString) => es => es
#if (postgres)
            .UsePostgres(connectionString)
#else
            .UseSqlServer(connectionString)
#endif
            .ApplySchemaOnStartup()
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Projection<CartSummary>("cart_summary", Run.Inline);
    }
}
