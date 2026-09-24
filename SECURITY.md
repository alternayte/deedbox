# Security policy

## Supported versions

Security fixes go into the latest 0.x minor release. Upgrade to it to get a fix.

## Report a vulnerability

Do not open a public issue for a vulnerability.

Report it privately through [GitHub's private vulnerability reporting](https://github.com/alternayte/deedbox/security/advisories/new). Include the affected package and version, the steps that show the problem, and its effect.

The maintainer confirms the report, agrees a fix and a disclosure date with you, and credits you in the advisory unless you ask otherwise.

## Scope

Deedbox holds event data and, for `[PersonalData]`, the keys that protect it. These are in scope:

- Encryption, key wrapping, key rotation and crypto-shredding in `Deedbox` and `Deedbox.Keys.AzureKeyVault`.
- Personal data that reaches storage, logs, metrics, traces, error messages or outbox rows in plain text.
- SQL built from names or identifiers in the providers and in `Deedbox.QueueBox`.
- The `deedbox` CLI.

The database server, the app's own code and the key service configuration are out of scope.
