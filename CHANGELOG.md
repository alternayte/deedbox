# Changelog

This file records every notable change to the Deedbox packages. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the versions follow [Semantic Versioning](https://semver.org/). In 0.x, only a minor release can break the public API, and its entry says how. The storage schema never breaks: each change ships a forward migration.

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

[0.2.0]: https://github.com/alternayte/deedbox/releases/tag/v0.2.0
[0.1.0]: https://github.com/alternayte/deedbox/releases/tag/v0.1.0
