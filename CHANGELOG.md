# Changelog

This file records every notable change to the Deedbox packages. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the versions follow [Semantic Versioning](https://semver.org/). In 0.x, only a minor release can break the public API, and its entry says how. The storage schema never breaks: each change ships a forward migration.

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

[0.3.1]: https://github.com/alternayte/deedbox/releases/tag/v0.3.1
[0.3.0]: https://github.com/alternayte/deedbox/releases/tag/v0.3.0
[0.2.1]: https://github.com/alternayte/deedbox/releases/tag/v0.2.1
[0.2.0]: https://github.com/alternayte/deedbox/releases/tag/v0.2.0
[0.1.0]: https://github.com/alternayte/deedbox/releases/tag/v0.1.0
