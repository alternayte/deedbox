# Deedbox

[![ci](https://github.com/alternayte/deedbox/actions/workflows/ci.yml/badge.svg)](https://github.com/alternayte/deedbox/actions/workflows/ci.yml)
[![nightly](https://github.com/alternayte/deedbox/actions/workflows/nightly.yml/badge.svg)](https://github.com/alternayte/deedbox/actions/workflows/nightly.yml)
[![license](https://img.shields.io/github/license/alternayte/deedbox)](LICENSE)

**Event-source part of your app. Postgres or SQL Server. EF Core, Dapper, or neither.**

Deedbox stores the events of one area of your app, such as orders or manuscripts, in the database you already have. Its tables live in their own schema. Appends share your connection and transaction. There is no mediator, no document database and no base class.

## A cart in 30 seconds

<!-- snippet: cart-events -->
```cs
// Events: plain records. No marker interface, no base class.
public record ItemAdded(string Sku, int Qty);

public record CheckedOut(DateTimeOffset At);
```
<!-- endSnippet -->

<!-- snippet: cart-state -->
```cs
// State: Initial and Evolve. Nothing else.
public record Cart(ImmutableDictionary<string, int> Items, bool IsCheckedOut) : IState<Cart>
{
    public static Cart Initial { get; } = new(ImmutableDictionary<string, int>.Empty, false);

    public static Cart Evolve(Cart s, object e) => e switch
    {
        ItemAdded x => s with { Items = s.Items.SetItem(x.Sku, s.Items.GetValueOrDefault(x.Sku) + x.Qty) },
        CheckedOut => s with { IsCheckedOut = true },
        _ => s,
    };
}
```
<!-- endSnippet -->

<!-- snippet: cart-decider -->
```cs
// Decisions: pure functions from state to new events.
public static class CartDecider
{
    public static IEnumerable<object> Add(Cart cart, string sku, int qty) =>
        cart.IsCheckedOut
            ? throw new InvalidOperationException("The cart is checked out.")
            : [new ItemAdded(sku, qty)];

    public static IEnumerable<object> CheckOut(Cart cart, DateTimeOffset now) =>
        cart.IsCheckedOut || cart.Items.IsEmpty ? [] : [new CheckedOut(now)];
}
```
<!-- endSnippet -->

<!-- snippet: register-postgres -->
```cs
builder.Services.AddDeedbox(es => es
    .UsePostgres(connStr)
    .ApplySchemaOnStartup()
    .Stream<Cart>(s => s                   // stream type "cart"
        .Events<ItemAdded, CheckedOut>())); // cart.item_added, cart.checked_out
```
<!-- endSnippet -->

<!-- snippet: write-execute -->
```cs
// Load, decide, evolve and append in one transaction.
var result = await store.Execute<Cart>(cartId, cart => CartDecider.Add(cart, sku, qty));

// result.State is the new state; result.Version the new version; result.Events the appended envelopes.
```
<!-- endSnippet -->

The [first-stream tutorial](https://deedbox-docs.pages.dev/tutorials/first-stream/) runs this end to end.

## What Deedbox is, and is not

Deedbox is a library for the event-sourced parts of an app you own: streams, concurrency, snapshots, projections, subscriptions, upcasting and personal-data erasure, on one algorithm for both databases.

Deedbox is not a document database, a mediator, a command bus, a message broker or a validation framework. It has no `Result` type and no opinion on errors.

Do not use Deedbox when:

- you want a document database too (use Marten or Polecat),
- you need tens of thousands of appends per second to one store; appends take turns on one counter ([benchmarks](https://deedbox-docs.pages.dev/operations/benchmarks/)),
- you want KurrentDB (EventStoreDB).

## Install

| You use | Install |
| --- | --- |
| Postgres | `dotnet add package Deedbox.Postgres --prerelease` |
| SQL Server | `dotnet add package Deedbox.SqlServer --prerelease` |
| EF Core projections or `UseDbContext` | also `Deedbox.EntityFrameworkCore` |
| Decider tests and the event-contract lockfile | `Deedbox.Testing` in your test project |
| Master key in Azure Key Vault | `Deedbox.Keys.AzureKeyVault` |
| Delivery through QueueBox | `Deedbox.QueueBox` |
| The CLI | `dotnet tool install -g Deedbox.Cli --prerelease` |
| A starter project | `dotnet new install Deedbox.Templates`, then `dotnet new deedbox` |

## Guarantees

Each guarantee has a test that enforces it on Postgres and SQL Server.

1. **No event is ever skipped.** No timeout moves a reader past a gap. ([torture suite](tests/Deedbox.Tests/Torture/))
2. **A checkpoint is the last event actually applied,** committed in the same transaction as the projection's writes.
3. **Per-stream order holds, and global order is commit order.**
4. **An idle store stays under a query budget.**
5. **`OnAppending` runs in the transaction; everything else is a checkpointed subscription.**
6. **Names are stored and checked at start-up.**
7. **Stored state is versioned** and rebuilt when its version changes.
8. **A poison event stops its projection** and names the event; skipping it is an audited command.
9. **Health means progressing, not caught up.**
10. **The public API is tracked,** and the on-disk schema never breaks.

## QueueBox

Deedbox pairs with [QueueBox](https://github.com/alternayte/queuebox), which delivers messages from an outbox table to brokers and webhooks. `Deedbox.QueueBox` writes the outbox rows in the append's transaction; see [wire QueueBox](https://deedbox-docs.pages.dev/how-to/wire-queuebox/).

## Documentation

The docs are at [deedbox-docs.pages.dev](https://deedbox-docs.pages.dev). They cover tutorials, how-to guides, concepts, reference and runbooks.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Deedbox is MIT-licensed.
