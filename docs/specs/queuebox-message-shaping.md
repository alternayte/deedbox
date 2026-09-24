# QueueBox message shaping

## What it does
Deedbox.QueueBox 0.2.0 adds `Publish<TEvent>((e, pending) => QueueBoxMessage?)`. The callback runs for each published event and returns the whole message: topic, payload and headers. A `null` return writes no outbox row for that event. The existing `Publish<TEvent>(topic)` and `Publish<TEvent>(topic, payload)` overloads keep their behaviour. An app can emit CloudEvents in structured or binary mode with this callback.

## Decisions
- One callback returns topic, payload and headers together — binary-mode CloudEvents and a topic per event need all three from the same event.
- `QueueBoxMessage(string Topic, object Payload)` with an optional `Headers` property of string names to string values — the QueueBox headers column is a JSON object of strings.
- The existing `Publish` overloads stay as short forms — they cover the common case, and existing code does not change.
- Deedbox always writes its default headers (`x-deedbox-*`, `X-Correlation-Id`, `traceparent`) — tracing and event identity stay on every message.
- App headers merge over the defaults, and the app value wins on the same name — deduplication uses the row ID, not a header, so an override cannot break it.
- The app cannot remove a default header — the defaults stay available to every receiver.
- Deedbox does not forward `EventMetadata.Headers` — metadata headers can hold app-internal values; the callback copies `pending.Metadata.Headers` when the app wants them.
- The row key stays the stream ID, and `aggregate_type` stays the stream type — one stream's messages keep their order.
- A `null` return skips the event — an app can publish only some events of a type.
- An empty topic, a topic over 255 characters, an empty header name, a null header value, or an exception in the callback fails the append with DBX032 and rolls it back — events and messages commit together or not at all.
- An event type still has one publication — one event gives at most one message; QueueBox routing fans out to destinations.
- The callback counts as a payload mapping for the `[PersonalData]` rule — the app chooses what reaches the outbox, as with the payload overload.
- Deedbox's JSON options serialize the payload — the same as the payload overload.
- No CloudEvents helper; the "Wire QueueBox" guide documents both modes with compiled snippets — Deedbox stays free of message formats, and `source` and the attribute mapping differ per app.

## Out
- `max_attempts` and `scheduled_at` per message. They can come later as optional properties.
- Overriding the row key, the row ID or `aggregate_type`.
- One event to several messages or topics.
- A built-in CloudEvents type or helper.
- Forwarding metadata headers without app code.

## How I know it works
- `Publish<ItemAdded>((e, p) => new QueueBoxMessage($"cart.{p.StreamId}", e) { Headers = new Dictionary<string, string> { ["ce-type"] = p.EventType } })` writes a row with that topic, the `ce-type` header and every default header, on Postgres and SQL Server.
- A header named `X-Correlation-Id` in the callback replaces the default value in the row.
- A callback that returns `null` for an event writes no row for it, and the append commits.
- A callback that returns an empty topic makes the append throw DBX032, and neither the events nor any row exist afterwards.
- `Publish<ReviewerInvited>((e, p) => ...)` starts without DBX032 for an event with `[PersonalData]`.
- The structured and binary CloudEvents snippets in the "Wire QueueBox" guide compile, and their tests read back a valid CloudEvent from the row.
- `just check` passes, and `PublicAPI.Unshipped.txt` lists only the new overload and `QueueBoxMessage`.
