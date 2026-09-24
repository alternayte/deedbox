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
- A subscription is for side effects outside the database. Delivery is at least once: pass `ctx.Envelope.EventId` on as an idempotency key.
- Mark personal data with `[property: DataSubject]` on the subject ID and `[property: PersonalData]` on each personal field. Personal fields are strings or nullable. Choose a key mode with `.Keys(...)`.
- Never append `SubjectErased` or `StreamDeleted`. Use `ISubjectErasure.EraseSubjectAsync` and `IEventStore.DeleteStream`.
- In a transaction the app owns (`UseTransaction`, `UseDbContext`), append to one stream, then commit soon.
- Test decisions with `Deedbox.Testing`: `Decider.Given<TState>(events).When(decide).Then(expected)`. Keep an `EventContracts.Verify(...)` test and commit its `events.lock`.
- Every Deedbox error has a DBX code; its message states the fix and links to https://deedbox-docs.pages.dev/reference/errors/.
