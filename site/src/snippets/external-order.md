<!-- snippet: external-order -->
```cs
public record OrderPlaced(decimal Total);

public record PaymentRequested(decimal Amount);

public record PaymentCaptured(string ChargeId);

public record PaymentDeclined(string Reason);

public record Order(decimal Total, string Status) : IState<Order>
{
    public static Order Initial { get; } = new(0m, "new");

    public static Order Evolve(Order s, object e) => e switch
    {
        OrderPlaced x => s with { Total = x.Total, Status = "awaiting_payment" },
        PaymentCaptured => s with { Status = "paid" },
        PaymentDeclined => s with { Status = "declined" },
        _ => s,
    };
}

public static class OrderDecider
{
    public static IEnumerable<object> Place(Order order, Quote quote) =>
        order.Status != "new"
            ? throw new InvalidOperationException("The order is already placed.")
            : [new OrderPlaced(quote.Total), new PaymentRequested(quote.Total)];

    // A redelivered event finds the payment already recorded, and records nothing.
    public static IEnumerable<object> RecordPayment(Order order, ChargeResult result) =>
        order.Status != "awaiting_payment" ? []
        : result.ChargeId is { } id ? [new PaymentCaptured(id)]
        : [new PaymentDeclined(result.DeclineReason ?? "declined")];
}
```
<!-- endSnippet -->
