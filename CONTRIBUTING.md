# Contributing to Deedbox

Deedbox is in early development. The first release is 0.1.0.

## Build and test

You need the .NET 10 SDK, the .NET 8 runtime, Docker and `just`.

```sh
just check
```

`just check` runs the repo checks, builds every package, runs every test on Postgres and SQL Server, and packs the packages. The tests start both databases with Testcontainers.

To use an existing server, set `DEEDBOX_TEST_POSTGRES` or `DEEDBOX_TEST_SQLSERVER` to a connection string.

## Rules

- Do not weaken, skip or delete a test to make it pass.
- Every public type and member has XML docs and a test.
- A change to the public API updates `PublicAPI.Unshipped.txt`.
- A change to storage ships a new numbered migration. Never edit a released migration.
