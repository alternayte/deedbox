# ReadStream and the review before 1.0 (0.6.0)

## What it does
Release 0.6.0 adds `IEventStore.ReadStream`, which returns the events of one stream in pages, decrypted and upcast. It removes the compatibility bodies from `IEventStoreAdmin`, so the interface has its 1.0 shape. A review of the fence and the runner runs until one round confirms no defect that breaks a README guarantee. The docs gain a page on table growth, and the changelog loses three wrong entries.

## Decisions
### ReadStream
- The member is `Task<IReadOnlyList<EventEnvelope>> ReadStream(string streamId, long afterVersion = 0, int limit = 500, CancellationToken ct = default)` — one stream, forward, with `afterVersion` as the cursor of the caller.
- It returns the `EventEnvelope` that a subscription gets: upcast, personal data decrypted, erased fields redacted and listed in `ErasedSubjects` — raw SQL cannot show encrypted events, and this is the gap an audit view has.
- It takes no state type, and the tenant comes from `DeedboxContext` — a view reads any registered stream by its ID.
- It returns a page, not an `IAsyncEnumerable` — `Replay` buffers events before it reads subject keys, because both use one connection.
- A store bound with `UseTransaction` or `UseDbContext` reads in that transaction, as `Load` does — a caller sees its own uncommitted appends.
- A deleted stream returns its one `StreamDeleted` event; a missing stream returns an empty list — a read reports history, and a view must show that the stream was deleted and when. `Load` keeps DBX028.
- A stream whose stream type is not registered fails with DBX009; a `limit` below 1 fails with `ArgumentOutOfRangeException` — the app cannot decode the events, and a page needs a size.

### Interfaces
- `ReadStream` has no default body, and the default bodies of `RetireAsync`, `EraseIdentityAsync` and `DestroyPseudonymPeriodAsync` go — the docs state that apps do not implement these interfaces, and a body that throws is not a 1.0 contract.
- Package validation has suppressions for these changes only — any other break fails `just pack`.
- `IMasterKeyProvider` does not change — a context parameter cannot bind an RSA-OAEP wrap in Key Vault, and the core can bind a wrap to its tenant later with an HKDF derivation in a new storage format.
- Error messages keep `https://deedbox-docs.pages.dev` — Cloudflare Pages keeps that address when a custom domain is added. The Pages project `deedbox-docs` is never deleted or renamed.

### Review of the fence and the runner
- Scope: `Instances.cs`, `AsyncRunner.cs`, `Consumers.cs`, `Jobs.cs`, `CheckpointSetup.cs`, `EventStore.Fenced`, and the provider SQL that these call, on both providers — this code is new in 0.5.0, and its last review found 22 defects.
- A finding counts only with a failing test — a claim without a reproduction is not a defect.
- A guarantee defect loses an event, applies one twice in a projection, moves a checkpoint past an unapplied event, or leaves erased data readable. Every other finding is an edge defect — the README promises the first class to users.
- A guarantee defect is fixed in 0.6.0, and the review runs once more on the changed code only. An edge defect is fixed in 0.6.0 with no new round — a rule of zero findings has no end.
- The release ships after one round with zero guarantee defects — that is the exit.

### Docs and changelog
- A new page, "Table growth", states that loads and the runner do not slow as the events table grows, that rebuilds and disk grow with it, and that `DeleteStream` removes data — users plan storage from facts, and archiving is not there.
- `CHANGELOG.md` loses the DBX040 "Breaking" entry in 0.3.0, 0.3.1 and 0.4.0 — the change shipped in 0.5.0 only.

## Out
- Stream archiving and partitions of the events table. They can ship in 1.x without a break.
- Backward reads, a read of all streams by global position, and filters by event type.
- Caller-supplied event IDs, and an `Execute` that returns a value.
- A docs domain, the supported-version matrix and the 1.0 support policy.
- A soak run of several days, and rebuild benchmarks. The nightly torture suite at scale 5 stays the long-run evidence.

## How I know it works
- `ReadStream` on a stream with a `[PersonalData]` event returns the plain value; after `EraseSubjectAsync` it returns the placeholder, and `ErasedSubjects` names the subject.
- Two calls with `limit: 2`, the second with `afterVersion` set to the last version of the first, return versions 1 to 4 of a five-event stream in order.
- `ReadStream` on a deleted stream returns one `StreamDeleted` event; on an unknown ID it returns an empty list; `Load` on the deleted stream still fails with DBX028.
- Inside `UseTransaction`, `ReadStream` returns an event that the same transaction appended and did not commit.
- A class that implements `IEventStoreAdmin` without `RetireAsync` does not compile.
- The review record in `PROGRESS.md` lists each round, its findings with their test names, and a last round with zero guarantee defects.
- `grep -c "DBX040" CHANGELOG.md` gives 1 entry under "Changed", in 0.5.0.
- `just check` passes on both providers and both frameworks.
