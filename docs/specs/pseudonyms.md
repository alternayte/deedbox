# Pseudonymous subject IDs

## What it does
Deedbox turns a real identity, such as `github:alice`, into a stable keyed subject ID, such as `person:k7q2m9x4…`.
The app passes the identity and a period ID; Deedbox returns `prefix + base32(first 128 bits of HMAC-SHA256(secret, identity))`.
Each (tenant, period) has its own random secret, wrapped by the master key.
`EraseIdentityAsync` erases the identity's subject in every period whose secret still exists.
Destroying a period's secret makes its subject IDs impossible to link to an identity again.
The identity is never stored, logged, traced, measured or put in an error message.
Issue: alternayte/deedbox#3. Version 0.4.0.

## Decisions
- The token is 128 bits: 26 lower-case base32 characters (RFC 4648 alphabet), no padding; not configurable — 128 bits makes collisions negligible and one fixed length keeps subject IDs uniform.
- The prefix is set once with `DeedboxBuilder.PseudonymPrefix(prefix)`, default `person:`; at most 74 characters with no white space — a subject ID is at most 100 characters.
- Periods are explicit period IDs from the app, such as `2026-Q3`; `PseudonymPeriod.Quarter(at)` and `PseudonymPeriod.Month(at)` build calendar IDs in UTC; an app that wants no rotation passes one fixed period ID — the app decides what a period means, and erasure never has to guess periods from dates.
- A period ID is 1 to 64 characters: letters, digits, `.`, `_`, `:` and `-`, starting with a letter or digit — IDs compare byte for byte on both databases and are safe to type in the CLI.
- One secret per (tenant, period), a new random 32-byte key wrapped by the master key like tenant keys, in the new table `pseudonym_keys` (migration 5) — it is not derived from the tenant key, so each can be destroyed on its own.
- The pseudonymizer lives in the core `Deedbox` package, as the scoped `IPseudonyms` service — it needs the key hierarchy and the erasure that live there.
- `IPseudonyms.SubjectForAsync(identity, periodId)` uses the scope's tenant; it creates the period's secret on first use, in its own committed transaction — a rolled-back append must never leave a secret that only one process knows.
- Each call reads the period's row; the unwrapped secret is cached per process only while the row's wrapped bytes are unchanged — a destroy or shred on another instance applies at the next call, and the master key is not called for every write.
- The row stores the prefix the period was created with; a call whose configured prefix differs fails with DBX037 — changing the prefix inside a period would silently give a person two subject IDs.
- Erasure by identity reads every period with a secret, computes each subject ID with the row's prefix, deletes all their subject keys in one transaction, then queues one erasure job per period — the admin API and the CLI then need no prefix option.
- `IEventStoreAdmin.EraseIdentityAsync(identity, tenantId)` and `IPseudonyms.EraseIdentityAsync(identity)` return the job IDs, one per period.
- Destroying a period secret (`IEventStoreAdmin.DestroyPseudonymPeriodAsync`, `deedbox pseudonyms destroy <period> --tenant <t> --yes`) turns the row into a tombstone with no key material, in one transaction with a `pseudonyms_destroyed` job row as the audit record — the jobs table is Deedbox's audit trail.
- A destroyed period stays closed: `SubjectForAsync` for it fails with DBX036 — a late write in an old period would otherwise get a new secret and a second, unrelated subject ID.
- Destroying a period that never had a secret writes the tombstone too, and returns false.
- Shredding a tenant deletes all its pseudonym rows, tombstones included — the tenant's old subject IDs can never be computed again, and data written afterwards gets new secrets, as with tenant keys.
- `RewrapKeysAsync` and `deedbox keys rewrap` re-wrap pseudonym secrets with the tenant keys, in the same transaction; the count includes them — the secret bytes do not change, so no subject ID changes.
- `deedbox erase --identity <id> --tenant <t> --master-key <m>` computes the subject IDs in the CLI; `--master-key` takes the `keys rewrap` forms and is required — the CLI must unwrap the secrets and must not guess the key mode.
- Deedbox does not normalize identities or resolve aliases; it rejects an empty identity or one with leading or trailing white space, without echoing it.
- Reads, rebuilds and erasure by subject ID never read `pseudonym_keys`.

## Out
- Normalizing identities (case, dots in Gmail addresses) or mapping several identities to one person.
- Destroying all periods before a date in one call.
- Listing periods in `deedbox status`.
- A prefix per call or per stream type.
- Protection for free text: content that names a person still needs `[PersonalData]`.

## How I know it works
- The same identity and period give the same subject ID on two instances, on Postgres and SQL Server; a fixed secret gives a fixed, known token.
- A different period, tenant or identity gives a different subject ID.
- `EraseIdentityAsync` erases the subject in every period with a secret, skips destroyed periods, and leaves another identity readable.
- After `DestroyPseudonymPeriodAsync`, `SubjectForAsync` for that period fails with DBX036 on every instance, the row holds no key material, and a `pseudonyms_destroyed` job row records it; the CLI command needs `--yes`.
- A scan of every column of every Deedbox table finds no trace of the identity after writes, erasure by identity and a period destroy; captured logs, spans and metric tags do not hold it either.
- Rotating the key ring and re-wrapping to another master key leave every subject ID unchanged.
- Shredding a tenant removes its pseudonym rows; the tenant's next subject ID for the same identity differs, and another tenant's does not.
- Reads, rebuilds and erasure by subject ID work after every pseudonym secret is destroyed.
- `deedbox erase --identity` erases the subject in every period and never prints the identity.
- `just check` passes on both databases and both frameworks.
