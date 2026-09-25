# Live instances and retired projections

## What it does
Each app instance records a heartbeat: the inline projections it registers and the event types it can append. Deedbox uses it so that an inline projection never misses an append from an instance that does not know it. A new inline projection switches from catch-up to inline only when no live instance can append its events without registering it. An instance that starts without an inline projection moves it back to catch-up first. A projection can be retired from the admin API or the CLI; its checkpoint stays as `retired`, and nothing applies it until someone rebuilds it.

## Decisions
- A heartbeat table holds one row per live instance: instance ID, host, app name, time last seen, its inline projection names, and the stored event types it can append — both gaps need to know what each running instance registers.
- The runner writes the heartbeat every 10 seconds, on every instance, including instances with the runner disabled; a row older than 30 seconds is not live; a graceful stop deletes its row — instances that append must be counted even when they run no projections, and a clean deploy should not wait for a timeout.
- The interval and timeout are internal, not options — they can become options later without a breaking change.
- An inline cut-over (a rebuild, or a new inline projection on a store with events) happens only when every live instance that can append an event type the projection handles also registers it — during a rolling deploy, the projection stays in catch-up until the old instances stop.
- Instances that cannot append any of the projection's event types do not count — a second app on other streams never blocks the switch.
- An instance that starts, can append a running inline projection's event types, and does not register it, moves that projection to catch-up from the current head before it serves, under the gate and counter locks that a cut-over uses — every append up to the head applied it inline, so nothing is missed, and a rollback keeps working.
- The forced cut-over after 20 polls without progress still requires the heartbeat condition — forcing it must not reopen the gap.
- Retiring sets the checkpoint to `retired` in one transaction; it does not delete the row — a later start of an old version must not recreate the checkpoint and replay into dropped tables.
- `IEventStoreAdmin.RetireAsync(name)` and `deedbox retire <name>` run at once, not as a job, and refuse with a new DBX code, naming the instances, while any live instance registers the name — the decision needs only the database.
- An instance that registers a retired name still starts; appends do not apply the projection, the runner skips it, and the health check reports it as degraded with the fix — a rollback must not fail to start.
- `RebuildAsync(name)` on a retired projection brings it back: `ResetAsync`, then a replay of every event — one way back, through an operation that exists already.
- `deedbox status` and `GetStatusAsync` list retired projections as `retired` with no lag; the health check ignores them unless a live instance registers the name.
- The heartbeat table comes from a new numbered migration on both databases — storage changes ship as forward migrations.

## Out
- Blocking or failing appends during a deploy.
- Deleting checkpoint rows.
- Choosing which instance runs which projection.
- A public heartbeat interval or timeout option.
- Built-in blue/green rebuilds.

## How I know it works
- An instance that registers a new inline projection, next to a live instance of the old version that appends the same event types, leaves the projection in catch-up; after the old instance stops, the projection is inline and has applied every event exactly once.
- An old instance that starts while a projection runs inline moves it to catch-up before its first append; its appends reach the projection through the runner, and none is missed.
- An app with only other stream types does not hold back a cut-over.
- `deedbox retire cart_summary` fails with the new DBX code and the instance names while an instance registers `cart_summary`, and succeeds after it stops.
- A start of an instance that registers a retired projection succeeds, applies nothing to it, and the health check reports it degraded; `RebuildAsync("cart_summary")` makes it run again.
- `deedbox status` shows the retired projection as `retired`.
- `just check` passes on both databases and both frameworks, with the torture suites unchanged.
