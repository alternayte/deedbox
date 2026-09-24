using Microsoft.Extensions.DependencyInjection;

namespace Shop;

// begin-snippet: evolution-records
public record LineAdded(string Sku, int Qty);             // was called ItemAdded

public record ItemAddedV2(string Sku, int Qty);           // the old shape, kept for the typed upcaster

public record ItemPriced(string Sku, int Qty, decimal Price);
// end-snippet

public static class Evolution
{
    public static void Rename(IServiceCollection services, string connStr)
    {
        // begin-snippet: rename-alias
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s
                .Event<LineAdded>(e => e.Alias("cart.item_added")) // stored events keep their old name
                .Event<CheckedOut>()));
        // end-snippet
    }

    public static void JsonUpcaster(IServiceCollection services, string connStr)
    {
        // begin-snippet: upcast-json
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s
                .Event<ItemAdded>(version: 2, up => up
                    .From(1, json => json["qty"] ??= 1))  // version 1 had no quantity
                .Event<CheckedOut>()));
        // end-snippet
    }

    public static void TypedUpcaster(IServiceCollection services, string connStr)
    {
        // begin-snippet: upcast-typed
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Cart>(s => s
                .Event<ItemPriced>(version: 3, up => up
                    .Name("cart.item_added")
                    .From(1, json => json["qty"] ??= 1)
                    .Upcast<ItemAddedV2, ItemPriced>(old => new ItemPriced(old.Sku, old.Qty, 0m)))
                .Event<CheckedOut>()));
        // end-snippet
    }
}
