# Deedbox build progress

The design doc (SDD) is the source of truth. It is local only and never committed.

## Steps

- [x] 1. Repo scaffold: solution, packages, Directory.Build.props, net8.0 + net10.0, analyzers (nullable, AOT, trimming), package validation, GitHub Actions with Testcontainers, CONTRIBUTING stub.
- [x] 2. Schema: embedded migration scripts for both providers, schema manager with locks, schema_version startup check, CLI "schema script" and "schema apply".
- [x] 3. Write path: string stream IDs, position counter (option A), streams and events tables, Append / Load / Execute with expected versions, conflict retry, snapshots with state_version, transaction modes (neither, Dapper, EF Core with several DbContexts in one transaction).
- [x] 4. Torture suite v1: concurrent appends with rollbacks and long transactions; assert gapless commit-ordered positions and per-stream order. HUMAN GATE 1: API shape review and torture suite v1 results.
- [x] 5. Registry and evolution: naming convention, aliases, event_types table, startup name check, JSON upcasters and typed upcasters, lockfile in Deedbox.Testing, Given/When/Then helpers.
- [x] 6. Inline projections (EF and ADO flavours), OnAppending hook, metadata context, causation/correlation, TraceParent capture, tenancy scoping.
- [x] 7. Async runner: checkpoints, projections, subscriptions, type filtering, LISTEN/NOTIFY and backoff, multi-instance locking, poison handling, in-place rebuilds, health checks, jobs table.
- [x] 8. Torture suite v2: projector kills, competing instances, sparse filters, idle query budget; every Anthology regression test.
- [x] 9. Personal data: [DataSubject] / [PersonalData], contract-customization encryption, key hierarchy, Database / Environment / Azure Key Vault key modes, subject_streams, erasure job, SubjectErased, stream deletion, startup safety rules, key provider compliance suite. HUMAN GATE 2: security review of crypto, key handling and erasure.
- [x] 10. Operations: remaining CLI commands, IEventStoreAdmin, metrics, traces, DBX error codes with docs URLs.
- [x] 11. Benchmarks: the matrix, nightly job, published results.
- [x] 12. Deedbox.QueueBox package against the confirmed QueueBox contract.
- [x] 13. Docs: Starlight site, MarkdownSnippets, Vale, error catalogue, README, dotnet new template, llms.txt, agent skill, Cloudflare deploy.
- [x] 14. Anthology migration: data migration script, module rewrites, remove replaced kernel code, move event-store tests into Deedbox.
- [x] 15. Release prep: changelog, package metadata, NuGet prefix check, security policy, full nightly run green. HUMAN GATE 3: release sign-off for 0.1.0.

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
- Step 2: Error docs URLs use https://deedbox-docs.pages.dev/reference/errors/dbxNNN/ until the docs domain is chosen (open item).
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
- Step 6: `app.UseDeedboxMetadata(http => ...)` is not built. It needs ASP.NET Core in the core package, which the SDD limits to Microsoft.Extensions abstractions. The core has a scoped `DeedboxContext` (TenantId, Metadata) that app middleware sets instead. Gate 2 confirms; an ASP.NET Core helper would be a new package.
- Step 6: Metadata fields are strings: CorrelationId, CausationId, Actor, TraceParent, plus string Headers. The stored JSON leaves out absent fields and empty headers. `store.WithMetadata(m => ...)` overrides metadata per append. TraceParent comes from Activity.Current (W3C) when the metadata has none.
- Step 6: `DeedboxContext.CausedBy(envelope)` sets the tenant, correlation ID, actor, and CausationId = the event's ID. The async runner uses it for every handler scope.
- Step 6: Projection instances are singletons, created once from the root container. Handlers keep no state between events; scoped services come from ctx.Services.
- Step 6: Order inside an append: stream row, inline projections in registration order, appending hooks, SaveChanges (UseDbContext contexts, then projection contexts), event_types, then the counter and the events.
- Step 6: An EF projection reuses a UseDbContext context of its type. Otherwise Deedbox creates one from DI on the append's connection and transaction, saves it before the counter, and disposes it afterwards.
- Step 6: Inline handlers see GlobalPosition as null, because the position is assigned after them.
- Step 6: Tenant IDs are at most 100 characters with no leading or trailing white space (DBX022).
- Step 6: Known limit: a caller transaction that appends to two streams holds the counter from its first append while it waits for the second stream's lock. A concurrent append that holds that stream lock waits for the counter. The database breaks this deadlock by aborting one transaction; nothing is lost or reordered. Gate 2 decides whether to document it or change the design.
- Step 7: The runner has one loop per async projection, subscription and inline projection, plus a jobs loop. A loop runs a batch only while it holds its checkpoint row (Postgres FOR NO KEY UPDATE SKIP LOCKED; SQL Server UPDLOCK + READPAST), so instances share work with no leases or setup.
- Step 7: A batch scans every event after the checkpoint and reads payloads only for the types the consumer handles (a SQL CASE). The checkpoint moves to the last scanned position. The runner never assumes positions are contiguous, because stream deletion (step 9) leaves gaps.
- Step 7: An append that touches an inline projection's types first takes that projection's gate lock shared (a Postgres transaction advisory lock; a SQL Server sp_getapplock), then reads its status in a second statement. It applies only projections that are running. Rebuild start, cut-over and a run-mode change take the gate exclusively, and cut-over also locks the counter, so no event is applied both inline and by the catch-up. A first version put this lock on the checkpoint row (FOR KEY SHARE against FOR UPDATE). On Postgres, a non-key UPDATE by the rebuild job let an append read the old status, and an event was applied twice. CI caught it; the gate lock replaces it.
- Step 7: Inline rebuild cut-over happens when the rest fits in one batch. If appends outpace the catch-up for 20 polls, it cuts over anyway; appends then wait while it applies the rest. An async rebuild is running again when a batch reads fewer events than the batch size.
- Step 7: Poison handling: retries start at RetryDelay and double (cap 5 minutes). The loop reads one event at a time from the failed batch to find the failing event. After HandlerRetries it stalls with a JSON error (reason poison, event, stream, version, exception). A restart retries a poison stall once. A subscription commits its progress up to the failing event.
- Step 7: A projection whose run mode changed stalls with reason mode_changed. It is never retried automatically; a rebuild clears it.
- Step 7: Jobs (rebuild, skip) each run in one transaction with their row locked. A failure is recorded on the row; rows stay as the audit trail. Subscriptions cannot be rebuilt. Enqueueing is internal until step 10 adds IEventStoreAdmin and the CLI commands.
- Step 7: The core now references Microsoft.Extensions.Diagnostics.HealthChecks, not only abstractions, because IHealthChecksBuilder lives there. The API is `AddDeedboxHealthChecks()` on IHealthChecksBuilder.
- Step 7: Postgres wakes runners with migration 0002: a statement-level trigger calls pg_notify('dbx_<schema>'). Schema names are now at most 50 characters, because a channel name is at most 63.
- Step 7: Runner defaults: batch 500; polls from 50 ms up to 5 s; 5 retries from 1 s; health stall after 10 minutes. `Runner(o => o.Enabled = false)` turns the runner off in a process.
- Step 7: The SDD's IBatchProjection is an abstract `BatchProjection` class with `Handles<T>()`. An interface would need its own way to declare event types.
- Step 7: Each handler call starts an Activity "deedbox.handle <consumer>" whose parent is the event's stored TraceParent. Step 10 adds the other spans and metrics.
- Step 8: Torture v2 is real chaos: it kills runner sessions (pg_terminate_backend, or KILL by application name), restarts runner hosts, runs three competing instances, fails 3% of handler first attempts, and uses a 3% sparse event type. Each projection must apply every event exactly once, enforced by a primary key on the event ID. The subscription must deliver every event at least once.
- Step 8: Idle query budget: 30 statements per minute per consumer, plus 30 for the jobs loop, with default polling. The local measurement is 48 statements in 30 s for 4 consumers, against a budget of 75. This settles the budget open item; the throughput threshold stays open until step 11.
- Step 8: Anthology regressions: new named tests cover a transaction that starts first but appends last, a projection class rename, one NOTIFY per append, and a duplicate mapping. Existing tests that already pin the other lessons carry a Regression trait.
- Step 9: Every encrypted blob is AES-256-GCM with a random 12-byte nonce and a 16-byte tag. Its associated data names what it is: subject key (tenant, key ID), field (key ID), stored state (tenant, stream), master wrap (master key version). A blob copied to another row does not verify.
- Step 9: Key hierarchy as the SDD describes. A tenant key is created in its own committed transaction and cached for the life of the process. Subject keys are cached for one operation only. Appends share-lock the subject key row, so an erasure waits for open appends and their data becomes unreadable with the rest.
- Step 9: A field is stored as {"$enc": "v1:<key ID>:<nonce>:<ciphertext>"}. Key IDs are 32 lower-case hex characters, so SQL Server's case-insensitive key_id collation cannot confuse two IDs. Migration 0003 adds a unique (tenant_id, key_id) index.
- Step 9: [PersonalData] is read from public top-level properties with reflection. Registration generics carry the trimming annotation. Nested types are not encrypted. EventsNestedIn is marked RequiresUnreferencedCode.
- Step 9: The stored state of a stream type that has any [PersonalData] event is sealed with the tenant key. The SDD does not say this, but without it state is plain personal data in the database, and the environment and Key Vault modes could not protect database-only leaks.
- Step 9: Erasing a subject deletes the key and clears the stored state of every stream that holds their data, in one transaction. Nothing reads their data after the call returns; loads rebuild state from redacted events. The queued job then appends SubjectErased and stores the rebuilt state, one stream per transaction. Removing each subject-stream pair first makes a rerun a no-op.
- Step 9: Jobs are idempotent. A job's own rejection (a DeedboxException or JobRejected) fails it at once; any other exception, such as a killed session, is retried up to 10 times. The erasure torture test found that a killed session failed an erasure job for good.
- Step 9: `DeleteStream(streamId)` is on IEventStore. A deleted stream fails Load and Append with DBX028. Deleting a missing or already-deleted stream does nothing. Deleted events leave gaps in global positions; the runner never assumed there were none.
- Step 9: Built-in events SubjectErased and StreamDeleted are stored as deedbox.subject_erased and deedbox.stream_deleted. They use a fixed JSON contract (web defaults) whatever the app's JSON settings. Apps cannot append them (DBX031).
- Step 9: Key mode API: `.Keys(k => k.StoreInDatabase() / FromEnvironment(var) / FromKeyRing(ring) / Use(provider))` plus `.RedactWith(placeholder)`. The database master key is master_keys row (empty tenant, version 0). Re-wrapping to another mode deletes it. Start-up logs a warning in database mode. Re-wrap is internal until step 10 adds `deedbox keys rewrap`.
- Step 9: IMasterKeyProvider has three members: KeyVersion, WrapAsync, UnwrapAsync. KeyVersion carries the provider prefix (env:, database:, azure:), so a row names the mode that wrapped it.
- Step 9: Azure Key Vault uses RSA-OAEP-256 by default. A CryptographyClient overload covers Managed HSM (A256KW) and custom clients. Keys wrapped by another key version need the version-client factory.
- Step 9: `[PersonalData(Subject = nameof(AuthorId))]` does not compile on a positional record parameter; use `Subject = "AuthorId"`. The docs step shows this.
- Step 9: The lockfile marks personal fields as pd(subject). Removing a marker breaks at any version change.
- Step 10: Tenant shredding (added at gate 2): `IEventStoreAdmin.ShredTenantAsync` and `deedbox tenant shred <tenant> --yes`. The tenant's key rows become tombstones: no key material, but the versions stay so none is reused. Its subject keys, subject pairs and stored state are deleted. Before an instance creates a subject key, it re-reads the tenant's key rows, so a tenant key still cached on that instance is never used for new data.
- Step 10: The CLI runs database-only operations itself: status, keys rewrap, tenant shred, and the key deletion that starts an erasure. It queues jobs for the app's runner for work that needs the app's registrations: rebuild, skip, erase, snapshots rebuild. `--wait` follows a job to the end.
- Step 10: `deedbox keys rewrap --from <m> --to <m>` takes database, env:<VARIABLE> or azure:<key URL>; Azure uses DefaultAzureCredential. The CLI therefore references Deedbox.Keys.AzureKeyVault and Azure.Identity, beyond the SDD's "Core, both providers".
- Step 10: `deedbox lockfile diff <old> <new>` compares two lockfile files, such as main's and the branch's. The CLI cannot load the app's registrations. It exits 1 when a change breaks stored events.
- Step 10: IEventStoreAdmin covers status, job lookup, rebuild, skip, erase (with an explicit tenant), snapshot rebuild, key re-wrap and tenant shred. Status lists every checkpoint row, whether or not this app registers it.
- Step 10: Metrics are on the Meter "Deedbox". Append: duration, events, conflicts, Execute retries, counter duration. Consumers: lag, lag in seconds, status (gauges), batch duration, failures, stalls. Also jobs, erased streams, decrypts and redactions. Spans on the ActivitySource "Deedbox": append, execute, load, delete_stream, batch, handle, job, erase_stream. The lag gauge reads the head only after a full batch, so an idle runner sends no extra statements.
- Step 10: `Errors.Titles` is the error catalogue: one title per DBX code. A test fails if a code has no title; the docs step builds one page per entry.
- Step 11: Benchmarks are a custom harness (`bench/Deedbox.Benchmarks`), not BenchmarkDotNet, because the matrix measures concurrent throughput. Each writer appends to its own stream, so the counter is the only contention. `just bench` runs it; the nightly workflow runs it with the torture suite at scale 5.
- Step 11: Throughput regression threshold: 30% below `bench/baseline.json` per cell. The baseline comes from the first nightly run on a GitHub-hosted runner. This settles the open item; revisit the threshold if nightly noise exceeds it.
- Step 11: Published results are in `bench/results.md`; the docs site (step 13) includes them. Peak on the CI runner: about 1,170 appends/s on Postgres, 700 on SQL Server.
- Step 11: CI on pushes still runs the full suite on both frameworks, stricter than the SDD's PR smoke subset on net8.0, because the whole suite takes about 2.5 minutes.
- Step 12: The QueueBox contract is QueueBox's published integration contract: `docs/integration.md` in alternayte/queuebox, which its own tests execute. It is one INSERT into the `outbox` table in the business transaction; only topic and payload are required. This settles the SDD open item. The tests create the table from QueueBox's migrations V1 and V9.
- Step 12: `UseQueueBox(q => q.Publish<T>(topic[, payload]))` writes one row per published event from an OnAppending hook. Row id = event ID, so a destination's X-Message-Id is an idempotency key. key = stream ID, so one stream's messages keep their order. aggregate_type = stream type. Headers carry X-Correlation-Id (QueueBox's correlation header), traceparent, causation and the event's identity.
- Step 12: Publishing an event with [PersonalData] requires a payload mapping (DBX032). Personal data never reaches the outbox in plain text by default. SubjectErased and StreamDeleted can be published like any event, which is how erasure reaches downstream systems.
- Step 12: `UseTable(name, schema)` and `UseColumns(...)` follow QueueBox's custom table and column mapping. Identifiers must be plain names and are quoted for the dialect.
- Step 13: The site is Starlight in `site/`. Code samples live in the compiled, tested project `site/snippets/Deedbox.Snippets`; its tests run the first-stream tutorial and the inline EF Core projection against Postgres. MarkdownSnippets does not process .mdx, and MDX rejects HTML comments. So each snippet fills a Markdown partial in `site/src/snippets/`, which MDX pages import, including inside synced tabs. `checks/docs-snippets-current.sh` fails `just check` when a partial or the README is stale.
- Step 13: Vale runs a Deedbox style (`site/styles/Deedbox`): second person, present tense, no marketing words, sentences under 35 words, and each page opening with what the reader achieves. `scripts/vale.sh` downloads a pinned Vale into artifacts/tools.
- Step 13: Error URLs are https://deedbox-docs.pages.dev/reference/errors/dbxNNN/, with one page per code. A test in Deedbox.Tests fails when a code has no page with its catalogue title.
- Step 13: `dotnet new deedbox [--database postgres|sqlserver]` makes a web app with one stream, an inline projection, schema setup, and decider and lockfile tests. `just template` (part of `just check`) generates both variants outside the repo against the packed packages, then builds and tests them. A manual run of the Postgres variant served add-item, checkout and load against a real database.
- Step 13: The Starlight Mermaid add-on is not used; the two SDD diagrams are numbered lists. The comparison page states only what its linked sources support.
- Step 13: `.github/workflows/docs.yml` checks snippets, runs Vale, builds the site, and deploys with wrangler to Cloudflare Pages project `deedbox-docs`, one alias per branch, with a PR preview comment. It deploys only when the CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID secrets exist; CLOUDFLARE_BEACON_TOKEN turns on Web Analytics.
- Step 13: The agent skill is `skills/deedbox/SKILL.md`; llms.txt comes from starlight-llms-txt.
- Step 13: The site is live at https://deedbox-docs.pages.dev (Cloudflare Pages project `deedbox-docs`, account of the local wrangler login, created on classic Pages with `--force` because wrangler 4 now routes Pages commands to Workers). deedbox.dev is not registered, so every docs link (error URLs, README, llms.txt, skill) points at the pages.dev address. Moving to a domain means replacing that one URL.
- Step 13: CLOUDFLARE_ACCOUNT_ID is set as a repo secret. CI deploys on each push once the user adds CLOUDFLARE_API_TOKEN (a token with Cloudflare Pages: Edit); the local OAuth login cannot create API tokens.
- Step 14 gap 1 (fixed): a new inline projection on a store that already held events started as running at position 0, so it never saw the earlier events. The docs said a renamed projection starts again from the first event. Now a new inline checkpoint on a non-empty store starts as rebuilding; the runner applies the earlier events and cuts over as in a rebuild. Anthology hit this: its three projections are new names on migrated events. Test: A_new_inline_projection_on_a_store_with_events_applies_the_earlier_events_then_runs_inline.
- Step 14 gap 2 (fixed): IEventStoreAdmin queued a rebuild, skip or snapshot job for any name, and the job failed later. Anthology's admin API must answer 422 for an unknown stream type at once. Now RebuildAsync and SkipAsync throw DBX033 for an unregistered name, and RebuildSnapshotsAsync throws DBX009. The CLI has no registry, so its jobs still fail in the runner. The snapshot test changed from "job failed" to "throws DBX009".
- Step 14 gap 3 (fixed): a QueueBox payload mapping gets (event, PendingEvent), and PendingEvent had no stream ID, so an integration event could not carry its aggregate ID. Anthology's ItemFinished integration event does. PendingEvent.StreamId is new public API; the QueueBox personal-data test maps it into the payload.
- Step 14 gap 4 (fixed): UsePostgres and UseSqlServer took the connection string when the app registered Deedbox. WebApplicationFactory applies its configuration after that, so Anthology's tests connected to the appsettings database. The provider is now created when the runtime is first resolved, and `UsePostgres(Func<IServiceProvider, string>)` and `UseSqlServer(Func<IServiceProvider, string>)` read the connection string from the app's services. Configuration errors still fail in AddDeedbox; only the provider moved.
- Step 14: Anthology's migration is branch `deedbox-migration` in alternayte/anthology. It takes Deedbox from a local feed (`packages/` plus `nuget.config` with package source mapping), packed as 0.1.0-alpha.anthology.N. Reverting means deleting both and pointing at nuget.org once 0.1.0 ships.
- Step 14: The data migration is one SQL script in Anthology (`scripts/migrate-es-to-deedbox.sql`), run once after the Deedbox schema exists. Stream IDs become their standard GUID text, which is what StreamId.Deterministic gives for Anthology's UUIDv5 inputs; names split into event_type and event_version; positions renumber from 1 in old order; state is left empty and rebuilt on load; checkpoints are deleted and the read models truncated, so gap 1's behaviour rebuilds all three at the next start.
- Step 14: "Anthology's full test suite must pass on the migrated data" is literal. The test fixture restores a pg_dump that the pre-Deedbox Anthology wrote through its API (with a v1 ItemWanted, two ratings under the old rerated name and a position gap), applies the Deedbox schema, runs the migration script, then waits for the rebuilds. All 149 tests pass on it; MigratedDataTests checks the rebuilt read models and writes to the migrated streams.
- Step 14: Handlers call `Execute`; Anthology's `Result` rejection becomes "decide nothing and return the error" through one kernel helper. TransactionDecorator is removed entirely, not mostly: Deedbox owns the write transaction and the validation decorator stays.
- Step 14: The projections need the user and the title, which most events do not hold. Handlers set them as metadata headers (userId, titleId) with Actor user:<id>; the script moves the old metadata into that shape. All three projections run inline, which fixes the inline-plus-async double registration.
- Step 14: Anthology's es.outbox had a translator for ItemFinished and no reader or publisher. QueueBox owns and migrates its own outbox table, so wiring Deedbox.QueueBox means running the QueueBox sidecar. The unused integration event is removed; Anthology wires QueueBox when a consumer exists.
- Step 14: The admin API keeps its routes on IEventStoreAdmin, with stored projection names (diary, library, lists) instead of class names. The single-stream snapshot rebuild is removed: Deedbox rebuilds a stale snapshot on load through state_version, and per-type rebuilds remain. A generic `/admin/jobs/{id}` reports projection rebuild jobs.
- Step 14: Build-time OpenAPI generation starts the host, and Deedbox's start-up check needs the database. Anthology removes hosted services under GetDocument.Insider, as ASP.NET documents; known-limits states it.
- Step 14: The six Anthology event-store test files are gone. Each scenario maps to an existing Deedbox test: appends, conflicts, missing-stream loads and state round trips (Streams), upcaster chains, current-version reads and unknown stored names (Evolution), evolver and rebuilder registration (DBX009 tests and the new admin validation test), snapshot rebuild jobs (AdminTests), catch-up from a checkpoint and default checkpoints (RunnerTests), metadata headers (MetadataTests), and the xid guard (Regressions). No scenario was uncovered, so none was added beyond the gap tests.
- Step 15: Every package ships the repository README and links the changelog as its release notes. The README's one relative link now points at the docs site.
- Step 15: The whole 0.1.0 API moves to PublicAPI.Shipped.txt now, because gate 3 signs off that surface. Later changes land in Unshipped again. Package validation gets its baseline after 0.1.0 is on nuget.org.
- Step 15: The template's projects referenced the literal 0.1.0-alpha, so a 0.1.0 template would have referenced unpublished packages. The template now holds DEEDBOX_VERSION, and the pack stamps in the package version.
- Step 15: `.github/workflows/release.yml` runs on a v* tag. It fails unless the tag matches VersionPrefix, every Unshipped file is empty, and the changelog has a dated entry. Then it runs `just check`, packs without the suffix, pushes to nuget.org with NUGET_API_KEY, and creates the GitHub release with the changelog section.
- Step 15: Private vulnerability reporting is on for the repository. SECURITY.md and CODE_OF_CONDUCT.md route reports through it; no personal address is published.
- Step 15: The first gate run failed once on net8.0: a metadata test captured a trace context it did not set. DiagnosticsTests registers process-wide listeners, which make Deedbox's append span current in tests that run at the same time. Recording the append span is intended, because async handler spans continue its trace (DiagnosticsTests checks this). DiagnosticsTests now runs in a collection without parallelization.
- Gate 3: Native json is an option of the SQL Server provider, `UseSqlServer(cs, sql => sql.NativeJson = true)`, not a numbered migration. Migration 0001 still creates nvarchar(max). A storage batch runs after the migrations on every apply, when the option is on, and converts each of the six JSON columns that is not json yet. So new stores and existing stores take the same path, and the schema version does not depend on the option. It stops before any change on a server without the json type.
- Gate 3: The runtime SQL is the same for both column types: parameters stay nvarchar, SQL Server converts them on write, and SqlClient 5.2 reads json as text. No statement compares, sorts or groups a JSON column. The server stores JSON without insignificant whitespace, which nothing in Deedbox depends on.
- Gate 3: With the option on, start-up fails with DBX034 when the server has no json type or a column is not converted, and names the fix. Without the option, json columns work too; nothing checks.
- Gate 3: `SqlServerSchema.Script` gains `nativeJson`, and the CLI gains `--native-json` for schema script and apply (Postgres rejects it). The new members are recorded as shipped, because they are part of 0.1.0.
- Gate 3: `just test` runs every SQL Server test a second time on SQL Server 2025 with native json (DEEDBOX_TEST_SQLSERVER_NATIVE_JSON=1). The first pass stays on SQL Server 2022 with nvarchar(max). The conversion test converts a store that already holds events on 2025, and expects DBX034 on 2022.
- Gate 3: The native json pass exposed a race in the torture suites' session killer: a session could end between the list and its KILL, and the batch failed. Each KILL now ignores a session that is already gone.
- Gate 3: One local gate run failed the append torture test in the native json pass. Both frameworks ran at once, each against an emulated SQL Server 2025 container, at 2 commits/s, and a writer's SqlException counted as a violation. Alone, the test passes in 39 s; both frameworks at once passed on a rerun. The pass now runs one framework at a time and starts no Postgres. The torture test logs each violation in full, so the next failure shows its cause.
- Gate 3: The release workflow publishes through nuget.org trusted publishing (GitHub OIDC, NuGet/login) instead of a stored NUGET_API_KEY. The temporary key lasts one hour and exists only in that run.

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
8. `DeedboxException.Code` holds a DBX code; each message ends with `https://deedbox-docs.pages.dev/reference/errors/dbxNNN/`. The docs domain is still an open item.
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

### HUMAN GATE 2: security review of crypto, key handling and erasure

Status: approved on 2026-09-24, with one addition: tenant shredding goes into step 10. The two open items got no answer, so the options that add no code apply: the docs describe the multi-stream deadlock, and the docs show a middleware one-liner that sets `DeedboxContext`.

#### What to review

| Area | Code |
| --- | --- |
| AES-GCM, associated data, blob formats | `src/Deedbox/PersonalData/Crypto.cs` |
| Master key modes (key ring, database) | `src/Deedbox/PersonalData/MasterKeys.cs` |
| Azure Key Vault | `src/Deedbox.Keys.AzureKeyVault/AzureKeyVaultMasterKey.cs` |
| Tenant keys, subject keys, re-wrap | `src/Deedbox/PersonalData/KeyRing.cs` |
| Field encryption, reveal, redaction | `src/Deedbox/PersonalData/Fields.cs`, `EventDecoding.cs` |
| Erasure, stream deletion | `src/Deedbox/PersonalData/SubjectErasure.cs`, `EventStore.EraseFromStream`, `EventStore.DeleteStream`, `Runner/Jobs.cs` |
| Key SQL (share locks, state clearing) | `ReadSubjectKey`, `DeleteSubjectKey` in both providers |
| Start-up safety rules | `PersonalFields.Map`, `EventRegistry` (DBX025-027), `KeyRing.Load` (DBX029) |

#### Guarantees and the tests that enforce them

- Personal fields and the stored state of their streams are never stored in plain text (`PersonalDataTests.Personal_fields_and_state_are_stored_encrypted_and_read_back`).
- After `EraseSubjectAsync` returns, no load reads the subject's data; after the job, every stream they touched holds one SubjectErased per subject and rebuilt state (`Erasing_a_subject_...`, `ErasureTortureTests`, with killed runners and concurrent appends).
- An altered ciphertext, or a master key that cannot unwrap, fails loudly (DBX030, DBX029). A deleted subject key is the only thing that reads as erased (`An_altered_ciphertext_...`, `A_wrong_master_key_...`).
- Erasure is scoped to the tenant (`Erasure_is_scoped_to_the_tenant`).
- Every key mode passes `KeyProviderCompliance.VerifyAsync`: database, key ring, and Azure with a local RSA key. A provider that does not verify fails it.
- Start-up fails without a key mode (DBX025), for a non-nullable personal property (DBX026), and for a missing subject (DBX027).

#### Known limits, stated plainly

1. Backups taken before an erasure keep the wrapped subject key, and in database mode the master key, until they age out.
2. Projections and subscriptions receive decrypted data. The app scrubs its own tables when it handles SubjectErased.
3. Subject IDs, stream IDs and metadata are plain text by design; the docs require pseudonymous IDs.
4. Only top-level properties are encrypted.
5. An event written about a subject after erasure gets a new key and stays readable. Erasure is a point in time.
6. ErasedSubjects on an envelope is best effort when an upcaster renamed the field.
7. There is no API yet to shred a whole tenant, although the per-tenant key supports it; it would delete the tenant's master_keys rows. Step 10 (admin API) can add it if you want it in 0.1.0.

#### Decisions to confirm

- Sealing personal streams' stored state with the tenant key (not in the SDD).
- Clearing stored state as part of the key deletion.
- The job retry policy: rejections fail, anything else retries up to 10 times.

#### Items still open from gate 1 and step 6

- A caller transaction that appends to two streams can deadlock with a concurrent append; the database aborts one. Nothing is lost. Document it, or change the design?
- `app.UseDeedboxMetadata(...)` needs ASP.NET Core; the core has the scoped `DeedboxContext` instead. Add an ASP.NET Core package, or document the middleware one-liner?

### HUMAN GATE 3: release sign-off for 0.1.0

Status: approved on 2026-09-24, with native json on SQL Server added to 0.1.0.

Released on 2026-09-24: https://github.com/alternayte/deedbox/releases/tag/v0.1.0. All nine packages are on nuget.org, owned by alternayte. The trusted publishing push needed three attempts: the first two got 403 on new package IDs until the nuget.org policy was corrected; Deedbox.Cli went out in the first attempt, and the reruns skipped it as a duplicate.

#### State

- CI is green on the step 15 commit: Postgres and SQL Server, net8.0 and net10.0, 315 tests per framework, plus the docs snippets and both template variants.
- Nightly is green on the step 15 commit: torture scale 5 on both providers, and benchmarks within 30% of the baseline in every cell (run 35975600158).
- With native json: CI (run 35980778671) and nightly (run 35980785754, torture scale 5 including the SQL Server 2025 pass, and benchmarks) are green.
- Anthology runs on Deedbox in https://github.com/alternayte/anthology/pull/2. All 149 Anthology tests pass on data that the old event store wrote and the migration script moved. Step 14 found four Deedbox gaps; each is fixed and tested (see the step 14 decisions).
- The docs are live at https://deedbox-docs.pages.dev.

#### NuGet

All nine package IDs are free on nuget.org (checked 2026-09-24), and no package matches "deedbox". The prefix reservation needs your nuget.org account: request `Deedbox.*` as described at https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation.

#### What you do to release

1. Add a trusted publishing policy on nuget.org for alternayte/deedbox and `release.yml`. The workflow logs in with NuGet/login, so no API key is stored. The repository variable NUGET_USER holds the nuget.org user name. Reserving the prefix is optional and is requested by email.
2. Set the date of the 0.1.0 entry in CHANGELOG.md.
3. Push the tag v0.1.0. The release workflow checks, packs, pushes and creates the GitHub release.
4. After the release, set PackageValidationBaselineVersion to 0.1.0 and raise VersionPrefix.

Also still open from step 13: the CLOUDFLARE_API_TOKEN secret for docs deploys from CI.

#### Decisions to confirm

- SQL Server native `json`: you chose to ship it in 0.1.0. It is in (see the Gate 3 decisions below).
- Anthology drops its unread outbox instead of running the QueueBox sidecar (Anthology PR, decision 1).
- SDD open items that only you can close: a GitHub org (the repositories are under alternayte) and a docs domain (deedbox.dev is not registered).

## 0.2.0

Spec: `docs/specs/queuebox-message-shaping.md` (from the grill on 2026-09-24).

- QueueBox message shaping: `Publish<TEvent>((e, pending) => QueueBoxMessage?)` returns topic, payload and extra headers per event, or null to skip it. Default headers stay; an app header replaces a default of the same name in any letter case, because header names are case-insensitive on every transport. Invalid messages and callback exceptions fail the append with DBX032; a callback exception is the inner exception.
- `QueueBoxMessage` is a sealed class, not a record: a record would add value equality over an `object` payload and a dictionary, which compares by reference, and more public members.
- The callback payload uses Deedbox's JSON options, the same as the payload overload. Returning the event itself sends it as plain JSON; the callback counts as a mapping for the `[PersonalData]` rule, so that is the app's choice.
- CloudEvents: the "Wire QueueBox" guide shows structured and binary mode as compiled snippets; their tests read a valid CloudEvent back from an outbox table on Postgres. No CloudEvents type ships.
- VersionPrefix is 0.2.0, and package validation uses 0.1.0 from nuget.org as its baseline, so a breaking change fails `just pack`.
- `scripts/template-check.sh` takes the newest template package, because artifacts can hold templates of several versions.
- SQL Server test connections use a 300 s command timeout. A native json gate run failed the append torture test with a SqlClient timeout in ReadStream: a writer queued behind the test's long transactions, at 2 commits/s under x86 emulation of SQL Server 2025, waited past the 30 s default. The torture checks (gaps, commit order, duplicates) are unchanged.
- Released on 2026-09-24: https://github.com/alternayte/deedbox/releases/tag/v0.2.0. All nine packages pushed through trusted publishing in one attempt.

## Docs after 0.2.0

- Tutorial "Version a manuscript" (`tutorials/manuscript-versions.mdx`): versions are VersionFrozen events that list their sections, named after NISO JAV stages; corrections follow Crossref (notice with its own DOI, article DOI kept) and retractions follow NISO CREC (nothing deleted). The state keeps only what decisions need; an inline EF Core projection keeps the history in six tables; one query class serves REST and GraphQL, including the latest version, the published version, the history, one version, and section changes between two versions.
- The tutorial code is in `site/snippets/Deedbox.Snippets/Publishing`. Its test runs the whole lifecycle on Postgres through the HTTP endpoints, a GraphQL query and an erasure. Hot Chocolate and Microsoft.AspNetCore.TestHost are docs-only package versions in Directory.Packages.props; no Deedbox package depends on them.
- The tutorial's detail read is one query: the projection keeps the whole ManuscriptView as a jsonb document per manuscript, next to the columns the list filters and sorts on. Authors, rounds and updates live only in the document, so the read model has three tables, not six. The snippet tests count EF Core commands: the detail GET runs one.
- The list is `GET /manuscripts?search=&status=&after=&limit=`: keyset paging on (updated_at desc, id) with an opaque cursor, and an escaped ILIKE title search behind a pg_trgm GIN index.
- GraphQL moved to the how-to "Serve a read model over GraphQL", with Hot Chocolate type extensions and batch and grouped DataLoaders. The snippet test shows a page with authors, latest version and versions costs three queries. Queries open a context per call from IDbContextFactory, because GraphQL resolvers run in parallel.
- The tutorial adds a second projection, `people`, with `people` and `manuscript_authors` tables: manuscripts by person, search by name or affiliation, counts per person and per institution, and one profile per person across manuscripts. AuthorAdded now carries PersonId, ORCID and the corresponding-author flag; affiliation belongs to the authorship. Byline order is the event's stream version. The snippet test covers each scenario and an erasure.
- New concept page "Inline or async projections" and how-to "Replace a read model without downtime" (a manual blue/green rebuild: the new projection is registered async under a new name and serves reads once caught up).
- Two gaps found while writing them: (1) nothing removed a retired projection's checkpoint; (2) during a rolling deploy, instances of the old version appended without applying a new inline projection, which then missed those events after its cut-over. Both are fixed in 0.3.0.

## 0.2.1

- A docs gate run failed the SQL Server append torture test: an observer saw position 308 right after 305. Diagnostics added to the test showed that the missing positions existed in the final table, so the writer was correct and the read skipped them. A focused test (`ReadRaceTests`) reproduced it under load: a reader expected 962 and got 979. Cause: under SQL Server's locking READ COMMITTED, a read that waits on an append's uncommitted row can resume part of the way through that append's rows after it rolls back, while the next append reuses the same positions behind it. The runner reads with the same query, so an async projection on SQL Server without RCSI could skip events for good. Postgres and RCSI read committed snapshots and were not affected.
- Fix: every read by position (runner, rebuild, skip) reads the counter first and stops at it, in one batch on SQL Server and one statement on Postgres. Positions at or below the committed counter are committed, so no row in the range is in flight or reused. On locking SQL Server, the counter read waits for an append in flight; that is the wait the old read had too, one row earlier.
- The torture observers now read through the provider, as the runner does, instead of their own range query. The ordering concept page states the bound, and shows it for apps that read the events table with their own SQL.
- VersionPrefix is 0.2.1, with package validation against 0.2.0.
- Released on 2026-09-25: https://github.com/alternayte/deedbox/releases/tag/v0.2.1. All nine packages pushed through trusted publishing.

## 0.3.0

Spec: `docs/specs/live-instances-and-retired-projections.md` (from the grill on 2026-09-25).

- Heartbeat: every instance writes a row to `instances` at start-up, every 10 seconds and until it stops, listing its projections and subscriptions, its inline projections, and the `streamType/eventType` pairs it can append. Liveness is three intervals by the database clock. A clean stop deletes the row; rows unseen for ten liveness windows are deleted.
- `checkpoints.handles` records the events a projection handles, including aliases and built-ins, so an instance without the projection's code can compute whether it would skip it. An instance skips a projection when it does not run it inline and can append one of its events; built-ins count only where the instance's stream types meet the projection's.
- The inline cut-over checks the live instances before and again after taking the gate and counter locks, and keeps catching up while any would skip the projection. Start-up writes the heartbeat first, seeds new inline checkpoints in catch-up when a live instance would skip them, then moves back to catch-up any running inline projection that this instance would skip, at the current head under the same locks.
- Retire: `Admin.Retire` serves both `IEventStoreAdmin.RetireAsync` and `deedbox retire`. It refuses with DBX035 while a live instance registers the name, and DBX033 when no checkpoint has it. `retired` checkpoints are skipped by appends and by the runner, listed with no lag, and reported degraded by the health check when the app registers the name. `RebuildAsync` brings one back.
- `RetireAsync` has a default interface body that throws NotSupportedException, so implementations of IEventStoreAdmin written for 0.2 still compile; package validation against 0.2.1 passes. Version 0.3.0, a minor release for the new API and schema.
- The tests `LiveInstancesTests` cover the rolling deploy, the rollback, an instance with other stream types, and retire; with the conflict rule disabled, the first two fail.

