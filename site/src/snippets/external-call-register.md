<!-- snippet: external-call-register -->
```cs
services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .Stream<Order>(s => s.Events<OrderPlaced, PaymentRequested, PaymentCaptured, PaymentDeclined>())
    .Subscription<ChargePayment>("charge-payment"));
```
<!-- endSnippet -->
