---
name: deedbox
description: Write correct code with Deedbox, the .NET event-sourcing library for Postgres and SQL Server. Use when code registers streams, appends or loads events, writes projections or subscriptions, marks personal data, or changes an event.
---

# Deedbox

Deedbox stores events in the app's own Postgres or SQL Server database. Docs: https://deedbox-docs.pages.dev/llms-full.txt

## Rules

- Events are plain records. Do not add a base class or a marker interface.
- A state type implements `IState<TSelf>`: `static Initial` and `static Evolve(state, event)`. `Evolve` never throws and never does I/O.
- Decisions are pure functions from state to `IEnumerable<object>`. Pass them to `store.Execute<TState>(streamId, state => ...)`. Do not load and append by hand unless the caller needs a custom error type; then use `Load`, then `Append(streamId, ExpectedVersion.Exact(version), events)`.
- Register every event on its stream: `.Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())`. One CLR type belongs to one stream.
- Never rename a stored event without `.Alias("old.name")`. Never change an event's properties without raising its version and adding an upcaster: `.Event<T>(version: 2, up => up.From(1, json => ...))`.
- A projection registers handlers in its constructor with `On<T>`, overrides `ResetAsync`, and is registered with a stable name and one run mode: `.Projection<T>("name", Run.Inline)` or `Run.Async`.
- A subscription is for side effects outside the database. Delivery is at least once: pass `ctx.Envelope.EventId` on as an idempotency key, and pass `ctx.CancellationToken` to each call; `RunnerOptions.HandlerTimeout` (5 minutes) cancels through it.
- A new subscription handles every stored event. To handle only later events, register it with `.Subscription<T>("name", SubscriptionStart.Now)`.
- Mark personal data with `[property: DataSubject]` on the subject ID and `[property: PersonalData]` on each personal field. Personal fields are strings or nullable, and top-level: `[PersonalData]` on a nested type fails start-up (DBX026). A subject ID has 1 to 100 characters and no leading or trailing white space. Choose a key mode with `.Keys(...)`.
- To change the key mode or rotate the key on a running app, add the other key for unwrap only, as in `.Keys(k => k.StoreInDatabase().AlsoUnwrapWith(o => o.FromEnvironment("NEW_KEY")))`, and follow the order in https://deedbox-docs.pages.dev/how-to/rotate-keys/. Never run `deedbox keys rewrap` first.
- Never put an email or a login in a subject ID. Compute it with `IPseudonyms.SubjectForAsync("github:alice", PseudonymPeriod.Quarter(now))`, and erase by identity with `IPseudonyms.EraseIdentityAsync`.
- Never append `SubjectErased` or `StreamDeleted`. Use `ISubjectErasure.EraseSubjectAsync` and `IEventStore.DeleteStream`. On `IEventStoreAdmin`, `EraseSubjectAsync`, `EraseIdentityAsync` and `DestroyPseudonymPeriodAsync` need a `tenantId` (`""` for no tenants); the erase methods return `ErasureResult { JobIds, KeysDeleted }`, and zero keys means nothing was erased.
- In a transaction the app owns (`UseTransaction`, `UseDbContext`), make one append, then commit soon. On DBX038, roll back and run the transaction again. With EF Core `EnableRetryOnFailure`, wrap the whole call in the execution strategy to get retries.
- `IEventStore` and `IAppendingHook` members have no `Async` suffix; everything else has one. Result types have properties, not constructors; `var (state, version) = await store.Load<T>(id)` works. Statuses are enums: `ConsumerMode`, `ConsumerState`, `JobKind`, `JobState`. Do not implement `IEventStoreAdmin`.
- A custom `IMasterKeyProvider.WrapAsync` returns `new WrappedKey { Bytes = ..., KeyVersion = ... }`.
- A `[JsonDerivedType]` member needs .NET 9 or later on Postgres (DBX039 on .NET 8).
- Test decisions with `Deedbox.Testing`: `Decider.Given<TState>(events).When(decide).Then(expected)`. Keep an `EventContracts.Verify(...)` test and commit its `events.lock`.
- Every Deedbox error has a DBX code; its message states the fix and links to https://deedbox-docs.pages.dev/reference/errors/. Compare codes with `DeedboxError` constants: `catch (DeedboxException e) when (e.Code == DeedboxError.StreamDeleted)`.
