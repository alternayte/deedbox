# Deedbox

## What this is

An MIT-licensed .NET library that event-sources part of an existing app on Postgres or SQL Server, with EF Core, Dapper or plain ADO.NET.
Work follows the step list and decisions in `PROGRESS.md`; the design doc is local only and never committed.

## Run

It is a library; nothing runs on its own. The CLI runs with `dotnet run --project src/Deedbox.Cli -f net10.0 -- schema script --provider postgres`.

## Test

- `just check` is the gate: repo checks, build, every test on both providers and both frameworks, then pack.
- Tests need Docker; Testcontainers starts Postgres and SQL Server. DEEDBOX_TEST_POSTGRES and DEEDBOX_TEST_SQLSERVER point them at existing servers.
- `scripts/errors.sh` builds and prints each distinct compiler error on one line.
- `just api` records new public symbols in PublicAPI.Unshipped.txt after a deliberate API change.

## Stack rules

- Targets net8.0 and net10.0; core and providers are AOT-compatible, and warnings are errors.
- Every algorithm lives in the core. A provider class only supplies SQL for one database, so both databases behave the same.
- Sibling packages share internals through InternalsVisibleTo and pin each other's exact version.
- Every public symbol has XML docs and a test.
- No ConfigureAwait.
- A provider test is an abstract class with one sealed subclass per database.
- Never weaken, skip or delete a test to make it pass.

## Domain words

- stream: the ordered events of one aggregate instance, keyed by tenant and stream ID.
- stream type: the stored name of a state type, such as cart.
- event type: the stored name of an event, such as cart.item_added; its shape version is stored apart.
- global position: an event's place in commit order across all streams; gapless, never used in arithmetic.
- position counter: the single row whose lock serializes appends from its update to commit.
- snapshot: a stream's state stored with its state version and the stream version it reflects.
- torture suite: randomized concurrent tests that assert the store's guarantees on both providers.
- gate: a step in the build prompt where work stops for human review.
