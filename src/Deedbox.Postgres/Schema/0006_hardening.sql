-- Deleting a stream removes its subject pairs by stream; the primary key leads with the subject.
CREATE INDEX IF NOT EXISTS subject_streams_by_stream ON {{schema}}.subject_streams (tenant_id, stream_id);

-- The job loop claims the oldest queued job; finished jobs stay as the audit trail and must not be scanned.
CREATE INDEX IF NOT EXISTS jobs_queued ON {{schema}}.jobs (created_at, id) WHERE status = 'queued';

-- Stall and job records no longer keep exception messages: a handler's or the database's message can hold personal
-- data, and these rows outlive an erasure. Deedbox's own job rejections name no data and stay.
UPDATE {{schema}}.checkpoints SET error = error - 'message' - 'stackTrace'
WHERE error ?| ARRAY['message', 'stackTrace'];

UPDATE {{schema}}.jobs SET progress = progress #- '{stall,message}' #- '{stall,stackTrace}'
WHERE progress -> 'stall' ?| ARRAY['message', 'stackTrace'];

UPDATE {{schema}}.jobs SET progress = progress - 'error'
WHERE status = 'failed' AND progress ? 'error'
  AND coalesce(progress ->> 'exception', '') NOT IN ('Deedbox.JobRejected', 'Deedbox.DeedboxException');

-- The newest storage format an instance can read. A version from before this column reads only format 1, and nobody
-- writes a newer format while such an instance has a heartbeat row.
ALTER TABLE {{schema}}.instances ADD COLUMN IF NOT EXISTS formats int NOT NULL DEFAULT 1;

INSERT INTO {{schema}}.schema_version (version) VALUES (6) ON CONFLICT (version) DO NOTHING;
