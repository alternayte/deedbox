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

## Release

1. Move each `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.
2. Set the release date in `CHANGELOG.md`.
3. Push the tag `v<VersionPrefix>`, such as `v0.1.0`. The release workflow runs `just check`, packs without the prerelease suffix, pushes the packages to nuget.org through trusted publishing, and creates the GitHub release. The nuget.org trusted publishing policy names this repository and `release.yml`; the repository variable NUGET_USER holds the nuget.org user name.
4. Set `PackageValidationBaselineVersion` to the released version, so package validation compares the next build against it. Raise `VersionPrefix`.

## Conduct and security

Read the [code of conduct](CODE_OF_CONDUCT.md). Report vulnerabilities as the [security policy](SECURITY.md) says, not in public issues.
