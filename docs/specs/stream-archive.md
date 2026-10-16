# Stream archive

## What it does
An app archives the streams it considers finished. The archive moves their events from `deedbox.events` to `deedbox.events_archive`, so the hot table and default rebuilds stop growing with finished streams. A stream stays loadable, readable, appendable, erasable and deletable after the archive. Global positions do not change, so checkpoints stay valid. Each archive is a job in `deedbox.jobs` (issue alternayte/deedbox#10).

## Decisions
- `IEventStoreAdmin.ArchiveStreamsAsync(IReadOnlyList<StreamKey>)` queues one job for a list of (tenant, stream ID) pairs; `ArchiveStreamAsync(tenantId, streamId)` is the one-stream form — only the app knows which stream is finished, so there is no selection by age.
- The job progress counts streams archived, partial, and skipped (missing or deleted) — the job row is the audit trail.
- Migration 8 adds `events_archive` with the columns, primary key and unique constraints of `events`, and `streams.archived_to` (bigint, default 0) — the stream records the version it is archived up to.
- The job archives one stream per transaction under the stream row lock, with insert-select then delete — an append to the same stream waits and never interleaves.
- The job moves only events whose global position is at or below the lowest checkpoint of every consumer that is not retired — a consumer never misses an event it did not apply, so a stalled or catching-up consumer holds the archive back.
- A stream with events above that line is archived up to the line and counted as partial — a later archive moves the rest.
- The job does not take the position counter — every event at or below the lowest checkpoint is committed, and a reset or a new consumer reads the archive only through `includeArchived`.
- An append to an archived stream succeeds; its events go to `deedbox.events` — erasure appends `SubjectErased` and `DeleteStream` appends `StreamDeleted` to archived streams, and a restore would undo the archive.
- `Load`, `ReadStream`, the snapshot job, the erasure job, a state-version replay and a `SnapshotPolicy.Never` load read both tables below `archived_to` — the history stays whole.
- `DeleteStream` deletes the stream's rows from both tables — it stays the only way to remove history.
- `RebuildAsync(projection, includeArchived = false)`: with true, the replay reads both tables merged in global position order until it passes the highest archived position — a read model of all manuscripts needs the archived ones.
- The checkpoint stores the include flag while the rebuild runs — a restart or a handover keeps it.
- A new projection or subscription reads only `deedbox.events` — to include the archive, rebuild it with `includeArchived: true`.
- An archived event is storage format 3; 0.8 writes `instances.formats = 3` — an instance on 0.7 or earlier replays stored state from the hot table only.
- The archive call fails with DBX041 while any heartbeat row, live or stale, has `formats < 3`, and the job checks again in its transaction — a rolling deploy cannot corrupt stored state.
- After the first archive, a rollback to 0.7 or earlier is not supported — 0.7 accepts a newer schema and cannot be changed.
- `JobKind.Archive` and the stored kind `archive` join the job kinds — status and the CLI show the job.
- The CLI gets `deedbox archive --tenant <t> --stream <id>`, `deedbox archive --file <path>` (one stream per line), and `deedbox rebuild --include-archived` — the admin API and the CLI stay equal.
- The operator may move the archive table's storage (tablespace, filegroup, page compression) and back it up apart; deleting or truncating its rows is not supported — four read paths need them.
- The torture suite archives streams during concurrent appends and consumers — it asserts that no event is lost and none is applied twice.
- Release 0.8.0, a minor release — it adds public API and a migration.

## Out
- Selection by stream type or age.
- Partitions of `deedbox.events` or of the archive table.
- Export-and-delete of archived rows.
- A restore of an archived stream to the hot table.

## How I know it works
- An archived stream loads with the same state and version as before the archive, on both providers.
- `ReadStream` returns the same envelopes before and after the archive; an append after the archive returns the old and the new events in version order.
- `SELECT count(*) FROM deedbox.events` goes down by the event count of the archived streams.
- A rebuild without `includeArchived` applies no archived event; with it, the read model equals the one built before the archive.
- With a projection stalled below a stream's last event, the job archives the stream up to the stall and counts it partial; the projection applies the rest after the skip.
- An erasure of a subject whose stream is archived redacts the event in `ReadStream` and clears the stored state; the next load rebuilds the same state with the field redacted.
- With a heartbeat row at `formats = 2`, `ArchiveStreamsAsync` fails with DBX041.
- The table growth and known limits pages describe the archive, and the known limits page states the rollback limit.
