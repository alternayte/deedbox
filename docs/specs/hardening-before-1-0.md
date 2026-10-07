# Hardening before 1.0 (0.5.0)

## What it does
Release 0.5.0 closes the defects and design gaps from the 1.0 readiness review of 2026-10-06. An inline projection no longer misses an append from an instance that is not live, and a cut-over never holds the position counter without a bound. Erasure leaves no plain text in stored state, stall records or job rows. Stored crypto formats carry a version, and a master key can change mode while the app runs. The public result types and `IMasterKeyProvider` change shape, so this is a breaking minor release.

## Decisions
### Inline projections and the runner
- A cut-over, under the position counter lock, deletes the heartbeat row of each instance that is not live and would skip the projection — the cut-over already ignores that instance, so its later appends must not pass.
- Every append confirms its own heartbeat row in the statement that takes the position counter — the counter orders the check against the cut-over, and it adds no round trip.
- An evicted instance writes its heartbeat again and moves the projection back to catch-up, as start-up does — its next append then passes.
- A transaction that Deedbox owns retries once after an eviction; a caller-owned transaction fails with DBX038 — Deedbox cannot replay the caller's other work. This replaces the 0.3.0 rule that an append never fails during a deploy.
- A forced cut-over holds the position counter for at most 2 seconds; if it does not reach the head, it commits the events it applied and stays in catch-up — an unbounded hold stops every append in the store.
- Polls in single-step mode after a handler failure do not count toward the 20 that force a cut-over — that mode cannot shrink the gap.
- After 5 forced attempts in a row that do not finish, the runner logs a warning and the health check reports degraded with the gap — the handler is too slow for the append rate, and the operator must know.
- A skip on an inline projection sets its checkpoint to rebuilding, not running — running at the skipped position drops every event up to the head.
- Stall and job records keep the exception type and stack frames, without message text; the full exception goes to the log — message text can hold personal data, and those rows outlive an erasure.
- Migration 6 removes `message` and `stackTrace` from stored stall and job JSON, and adds an index on `subject_streams (tenant_id, stream_id)` and a claim index on `jobs` — old rows hold the same text, and both tables are scanned today.

### Subscriptions
- A new subscription starts at the first event by default; `Subscription<T>(name, SubscriptionStart.Now)` starts at the head when its checkpoint row is first created — the default skips no event, and the option has no effect afterwards.
- Start-up logs a warning with the name and the event count when it creates a subscription checkpoint on a store that holds events — the operator sees a replay of side effects before it runs far.
- A subscription commits its checkpoint after each event; a batch projection keeps one commit per batch — a crash then repeats one side effect, not a whole batch.
- `RunnerOptions.HandlerTimeout`, default 5 minutes, cancels a handler call and counts it as a failed attempt — a handler that hangs holds the checkpoint row and a connection for the life of the process.
- A transient database error in a handler is retried with backoff and never counts toward a poison stall — a failover is not a fault of the event.

### Writes
- A write in a caller-owned transaction runs inside a savepoint, rolled back on any failure — a caller that catches the error and commits must not keep a stream row whose version has no events.
- The commit does not take the caller's cancellation token — a cancelled request must not leave the caller unsure whether the write happened.
- `AppendResult`, `ExecuteResult`, `LoadResult`, `StoreStatus`, `ConsumerStatus` and `JobInfo` become non-positional records, and their status, mode and kind strings become enums — a new field must not be a binary break after 1.0.
- DBX codes are public constants, and the docs state that apps do not implement `IEventStoreAdmin` — callers compare codes without string literals, and the interface can grow.
- The members of `IEventStore` and `IAppendingHook` keep their names without the `Async` suffix; the docs state the rule — the suffix separates sync from async, and Deedbox has no sync members.

### Personal data and keys
- Erasure deletes the subject key first, then clears stored state, and queues the erase job in the same transaction — an open append cannot commit a readable snapshot after the call returns, and a crash cannot leave a key deleted with no job.
- The lazy snapshot write on a load does nothing when the stored state changed since the read — a load that decrypted before an erasure must not write the data back.
- The erase job records the key ID and deletes only that key; an instance without the stream type leaves the job for another — a subject that returns keeps the new key, and every stream gets its SubjectErased.
- The JSON name of a `[PersonalData]` property comes from the serializer contract; start-up fails when it does not resolve or when a nested type carries the attribute — a field that is skipped is stored in plain text.
- Subject IDs follow the stream ID rules for length and white space — SQL Server otherwise treats two IDs as one key.
- Admin erasure takes a required tenant and returns the number of keys deleted — a call on the wrong tenant must not look like success.
- Stored state is sealed with a key derived from the tenant key and the stream ID — the random-nonce bound then applies per stream, with no key rotation.
- Every encrypted blob carries a format version; a v2 field binds the event ID and the field name; v1 stays readable — a copied field must not verify, and a later format needs a way in.
- `IMasterKeyProvider.WrapAsync` returns the key version with the bytes — reading it afterwards from the provider races with a key rotation.
- A key mode names extra providers for unwrap only; the rotate-keys guide becomes deploy with both, re-wrap, remove the old one — a running instance must read rows under either key during the change.

### Proof and repo
- New tests: an upgrade from each released schema with data; EF Core `EnableRetryOnFailure` with `UseDbContext` and an EF projection; a `[JsonDerivedType]` event on Postgres; an app published with `PublishAot` — each claim or suspected defect needs a run, and a failure becomes a fix in this release.
- The Kubernetes guide uses a readiness probe; the QueueBox guide drops its ordering claim; the error index lists every code; `.wrangler/cache` leaves the repo; release actions are pinned to commits — each is wrong or exposed today.

## Out
- The `Async` rename, caller-supplied event IDs and a `ReadStream` API.
- Tenant key rotation.
- Public options for the cut-over hold and the heartbeat interval.
- The docs domain, the supported-version matrix and the 1.0 support policy.

## How I know it works
- An old instance whose heartbeat is blocked past the liveness window keeps appending through a cut-over; the projection has applied every event exactly once, and a caller-owned append got DBX038.
- An inline rebuild under steady writers with failing handlers never holds an append longer than the 2-second limit plus one batch.
- After a skip on a stalled inline rebuild, the projection's row count equals the event count.
- A hook that throws inside a caller transaction, followed by a commit, leaves `streams.version` equal to the highest event version.
- With the runner off, an erasure that races an open append and a load leaves no readable state for the subject; `checkpoints` and `jobs` hold no exception message.
- A field copied to another event fails with DBX030; a store written by 0.4.1 loads, appends and erases.
- A re-wrap from database mode to an environment key on a running app causes no DBX029.
- A new subscription with `SubscriptionStart.Now` on a store with events delivers only later events; one without it logs the warning and delivers all.
- `just check` passes on both providers and both frameworks, with the new tests and `just pack` validation suppressions only for the listed API changes.
