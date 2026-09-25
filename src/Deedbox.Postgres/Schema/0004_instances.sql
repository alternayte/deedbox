-- One row per live app instance: what it runs and what it can append, so an inline projection never misses an
-- append from an instance that does not know it. Rows are refreshed every few seconds and removed on a clean stop.
CREATE TABLE IF NOT EXISTS {{schema}}.instances (
    instance_id        uuid        NOT NULL,
    host               text        NOT NULL,
    app                text        NOT NULL,
    consumers          jsonb       NOT NULL,
    inline_projections jsonb       NOT NULL,
    event_types        jsonb       NOT NULL,
    started_at         timestamptz NOT NULL DEFAULT now(),
    seen_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT instances_pk PRIMARY KEY (instance_id)
);

-- The events each projection handles, so an instance without the projection's code can tell whether it would skip it.
ALTER TABLE {{schema}}.checkpoints ADD COLUMN IF NOT EXISTS handles jsonb NULL;

INSERT INTO {{schema}}.schema_version (version) VALUES (4) ON CONFLICT (version) DO NOTHING;
