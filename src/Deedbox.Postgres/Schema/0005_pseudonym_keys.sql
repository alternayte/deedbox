-- One secret per tenant and period for pseudonymous subject IDs, wrapped by the master key. A destroyed period keeps
-- its row as a tombstone with no key material, so the period can never get a new secret.
CREATE TABLE IF NOT EXISTS {{schema}}.pseudonym_keys (
    tenant_id    text        NOT NULL DEFAULT '',
    period_id    text        NOT NULL,
    prefix       text        NOT NULL,
    wrapped_key  bytea       NOT NULL,
    wrapped_by   text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    destroyed_at timestamptz NULL,
    CONSTRAINT pseudonym_keys_pk PRIMARY KEY (tenant_id, period_id)
);

INSERT INTO {{schema}}.schema_version (version) VALUES (5) ON CONFLICT (version) DO NOTHING;
