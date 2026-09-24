-- Reads find a subject key by the key ID in an encrypted field.
CREATE UNIQUE INDEX IF NOT EXISTS subject_keys_key_id ON {{schema}}.subject_keys (tenant_id, key_id);

INSERT INTO {{schema}}.schema_version (version) VALUES (3) ON CONFLICT (version) DO NOTHING;
