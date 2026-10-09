-- The events each instance handles per projection and subscription. An instance of a version that handles fewer events
-- leaves a checkpoint alone while a live instance handles more, so it never moves the checkpoint past their events.
ALTER TABLE {{schema}}.instances ADD COLUMN IF NOT EXISTS handles jsonb NULL;

INSERT INTO {{schema}}.schema_version (version) VALUES (7) ON CONFLICT (version) DO NOTHING;
