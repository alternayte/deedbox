# Deedbox build progress

The design doc (SDD) is the source of truth. It is local only and never committed.

## Steps

- [x] 1. Repo scaffold: solution, packages, Directory.Build.props, net8.0 + net10.0, analyzers (nullable, AOT, trimming), package validation, GitHub Actions with Testcontainers, CONTRIBUTING stub.
- [x] 2. Schema: embedded migration scripts for both providers, schema manager with locks, schema_version startup check, CLI "schema script" and "schema apply".
- [x] 3. Write path: string stream IDs, position counter (option A), streams and events tables, Append / Load / Execute with expected versions, conflict retry, snapshots with state_version, transaction modes (neither, Dapper, EF Core with several DbContexts in one transaction).
- [x] 4. Torture suite v1: concurrent appends with rollbacks and long transactions; assert gapless commit-ordered positions and per-stream order. HUMAN GATE 1: API shape review and torture suite v1 results.
- [x] 5. Registry and evolution: naming convention, aliases, event_types table, startup name check, JSON upcasters and typed upcasters, lockfile in Deedbox.Testing, Given/When/Then helpers.
- [ ] 6. Inline projections (EF and ADO flavours), OnAppending hook, metadata context, causation/correlation, TraceParent capture, tenancy scoping.
- [ ] 7. Async runner: checkpoints, projections, subscriptions, type filtering, LISTEN/NOTIFY and backoff, multi-instance locking, poison handling, in-place rebuilds, health checks, jobs table.
- [ ] 8. Torture suite v2: projector kills, competing instances, sparse filters, idle query budget; every Anthology regression test.
- [ ] 9. Personal data: [DataSubject] / [PersonalData], contract-customization encryption, key hierarchy, Database / Environment / Azure Key Vault key modes, subject_streams, erasure job, SubjectErased, stream deletion, startup safety rules, key provider compliance suite. HUMAN GATE 2: security review of crypto, key handling and erasure.
- [ ] 10. Operations: remaining CLI commands, IEventStoreAdmin, metrics, traces, DBX error codes with docs URLs.
- [ ] 11. Benchmarks: the matrix, nightly job, published results.
- [ ] 12. Deedbox.QueueBox package against the confirmed QueueBox contract.
- [ ] 13. Docs: Starlight site, MarkdownSnippets, Vale, error catalogue, README, dotnet new template, llms.txt, agent skill, Cloudflare deploy.
- [ ] 14. Anthology migration: data migration script, module rewrites, remove replaced kernel code, move event-store tests into Deedbox.
- [ ] 15. Release prep: changelog, package metadata, NuGet prefix check, security policy, full nightly run green. HUMAN GATE 3: release sign-off for 0.1.0.

## Decisions

- Step 1: Sibling packages share internals through InternalsVisibleTo and depend on each other's exact version. This keeps the provider SPI out of the public API; the family ships in lockstep.
- Step 1: Public API is tracked with PublicApiAnalyzers. PublicAPI.Shipped.txt is the previous release; a change without an Unshipped entry fails the build.
- Step 1: Package validation is on. The baseline version is set once 0.1.0 ships, because no earlier package exists.
- Step 1: Versions are 0.1.0-alpha until release sign-off.
- Step 1: Deedbox.Templates is not packable until step 13, because it has no template content yet.
- Step 1: CI runs `just check` in one job: both frameworks, both providers. The PR smoke / nightly split arrives with the nightly job in step 11.
- Step 1: Tests use xUnit v3 and Testcontainers (postgres:17-alpine, mssql/server:2022-latest). DEEDBOX_TEST_POSTGRES / DEEDBOX_TEST_SQLSERVER point tests at existing servers.
- Step 2: Migration 0001 holds every table in the SDD. Until 0.1.0 ships, 0001 may still change; after release, migrations are forward-only.
- Step 2: Key lengths are the same on both providers: tenant_id 100, stream_id 200, subject_id 100, type names 200 characters. SQL Server index key limits set them; Postgres enforces them in code so behaviour matches.
- Step 2: A database schema newer than the build passes the start-up check. Migrations are additive, and a rolling deploy runs old pods against a new schema.
- Step 2: `deedbox schema script --from n` takes the version the database has now and prints migrations n+1 to latest.
- Step 2: The SDD's `EventStoreSchema.Script(...)` is `PostgresSchema.Script(...)` and `SqlServerSchema.Script(...)`. The core has no provider enum to pass. Gate 1 confirms.
- Step 2: Error docs URLs use https://deedbox.dev/errors/dbxNNN until the docs domain is chosen (open item).
- Step 2: No ConfigureAwait (CA2007 off). Deedbox targets hosts without a synchronization context.
- Step 2: `just api` records new public API symbols from RS0016 build errors into PublicAPI.Unshipped.txt.
- Step 3: A write locks the stream identity before it reads the stream. SQL Server uses UPDLOCK + HOLDLOCK on the row or its key range. Postgres uses a transaction advisory lock on a hash of (tenant, stream) plus FOR UPDATE. Writers of one stream queue up on both providers, including when they create it.
- Step 3: Execute reads the stream under that lock and decides on locked state, instead of load-then-check. This removes a round trip and wasted decisions. Conflict retries (default 3, no delay) stay as a safety net; the torture suite shows whether they ever fire.
- Step 3: `streams.state_at` is added: the stream version the stored state reflects. "Every N events" snapshots need it.
- Step 3: JSON defaults are JsonSerializerDefaults.Web (camelCase). Enums stay numbers, the System.Text.Json default, because the non-generic string enum converter is not AOT-safe. ConfigureJson changes this.
- Step 3: `ExpectedVersion.Exact(0)` equals NoStream, so `Exact(version)` from a Load of a missing stream works.
- Step 3: Stream IDs are 1 to 200 characters with no leading or trailing white space. SQL Server ignores trailing spaces in comparisons; rejecting them keeps both providers identical.
- Step 3: `StreamId.Deterministic(ns, parts)` joins parts with ':' and uses RFC UUIDv5. This matches Anthology's StreamId.For, so migrated IDs still resolve.
- Step 3: SQL Server appends pass all events as one JSON parameter to OPENJSON. This needs SQL Server 2016+ at compatibility level 130+.
- Step 3: The events metadata column holds "{}" until step 6 adds metadata.
- Step 3: Transaction modes are `store.UseTransaction(dbTransaction)` for Dapper/ADO.NET and `store.UseDbContext(context, params others)` for EF Core. The SDD names neither; gate 1 confirms.
- Step 3: In EF mode, when the caller owns the transaction, Deedbox enlists the other contexts and leaves them enlisted. The caller completes the transaction.
- Step 3: IEventStore is registered as scoped, for the tenant and metadata context in step 6.
- Step 4: The torture suite also runs on SQL Server with READ_COMMITTED_SNAPSHOT on (a second database, deedbox_rcsi), because Azure SQL enables it by default and readers then skip locked rows instead of waiting.
- Step 4: Torture tests carry the trait Category=Torture. DEEDBOX_TORTURE_SCALE multiplies the work; DEEDBOX_TORTURE_SEED replays a run's operation choices.
- Step 5: `EventsNestedIn(typeof(CartEvents))` takes a Type. C# does not allow a static class as a type argument, so the SDD's `EventsNestedIn<CartEvents>()` cannot compile.
- Step 5: Event registration overloads are `Event<T>()`, `Event<T>(name)`, `Event<T>(e => ...)` and `Event<T>(version, up => ...)`. The builder has Name, Alias, From (JSON step) and Upcast<TOld, TNew> (typed step). This matches the SDD snippets.
- Step 5: Every version from 1 to the current one needs exactly one upcast step. A typed step must be the last one, because its output is the current CLR type.
- Step 5: Appends write new (stream type, event type, version) rows to event_types in the append transaction, before the counter. A process cache skips known rows. The cache fills from the start-up read and from commits Deedbox makes itself, never from a caller's transaction, which may still roll back. On SQL Server the event_types key uses IGNORE_DUP_KEY, so concurrent first writers both succeed.
- Step 5: The start-up check fails on stored names with no mapping, on events registered under another stream type, and on stored versions newer than the build. It lists every problem in one message. A newer stored version fails because this build cannot read it.
- Step 5: The lockfile uses its own shape walker over System.Text.Json contracts instead of JsonSchemaExporter. It prints the SDD's format (int32, date-time) and needs no System.Text.Json 9 package on net8.0.
- Step 5: Lockfile rules: removing a name or alias breaks, a lower version breaks, and at the same version, removing a property, changing its type, making it non-nullable or adding a non-nullable property breaks. New events, aliases, versions and nullable properties rewrite the file locally. Under CI (CI=true), any difference fails. The lockfile path is relative to the calling test file.
- Step 5: Given/When/Then compares events by type and JSON, so records that hold collections compare by content.
- Step 5: Envelopes carry the current event name and version, after upcasting, not the stored alias or old version.

## Gate reports

### HUMAN GATE 1: API shape and torture suite v1

Status: approved on 2026-09-24. Every item above stands as written.

#### Public API to confirm

The public surface is in `src/*/PublicAPI.Unshipped.txt`. These names differ from the SDD or fill a gap in it:

1. `PostgresSchema.Script(from, schema)` and `SqlServerSchema.Script(from, schema)` replace the SDD's `EventStoreSchema.Script(...)`. The core has no provider type to pass, so each provider owns its script.
2. `store.UseTransaction(dbTransaction)` is the Dapper / ADO.NET mode. `store.UseDbContext(context, params others)` is the EF Core mode. Both return a bound `IEventStore`.
3. `Execute<TState>(id, decide)` locks the stream row (or the missing row's slot) when it loads, and decides on locked state. The SDD describes load-then-check with retries. Retries (`ExecuteRetries`, default 3, no delay) remain but only cover a lost race that the lock already prevents.
4. `ExpectedVersion` is `Any`, `NoStream` or `Exact(n)`. `Exact(0)` equals `NoStream`.
5. `LoadResult<TState>` is a record struct that deconstructs to `(state, version)`. `AppendResult` and `ExecuteResult<TState>` are records.
6. `EventEnvelope` has no public constructor. Metadata properties arrive in step 6.
7. `SnapshotPolicy.EveryAppend` (default), `Every(n)` and `Never`, set per stream with `.Snapshots(...)`; `.StateVersion(n)` sets the state version.
8. `DeedboxException.Code` holds a DBX code; each message ends with `https://deedbox.dev/errors/dbxNNN`. The docs domain is still an open item.
9. JSON: `ConfigureJson(...)` and `UseJsonContext(...)`. Defaults are camelCase; enums are numbers.
10. `IEventStore` is scoped.

#### Torture suite v1 results

`tests/Deedbox.Tests/Torture/AppendTortureTests.cs` runs on Postgres 17, SQL Server 2022 (locking read committed) and SQL Server 2022 with read-committed snapshot.

- Each run: 16 writers x 40 operations over 24 shared streams, and 3 observers that tail the global order. Operations mix owned Append and Execute, Execute whose decision throws, caller-transaction Append and Execute with 25% rollbacks, and long transactions (50-250 ms) that hold the position counter and then commit or roll back.
- Assertions: positions are exactly 1..N and the counter is N; the store holds exactly the committed events at the positions their envelopes reported; no rolled-back event exists; each append's events are contiguous; each stream's versions run 1..n in global order; every observer saw the final order grow with no gap; after each caller commit, every lower position is already committed; each stream's stored state matches its events.
- Two fixed scenarios: a long transaction makes a later append wait; its rollback gives the waiting append position 1, and its commit orders the waiting append after it.
- Local run (Apple silicon; SQL Server runs under x64 emulation), both frameworks: all pass. About 520-545 committed and 80-105 discarded appends per run. Postgres ran each run in about 8 s, SQL Server in about 18-20 s. Long transactions hold the counter, so these numbers are not throughput benchmarks; step 11 measures throughput.
- Mutation check: a Postgres sequence in place of the counter (the design behind the Marten skip bugs) fails all three Postgres torture tests. Observers report gaps such as "position 13 right after 11".

#### Decisions to confirm

Every decision under "Decisions" for steps 1-4. The ones with the most weight:

- The write lock on the stream identity (advisory lock + FOR UPDATE on Postgres; UPDLOCK + HOLDLOCK on SQL Server).
- Exact sibling-package version pins with shared internals.
- Migration 0001 stays editable until 0.1.0 ships.
- The added `streams.state_at` column.
