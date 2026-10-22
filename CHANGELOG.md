# Changelog

This file records every notable change to the Deedbox packages. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the versions follow [Semantic Versioning](https://semver.org/). In 0.x, only a minor release can break the public API, and its entry says how. The storage schema never breaks: each change ships a forward migration.

## [0.8.1] - 2026-10-20

This release fixes the torture test that backs the archive, and replaces the published benchmark numbers with the numbers of a run on the 0.8.0 code. No shipped code changes, so no app behaviour changes.

### Fixed

- `PostgresRunnerTortureTests.Killed_restarted_and_competing_runners_apply_every_event_exactly_once` failed on about half of the nightly runs, and on the 0.8.0 release commit ([run](https://github.com/alternayte/deedbox/actions/runs/38918521283)). The failure was in the test, not in the store. The test queues a rebuild of a projection with the archive under the chaos, waits for every consumer to reach the head, then asks for one more archive of every stream and expects the events table to be empty. A session kill rolls a runner's job claim back, so the rebuild job could still be queued when that wait ran. The rebuild then moved the projection to the start of the stream, the archive line moved with it, and the last archive moved nothing and reported done. The test now waits for every job it queued, by job ID, before it reads the consumers. The store's guarantees are unchanged, and the README guarantees stand as written: the archive line held because an archive moves no event that a consumer has not applied.
- The same test waited for "every job finished" through `GetStatusAsync().Jobs`, which holds the 20 newest jobs. The chaos queues more than 20 jobs at torture scale 5, so the oldest were outside that window and the wait passed over them. The wait and the assertion go by job ID now. At torture scale 5 the chaos leaves a backlog of more than 80 archive jobs, so the wait fails when no job finishes for 90 s, not when the whole backlog takes more than 90 s.
- `SqlServerUpgradeTests.A_store_that_a_release_wrote_upgrades_and_the_release_still_runs_on_it` failed on the nightlies of 2026-10-12, 10-18 and 10-20 with the `seen` projection stalled at position 0 on its first event. The failure was in the test's old-release app, not in the store. The app waited for a counter that its projection handler set before the batch committed, then stopped the host. The stop cancelled the commit in flight, so the `seen` table that the handler created rolled back, and the new build's projection found no table. The app now waits for the committed checkpoint.
- The nightly benchmark job failed on runner variance: its 30% band against a single-sample baseline was narrower than the runner's spread between runs of one commit. The band comes from a measurement now. Seven nightly runs measure identical benchmarked code, and `bench/baseline.json` holds the median appends/s of each cell over those runs, with the ID and the commit of every run. The job fails a cell more than 40% below that median on Postgres, or more than 65% on SQL Server; the worst drop those seven runs measured is 21.8% on Postgres and 51.6% on SQL Server. A red benchmark job reports a regression again, and `just bench` applies the same bands.

### Added

- A provider test: an archive job that runs while a projection rebuilds moves nothing and counts the stream partial; one more archive, after the replay reaches the head, moves every event of the stream.
- The torture test reports the head, the archive line, every checkpoint and what each stream has left when the events table is not empty, so a failure names its cause.

### Changed

- Docs: the benchmarks page carries the numbers of the nightly run of 2026-10-19 on commit `0a704c0`, which holds the 0.8.0 code ([run](https://github.com/alternayte/deedbox/actions/runs/39224756475)). The numbers before this release came from commit `0d48a6d` of 2026-09-24, the day the repository was created, and no run on current code reproduces them. The page states the spread between seven nightly runs of identical benchmarked code, and the band of the nightly benchmark gate against the median of those runs.

## [0.8.0] - 2026-10-16

This release archives finished streams ([#10](https://github.com/alternayte/deedbox/issues/10)). It breaks only an app that implements `IEventStoreAdmin` itself; apps call the interface and do not implement it. Migration 8 is a forward migration. After the first archive, do not roll back to 0.7 or earlier.

### Added

- `IEventStoreAdmin.ArchiveStreamsAsync(streams)` and `ArchiveStreamAsync(tenantId, streamId)` queue an archive job. The job moves the streams' events from `deedbox.events` to the new `deedbox.events_archive` table, one stream per transaction. It moves only events that every projection and subscription that is not retired has applied. A stream with later events is archived up to them and counted as partial; a later archive moves the rest. The job progress counts archived, partial and skipped streams, and the events moved.
- An archived stream loads, reads, appends, erases and deletes as before. `Load`, `ReadStream` and every replay of stored state read both tables. An append writes to `deedbox.events`. `DeleteStream` deletes the archived events too.
- `IEventStoreAdmin.RebuildWithArchiveAsync(projection)` and `deedbox rebuild <projection> --include-archived` replay archived events too, merged in global position order. A plain rebuild, a new projection and a new subscription read only `deedbox.events`.
- `deedbox archive --stream <id>` or `--file <path>`, with `--tenant` and `--wait`.
- `StreamKey`, `JobKind.Archive`, and the `deedbox.archive.events` counter.
- DBX041: an archive fails while a heartbeat row comes from Deedbox 0.7 or earlier. Those versions do not read the archive table, so they would rebuild an archived stream's state from part of its events. This version records storage format 3 in its heartbeat row.
- Migration 8 adds the `events_archive` table, and the `streams.archived_to` and `checkpoints.archive_until` columns.
- Docs: the "Table growth" page describes the archive, and "Known limits" states its limits and the rollback limit.

### Changed

- **Breaking:** a class of your own that implements `IEventStoreAdmin` must implement `ArchiveStreamsAsync`, `ArchiveStreamAsync` and `RebuildWithArchiveAsync`.

## [0.7.0] - 2026-10-16

### Added

- `Deedbox.QueueBox`: `QueueBoxMessage.AggregateType` sets the `aggregate_type` of one event's outbox row. A destination that routes on it, such as with `exchangeFrom: aggregate_type`, can send the events of one stream to different exchanges. Null writes the stream type, as before. An empty value, or a value over 255 characters, fails the append with DBX032 ([#9](https://github.com/alternayte/deedbox/issues/9)).

### Changed

- `Deedbox.QueueBox` writes one `INSERT` statement per outbox row, in stream version order, in one command. Before, the rows of one append shared one multi-row `VALUES` list, and neither database documents the order in which it fills the QueueBox `sequence` column across such a list ([#8](https://github.com/alternayte/deedbox/issues/8)).

### Fixed

- The "Wire QueueBox" guide says that one stream's messages reach a destination in stream order on QueueBox 0.4.0 and later, with migration V11 applied. It states the cost: one message of a stream is in flight at a time, and a message that waits for a retry holds back the later messages of its stream. The `x-deedbox-stream-version` advice stays for older QueueBox versions and for other relays ([#8](https://github.com/alternayte/deedbox/issues/8)).

## [0.6.0] - 2026-10-09

This release gives the public interfaces their 1.0 shape. It breaks only an app that implements `IEventStore` or `IEventStoreAdmin` itself; apps call these interfaces and do not implement them.

### Added

- `IEventStore.ReadStream(streamId, afterVersion, limit)` reads one page of a stream's events in version order. Each item is the envelope that a subscription gets: the event in its current shape, personal data decrypted, and erased fields redacted with their subjects in `ErasedSubjects`. A missing stream gives an empty list, and a deleted stream gives its `StreamDeleted` event. A store from `UseTransaction` or `UseDbContext` reads in that transaction.
- Migration 7 adds the nullable `instances.handles` column.
- Docs: the "Table growth" page states what a growing events table costs and how to remove data.

### Changed

- **Breaking:** `IEventStoreAdmin.RetireAsync`, `EraseIdentityAsync` and `DestroyPseudonymPeriodAsync` have no default body. A class of your own that implements the interface must implement them.
- **Breaking:** a class of your own that implements `IEventStore` must implement `ReadStream`.

### Fixed

A review of the fence and the runner found these defects. Each has a test that failed before its fix.

- An instance that registers a projection as async, and that a cut-over evicted while the projection went inline, joined again and appended without the projection. The join now stalls the projection as `mode_changed` in the transaction that writes the heartbeat row.
- An inline projection that handles `StreamDeleted` or `SubjectErased` missed that event when an app with other stream types, or with a stream type that has no events, appended it. Such an instance now counts as one that skips the projection, so the projection stays in catch-up while that instance is live.
- On SQL Server, a projection handler that caught an error with which the server rolled the transaction back made the runner commit the checkpoint past events whose writes were gone. The same catch in an inline projection or an appending hook stored events without their stream row, and in a reset it made a rebuild apply every event over the old rows. Deedbox now fails the event, the append or the rebuild job, and checks after each call.
- During a rolling deploy, an instance of the version that handles fewer events moved the checkpoint of an async projection or a subscription past events that only the newer version handles. It now leaves the checkpoint to a live instance that handles more. The heartbeat rows decide, read in the same transaction as the batch. A rebuild records the events of every instance with a heartbeat row, whichever version runs the job. Migration 7 adds the `instances.handles` column for this. The rule covers instances on 0.6.0 and later.
- An instance of the older version could run a rebuild job while a newer version was live. Its reset did not clear what the newer version wrote, so the replay applied those events again. It now leaves the job to the instance that handles more.
- A failure after a handler returned, such as an aborted transaction, a reader that the handler left open, or a session that the database ended during each call, was never counted. The consumer retried without end and could not be skipped. It now counts like a failure in the handler, so the consumer stalls on the event.
- A new catch-up of an inline projection started with the forced cut-over that an earlier catch-up left pending, and held the position counter with no appends to outrun.
- On SQL Server without `READ_COMMITTED_SNAPSHOT`, a start-up held the position counter while it waited for the rebuild of another projection, and an append that applies an inline projection waited for that rebuild too.
- A rebuild job for a subscription name stayed queued for ever when two instances registered the subscription. It now fails at once with the reason.
- An instance that rejected a job could overwrite the result of an instance that ran the job since.
- The changelog listed the DBX040 isolation-level change under 0.3.0, 0.3.1 and 0.4.0. It shipped in 0.5.0 only.

## [0.5.0] - 2026-10-07

This release closes the defects from the 1.0 readiness review. It breaks the public API in the places listed under "Changed"; migration 6 is a forward migration.

### Fixed

- A skip on an inline projection that stalled in catch-up put it back to `running` at the skipped position, so every event between that position and the head was never applied. The skip now puts it back to catch-up.
- An inline projection in catch-up tried its cut-over again while it stepped through a failed batch. It then stalled at the wrong position, and `deedbox skip` refused the event. It now steps to the failing event first.
- A stalled inline projection now retries its poison event on the schedule, as other consumers do.
- A failed append in a caller's transaction (`UseTransaction`, or `UseDbContext` with an open transaction) left the stream row at the new version with no events, when the caller caught the error and committed. The write now runs in a savepoint.
- `EraseSubjectAsync` could return while a stream still had readable stored state for the subject: an append that was open during the erasure, or a load that rebuilt a snapshot, could store it after the state was cleared. The erasure now clears state again after the key is gone, and a load rebuilds a snapshot only under the stream's lock.
- The key deletion and the erasure job are one transaction. Before, a crash between them left the key deleted with no job, so no `SubjectErased` was appended.
- The erasure job deletes no key. Before, it deleted the subject's current key, so a subject who came back before the job ran lost the new data.
- An erasure job on an instance that does not register a stream type removed that stream's subject pair and appended nothing. Each instance now handles the stream types it registers, and the job waits for an instance that registers the rest.
- A `[PersonalData]` property whose JSON name differs from the naming policy, through a contract modifier or a custom resolver, was stored in plain text. The name now comes from the serializer's contract.
- A `[PersonalData]` or `[DataSubject]` attribute on a type nested in an event did nothing. Start-up now fails with [DBX026](https://deedbox-docs.pages.dev/reference/errors/dbx026/).
- A subject ID follows the stream ID rules: 1 to 100 characters with no leading or trailing white space ([DBX027](https://deedbox-docs.pages.dev/reference/errors/dbx027/)). SQL Server treated two subject IDs that differ in trailing spaces, or after character 100, as one key. The rule applies to appends; an erasure still accepts any ID that an older version stored.
- An event or state with a polymorphic member (`[JsonDerivedType]`) could not be read back on Postgres, because `jsonb` reorders keys. On .NET 9 and later Deedbox reads it. On .NET 8 start-up fails with [DBX039](https://deedbox-docs.pages.dev/reference/errors/dbx039/).
- `UseDbContext` and `Projection<TDbContext>` failed when the context used a retrying execution strategy, such as `EnableRetryOnFailure`.
- The commit of an append no longer takes the caller's cancellation token.
- The Kubernetes guide used the health check as a liveness probe, which restarts every pod while a subscription is stalled. The guide now keeps the check off both probes and gives it its own path for alerts.
- The "Wire QueueBox" guide said one stream's messages keep their order. They do not ([#1](https://github.com/alternayte/deedbox/issues/1)).

- An inline projection could miss appends from an instance that was paused past the liveness window, or whose heartbeat failed, and then went on appending. A cut-over now removes the heartbeat row of an instance that is not live, and every append checks its own row under the position counter. An append without a row writes nothing. Deedbox joins the instance again and repeats an append in a transaction that it owns; an append in your transaction, or with a DbContext passed to `UseDbContext`, fails with [DBX038](https://deedbox-docs.pages.dev/reference/errors/dbx038/), and you run the transaction again. The check covers instances on 0.5.0 and later.
- A cut-over that found a live instance to wait for went on with a normal batch while it still held the position counter. It now gives the counter back first.
- A forced cut-over held the position counter until it had applied every remaining event, so every append in the store waited without a limit. It now holds the counter for at most 2 seconds, keeps the events it applied, and stays in catch-up. It tries again after appends had the same time, so holds and catch-up alternate until it finishes. After five such attempts in a row, the health check reports the projection as degraded.
- A handler that never returned held its checkpoint and a connection until the process stopped. `RunnerOptions.HandlerTimeout`, 5 minutes by default, now cancels the call through its token and counts it as a failed attempt. This also covers a handler that blocks its thread. A subscription handler that ignores the token is left behind after 10 more seconds. A projection handler writes in the batch's transaction, so the runner waits for it: pass the token to everything it awaits.
- With `HandlerRetries = 0`, a consumer stalled with its checkpoint before the whole batch, not before the failing event, so `deedbox skip` refused the event. A stall is now recorded only after the event failed on its own.
- A skip or rebuild that waited for a failing batch could be overwritten by that batch's stall record.
- A handler that left a reader open on the batch's connection made the rollback fail, and the event was then retried without end and never stalled.
- A projection registered inline by one instance and async by another could be applied both ways. An instance now leaves a checkpoint of the other run mode alone, a rebuild takes the inline gate for either mode, and an append applies a projection inline only while its checkpoint is inline.
- A new inline checkpoint was created as `running` from a read that a joining instance could race, and a process that appended without a started host applied an inline projection with no checkpoint at all. Checkpoints are now created under the position counter, by hosted and hostless processes alike.
- When a new version of an inline projection handles an event that another app appends, that app's appends skipped it. The projection now goes back to catch-up until that app stops. A checkpoint records every event that any version handled since its last rebuild.
- A cut-over waits for an instance from before 0.5.0 as long as it has a heartbeat row, because such an instance does not check its row when it appends.
- A failover or a lost connection while a handler ran counted as an attempt on the event, so a short database outage stalled consumers. A transient database error is now retried with backoff and counts no attempt. An error that stays transient at one event for longer than `StallAfter`, such as a query that always times out, then counts as a failure of that event, so the consumer stalls and the event can be skipped.
- A subscription committed its progress once per batch, so a crash repeated the side effects of up to 500 events. It now commits after each event.
- A random AES-GCM nonce is safe for about 2^32 messages under one key, and a tenant key sealed every stored state of its tenant for ever. Stored state is now sealed with a key derived from the tenant key for one stream.
- A personal-data field copied to another event or property of the same subject still verified. A field now binds its event ID and its JSON name ([DBX030](https://deedbox-docs.pages.dev/reference/errors/dbx030/) otherwise).
- In database mode, an instance kept the master key in memory. After `deedbox keys rewrap` moved the keys away, it wrapped new tenant keys with a key that no longer existed. The key is now read for each wrap.
- With a Key Vault key that rotated between two overlapping wraps, a row could name the wrong key version.

### Changed

- **Breaking:** an append in your own transaction at REPEATABLE READ, SERIALIZABLE or SNAPSHOT fails with [DBX040](https://deedbox-docs.pages.dev/reference/errors/dbx040/). Deedbox orders appends with locks, and a transaction that keeps one snapshot reads a state from before a lock it waited for. Use READ COMMITTED, the default of both databases.

- **Breaking:** `AppendResult`, `ExecuteResult<T>`, `LoadResult<T>`, `StoreStatus`, `ConsumerStatus` and `JobInfo` have properties, not constructor parameters, so a later release can add one. `var (state, version) = await store.Load<T>(id)` still works.
- **Breaking:** `ConsumerStatus.Mode`, `ConsumerStatus.Status`, `JobInfo.Kind` and `JobInfo.Status` are enums: `ConsumerMode`, `ConsumerState`, `JobKind`, `JobState`. `deedbox status` prints the same text as before.
- **Breaking:** `IMasterKeyProvider.WrapAsync` returns a `WrappedKey`: the bytes and the key version that wrapped them, from the same call. A provider of your own returns `new WrappedKey { Bytes = ..., KeyVersion = ... }`.
- Appends write storage format 2 for personal-data fields, subject keys and stored state once every instance with a heartbeat can read it. While a 0.3 or 0.4 instance runs, they write format 1, so a rolling deploy works. Format 1 stays readable. After 0.5.0 has written format 2, an older version cannot read that data, so a rollback needs a restore.
- A marker for an encrypted field is read only as the whole value of a top-level property, which is the only place an append writes one.
- **Breaking:** `IEventStoreAdmin.EraseSubjectAsync`, `EraseIdentityAsync` and `DestroyPseudonymPeriodAsync` take a required `tenantId`; pass `""` when the app has no tenants. The two erasure methods return an `ErasureResult` with the job IDs and the number of keys deleted. Zero keys means the subject ID or the tenant is wrong, or the subject was erased before.
- `deedbox erase <subject>` exits 1 when it deleted no key.
- A stall and a failed job record the exception type and stack frames, not the message. A message can hold personal data, and these rows outlive an erasure. The full exception is in the log. The stall JSON has `exception` and `stack`; `message` and `stackTrace` are gone.
- Migration 6 removes exception messages from existing stall and job rows, and adds an index on `subject_streams (tenant_id, stream_id)` and on queued jobs.

### Added

- `KeysBuilder.AlsoUnwrapWith(...)` adds a master key that only unwraps, so the key mode or the key can change while the app runs. The "Rotate keys" guide has the order.
- `Subscription<T>(name, SubscriptionStart.Now)` starts a new subscription after the newest stored event. The default is still the first event; start-up now logs a warning when a new subscription will handle events that the store already holds.
- `DeedboxError` holds every DBX code as a constant.
- `JobInfo.StartedAt`.
- Tests that run each of 0.1.0, 0.2.1, 0.3.1 and 0.4.1 from nuget.org: the release writes a store, this build migrates and uses it, and the release then appends to the migrated store, as an old pod does in a rolling deploy.
- A native AOT app in the gate: it appends, loads and erases through a trimmed native binary.

## [0.4.1] - 2026-09-29

### Fixed

- `EventContracts.Verify` leaves out a property marked `[JsonIgnore]`, because System.Text.Json never writes it. Before, the lockfile listed it, so a computed, ignored property added to a type inside a locked event failed the check as "added as non-nullable", although the stored JSON did not change. A property ignored only when null or default stays in the lockfile, because it is still written. A lockfile written before 0.4.1 that lists an ignored property now fails with "was removed" for it: delete that member from the lockfile line, because stored events never held it ([#5](https://github.com/alternayte/deedbox/issues/5)).

## [0.4.0] - 2026-09-29

### Added

- `IPseudonyms.SubjectForAsync(identity, periodId)` turns a real identity, such as `github:alice`, into a keyed subject ID, such as `person:k7q2m9x4…`: the prefix plus the first 128 bits of HMAC-SHA256 in lower-case base32. Each tenant and period has its own random secret, wrapped by the master key. The same identity and period give the same subject ID on every instance. `PseudonymPeriod.Quarter(at)` and `PseudonymPeriod.Month(at)` build UTC period IDs; one fixed period ID gives subject IDs that never change. `DeedboxBuilder.PseudonymPrefix(prefix)` sets the prefix, `person:` by default. Migration 5 adds the `pseudonym_keys` table.
- `IPseudonyms.EraseIdentityAsync(identity)`, `IEventStoreAdmin.EraseIdentityAsync(identity, tenantId)` and `deedbox erase --identity <id> --master-key <key>` erase the identity's subject in every period whose secret still exists.
- `IEventStoreAdmin.DestroyPseudonymPeriodAsync(periodId, tenantId)` and `deedbox pseudonyms destroy <period> --yes` destroy a period's secret, so its subject IDs can never be linked to an identity again. A `pseudonyms_destroyed` job row records it. A destroyed period stays closed ([DBX036](https://deedbox-docs.pages.dev/reference/errors/dbx036/)). A period keeps its prefix ([DBX037](https://deedbox-docs.pages.dev/reference/errors/dbx037/)).
- The identity is never stored, logged, traced, measured or put in an error message. Reads and rebuilds never need a pseudonym secret.
- Docs: the how-to guide "Use pseudonymous subject IDs", and a pseudonym section in "Erasure and the key hierarchy".

### Changed

- Shredding a tenant also deletes its pseudonym secrets.
- `RewrapKeysAsync` and `deedbox keys rewrap` also re-wrap pseudonym secrets, in the same transaction, and the count includes them. No subject ID changes.
- `IEventStoreAdmin.EraseIdentityAsync` and `DestroyPseudonymPeriodAsync` have default bodies, so an implementation of the interface written for 0.3 still compiles.

## [0.3.1] - 2026-09-26

### Changed

- A consumer that stalls on a poison event retries the event every 5 minutes, and runs again once it succeeds. Before, it retried only when an instance started, so an outage of a service that a subscription calls stopped the subscription until a restart or a skip. The instances share one schedule, so the event gets one attempt per interval. The consumer stays `stalled` while it retries, so the health check still reports it and `deedbox skip` still works; `deedbox status` shows the attempts and the next retry time.
- Only a stall that exists when an instance starts gets that instance's immediate round of retries. Before, every instance that had not stalled the consumer itself gave it one more round.

## [0.3.0] - 2026-09-25

### Added

- Each app instance writes a heartbeat: the projections it runs and the event types it can append. Migration 4 adds the `instances` table and `checkpoints.handles`.
- `IEventStoreAdmin.RetireAsync(name)` and `deedbox retire <name>` retire a projection that no live instance registers ([DBX035](https://deedbox-docs.pages.dev/reference/errors/dbx035/) otherwise). A retired projection keeps its checkpoint, nothing applies it, and `RebuildAsync` brings it back. An instance that still registers it starts, and its health check reports degraded.

### Changed

- An inline projection switches from catch-up to inline only when no live instance can append its events without running it. Before, a new inline projection added during a rolling deploy missed the appends of instances of the old version.
- An instance that starts without a running inline projection, but can append its events, moves that projection back to catch-up from the current head. A rollback no longer makes an inline projection miss events.
- `IEventStoreAdmin.RetireAsync` has a default body, so an implementation of the interface written for 0.2 still compiles.

### Fixed

- A rebuild or snapshot job claimed by an instance that does not register the projection or stream type failed, even when another live instance could run it. That instance now leaves the job to one that can, and runs it, failing with the reason, only when no live instance can.

## [0.2.1] - 2026-09-25

### Fixed

- On SQL Server without `READ_COMMITTED_SNAPSHOT`, the async runner could skip events. A read that waited on an append in flight could resume past positions that the next append reused after a rollback, then return a later position, and the checkpoint moved past the skipped events. Every read by position now stops at the committed value of the position counter, read first in the same batch. Postgres and databases with `READ_COMMITTED_SNAPSHOT` were not affected. If an async projection on an affected database may have skipped events, rebuild it.

## [0.2.0] - 2026-09-24

### Added

- `Deedbox.QueueBox`: `Publish<TEvent>((e, pending) => new QueueBoxMessage(topic, payload) { Headers = ... })` builds the whole message for each event: its topic, payload and extra headers. Extra headers replace a default header of the same name; the defaults stay. Return null to skip an event. An invalid message, or an exception in the callback, fails the append with DBX032. The "Wire QueueBox" guide shows CloudEvents in structured and binary mode.

## [0.1.0] - 2026-09-24

The first release. It targets .NET 8 and .NET 10.

### Packages

- `Deedbox`: streams, projections, subscriptions, the background runner, personal data and the admin API.
- `Deedbox.Postgres` and `Deedbox.SqlServer`: the storage providers, with embedded schema migrations.
- `Deedbox.EntityFrameworkCore`: appends in a DbContext transaction, and projections that write through EF Core.
- `Deedbox.Testing`: Given/When/Then for deciders, the event-contract lockfile and the key provider compliance suite.
- `Deedbox.Keys.AzureKeyVault`: the master key in Azure Key Vault or Managed HSM.
- `Deedbox.QueueBox`: QueueBox outbox rows, written in the append transaction.
- `Deedbox.Cli`: the `deedbox` tool for schema scripts, status, rebuilds, erasure, key rewrap, tenant shredding and lockfile diffs.
- `Deedbox.Templates`: `dotnet new deedbox`.

### Added

- `Execute`, `Load` and `Append` on string stream IDs, with expected versions and conflict retries.
- A global position that is gapless and in commit order on both databases. The torture suites check it under concurrent appends, rollbacks, killed sessions and competing runners.
- Stored state per stream, with a state version that rebuilds stale state from events.
- Explicit stream and event names, aliases, JSON and typed upcasters, and start-up checks against the stored names.
- Inline and async projections, subscriptions, batch projections, in-place rebuilds, poison-event stalls with retries, and health checks.
- Metadata, causation and correlation, trace context capture, and shared-table tenancy.
- Crypto-shredding of `[PersonalData]` with a per-subject key under a per-tenant key and a master key. Key modes: database, environment and Azure Key Vault. Subject erasure, tenant shredding and stream deletion.
- `IEventStoreAdmin`, metrics, traces, and DBX error codes that link to their docs pages.
- Native `json` columns on SQL Server 2025 and Azure SQL, with `UseSqlServer(connectionString, sql => sql.NativeJson = true)`. Applying the schema converts existing `nvarchar(max)` columns.

[0.8.0]: https://github.com/alternayte/deedbox/releases/tag/v0.8.0
[0.7.0]: https://github.com/alternayte/deedbox/releases/tag/v0.7.0
[0.6.0]: https://github.com/alternayte/deedbox/releases/tag/v0.6.0
[0.5.0]: https://github.com/alternayte/deedbox/releases/tag/v0.5.0
[0.4.1]: https://github.com/alternayte/deedbox/releases/tag/v0.4.1
[0.4.0]: https://github.com/alternayte/deedbox/releases/tag/v0.4.0
[0.3.1]: https://github.com/alternayte/deedbox/releases/tag/v0.3.1
[0.3.0]: https://github.com/alternayte/deedbox/releases/tag/v0.3.0
[0.2.1]: https://github.com/alternayte/deedbox/releases/tag/v0.2.1
[0.2.0]: https://github.com/alternayte/deedbox/releases/tag/v0.2.0
[0.1.0]: https://github.com/alternayte/deedbox/releases/tag/v0.1.0
