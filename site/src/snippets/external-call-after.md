<!-- snippet: external-call-after -->
```cs
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
```
<!-- endSnippet -->
