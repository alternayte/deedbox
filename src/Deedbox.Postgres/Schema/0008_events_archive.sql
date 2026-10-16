-- The events of archived streams, moved out of events by the archive job. Same columns and keys as events, so global
-- positions and event IDs stay unique across both tables.
CREATE TABLE IF NOT EXISTS {{schema}}.events_archive (
    global_position bigint      NOT NULL,
    event_id        uuid        NOT NULL,
    tenant_id       text        NOT NULL DEFAULT '',
    stream_id       text        NOT NULL,
    version         bigint      NOT NULL,
    stream_type     text        NOT NULL,
    event_type      text        NOT NULL,
    event_version   integer     NOT NULL,
    payload         jsonb       NOT NULL,
    metadata        jsonb       NOT NULL,
    occurred_at     timestamptz NOT NULL,
    CONSTRAINT events_archive_pk PRIMARY KEY (global_position),
    CONSTRAINT events_archive_event_id UNIQUE (event_id),
    CONSTRAINT events_archive_stream_version UNIQUE (tenant_id, stream_id, version)
);

-- The highest stream version whose event is in events_archive; 0 when the stream was never archived.
ALTER TABLE {{schema}}.streams ADD COLUMN IF NOT EXISTS archived_to bigint NOT NULL DEFAULT 0;

-- A rebuild that includes archived events reads both tables while its position is below this; null reads events only.
ALTER TABLE {{schema}}.checkpoints ADD COLUMN IF NOT EXISTS archive_until bigint NULL;

INSERT INTO {{schema}}.schema_version (version) VALUES (8) ON CONFLICT (version) DO NOTHING;
