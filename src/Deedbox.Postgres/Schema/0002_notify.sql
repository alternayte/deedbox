-- Wakes async runners as soon as events commit. One notification per insert statement; runners also poll.
CREATE OR REPLACE FUNCTION {{schema}}.notify_events() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('dbx_{{schema}}', '');
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS events_notify ON {{schema}}.events;
CREATE TRIGGER events_notify AFTER INSERT ON {{schema}}.events FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.notify_events();

INSERT INTO {{schema}}.schema_version (version) VALUES (2) ON CONFLICT (version) DO NOTHING;
