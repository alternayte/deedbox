<!-- snippet: subscription -->
```cs
// A subscription does anything outside the database. Delivery is at least once,
// so pass the event ID on as an idempotency key.
public sealed class SendReceipt : Subscription
{
    // Subscriptions are created once; a scoped service comes from ctx.Services instead.
    public SendReceipt(IEmailSender email) =>
        On<CheckedOut>((_, ctx) => email.SendReceipt(ctx.Envelope.StreamId, ctx.Envelope.EventId, ctx.CancellationToken));
}
```
<!-- endSnippet -->
