# Stalled consumers retry

## What it does
A consumer that stalls on a poison event keeps retrying that event, and goes back to running when the event succeeds.
No restart and no skip is needed after an outage of a service that a handler calls.
The consumer still shows as stalled while it retries, so the health check and `deedbox status` report it.
Retries run at the capped backoff, one attempt per interval across all instances.
Version 0.3.1.

## Decisions
- After `HandlerRetries` failed retries, the checkpoint goes to `stalled` as today — the health check reports unhealthy, so an operator sees the stall.
- A stalled consumer retries its event every 5 minutes, the cap that the `RetryDelay` doubling already has — no new option, and the public API does not change.
- Only a stall with reason `poison` retries; a `mode_changed` stall still needs a deploy — a retry cannot fix a mode change.
- No marker for temporary exceptions — a handler cannot tell an outage from a bug, and a real poison event costs one attempt per interval.
- The stall error JSON in the checkpoint row stores `attempts` and `retryAt` — all instances then share one schedule.
- An instance retries only when `retryAt` has passed, and the instance that retries sets the next `retryAt` — N instances make one attempt per interval, not N.
- An instance that starts retries a poison stall at once, as today — a deploy that fixes the handler does not wait for the interval.
- Only a stall that exists when an instance first reads the checkpoint gets that immediate round — an instance that was already running adds no round of its own after a stall.
- When the event succeeds, the checkpoint goes to `running` and the error clears — the consumer then handles the events after it in order.
- `deedbox status` shows the attempt count and the next retry time of a stalled consumer — the operator sees that it retries.
- A skip still needs status `stalled` with reason `poison` — the status stays that while the consumer retries, so `deedbox skip` works as today.
- The release is 0.3.1 — the change is a fix to runner behaviour, with no new API and no schema change.

## Out
- Parking one event and continuing with later events: that breaks the order of a consumer.
- Rules for each exception type.
- Jobs: a failed job stays failed.
- Inline projections in an append: a failure there fails the append and does not stall.
- An inline projection that stalls in catch-up: a rebuild still restarts it.

## How I know it works
- A subscription whose handler throws until a flag changes stalls after `HandlerRetries`. After the flag changes, it goes back to `running` without a restart, and handles the events after the stalled one.
- With two instances running, the stalled event gets one attempt per interval, not two.
- `deedbox status` on a stalled consumer shows the attempt count and the next retry time.
- `deedbox skip` on a consumer that retries moves it past the event, as before.
- A `mode_changed` stall does not retry.
