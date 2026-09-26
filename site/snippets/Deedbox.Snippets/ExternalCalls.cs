using Microsoft.Extensions.DependencyInjection;

namespace Shop;

// begin-snippet: external-order
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
// end-snippet

public record Quote(decimal Total);

public record ChargeResult(string? ChargeId, string? DeclineReason);

public interface IPricing
{
    Task<Quote> QuoteAsync(IReadOnlyList<string> skus, CancellationToken ct);
}

public interface IPayments
{
    /// <summary>Returns a declined result for a declined card; throws for a timeout or an outage.</summary>
    Task<ChargeResult> ChargeAsync(decimal amount, string idempotencyKey, CancellationToken ct);
}

public static class ExternalCalls
{
    public static async Task PlaceOrder(IEventStore store, IPricing pricing, string orderId, IReadOnlyList<string> skus, CancellationToken ct)
    {
        // begin-snippet: external-call-before
        // Call the service first. If it throws, Execute does not run and nothing is written.
        var quote = await pricing.QuoteAsync(skus, ct);

        // The decision gets the quote as a value, so it stays pure and safe to run again.
        await store.Execute<Order>(orderId, order => OrderDecider.Place(order, quote), ct);
        // end-snippet
    }

    public static void Register(IServiceCollection services, string connStr)
    {
        // begin-snippet: external-call-register
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Stream<Order>(s => s.Events<OrderPlaced, PaymentRequested, PaymentCaptured, PaymentDeclined>())
            .Subscription<ChargePayment>("charge-payment"));
        // end-snippet
    }
}

// begin-snippet: external-call-after
public sealed class ChargePayment : Subscription
{
    public ChargePayment(IPayments payments) =>
        On<PaymentRequested>(async (e, ctx) =>
        {
            // The event ID is the idempotency key, so a retried event does not charge twice.
            var result = await payments.ChargeAsync(e.Amount, ctx.Envelope.EventId.ToString(), ctx.CancellationToken);

            // A declined card is a result, so the order records it. A timeout throws, and the event is retried.
            var store = ctx.Services.GetRequiredService<IEventStore>();
            await store.Execute<Order>(ctx.Envelope.StreamId, order => OrderDecider.RecordPayment(order, result), ctx.CancellationToken);
        });
}
// end-snippet
