using System.Collections.Immutable;

namespace Deedbox.Tests.Infrastructure;

public record ItemAdded(string Sku, int Qty);

public record CheckedOut(DateTimeOffset At);

public record Cart(ImmutableDictionary<string, int> Items, bool IsCheckedOut, int Changes) : IState<Cart>
{
    public static Cart Initial { get; } = new(ImmutableDictionary<string, int>.Empty, false, 0);

    public static Cart Evolve(Cart s, object e) => e switch
    {
        ItemAdded x => s with { Items = s.Items.SetItem(x.Sku, s.Items.GetValueOrDefault(x.Sku) + x.Qty), Changes = s.Changes + 1 },
        CheckedOut => s with { IsCheckedOut = true, Changes = s.Changes + 1 },
        _ => s,
    };
}

public record OrderPlaced(string Customer);

public record Order(string? Customer) : IState<Order>
{
    public static Order Initial { get; } = new((string?)null);

    public static Order Evolve(Order s, object e) => e is OrderPlaced p ? new Order(p.Customer) : s;
}

/// <summary>A counter whose state records every event it saw, so a lost or repeated event shows.</summary>
public record Incremented(int By);

public record Counter(long Total, int Count) : IState<Counter>
{
    public static Counter Initial { get; } = new(0, 0);

    public static Counter Evolve(Counter s, object e) => e is Incremented i ? new Counter(s.Total + i.By, s.Count + 1) : s;
}
