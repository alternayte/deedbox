# Deedbox build progress

The design doc (SDD) is the source of truth. It is local only and never committed.

## Steps

- [x] 1. Repo scaffold: solution, packages, Directory.Build.props, net8.0 + net10.0, analyzers (nullable, AOT, trimming), package validation, GitHub Actions with Testcontainers, CONTRIBUTING stub.
- [x] 2. Schema: embedded migration scripts for both providers, schema manager with locks, schema_version startup check, CLI "schema script" and "schema apply".
- [ ] 3. Write path: string stream IDs, position counter (option A), streams and events tables, Append / Load / Execute with expected versions, conflict retry, snapshots with state_version, transaction modes (neither, Dapper, EF Core with several DbContexts in one transaction).
- [ ] 4. Torture suite v1: concurrent appends with rollbacks and long transactions; assert gapless commit-ordered positions and per-stream order. HUMAN GATE 1: API shape review and torture suite v1 results.
- [ ] 5. Registry and evolution: naming convention, aliases, event_types table, startup name check, JSON upcasters and typed upcasters, lockfile in Deedbox.Testing, Given/When/Then helpers.
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

## Gate reports
