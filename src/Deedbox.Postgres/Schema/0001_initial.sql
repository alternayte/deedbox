CREATE SCHEMA IF NOT EXISTS {{schema}};

CREATE TABLE IF NOT EXISTS {{schema}}.schema_version (
    version    integer     NOT NULL PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS {{schema}}.streams (
    tenant_id     text        NOT NULL DEFAULT '',
    stream_id     text        NOT NULL,
    stream_type   text        NOT NULL,
    version       bigint      NOT NULL,
    state         jsonb       NULL,
    state_version integer     NOT NULL DEFAULT 0,
    state_at      bigint      NOT NULL DEFAULT 0,
    deleted_at    timestamptz NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT streams_pk PRIMARY KEY (tenant_id, stream_id)
);

CREATE TABLE IF NOT EXISTS {{schema}}.events (
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
    CONSTRAINT events_pk PRIMARY KEY (global_position),
    CONSTRAINT events_event_id UNIQUE (event_id),
    CONSTRAINT events_stream_version UNIQUE (tenant_id, stream_id, version)
);

-- The serialized global position counter. One row; appends hold its lock from update to commit.
CREATE TABLE IF NOT EXISTS {{schema}}.position (
    id    boolean NOT NULL DEFAULT true,
    value bigint  NOT NULL,
    CONSTRAINT position_pk PRIMARY KEY (id),
    CONSTRAINT position_single_row CHECK (id)
);

INSERT INTO {{schema}}.position (id, value) VALUES (true, 0) ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS {{schema}}.event_types (
    stream_type   text        NOT NULL,
    event_type    text        NOT NULL,
    event_version integer     NOT NULL,
    first_seen    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT event_types_pk PRIMARY KEY (stream_type, event_type, event_version)
);

CREATE TABLE IF NOT EXISTS {{schema}}.checkpoints (
    name       text        NOT NULL,
    position   bigint      NOT NULL DEFAULT 0,
    aux        bigint      NULL,
    mode       text        NOT NULL,
    status     text        NOT NULL,
    error      jsonb       NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT checkpoints_pk PRIMARY KEY (name)
);

CREATE TABLE IF NOT EXISTS {{schema}}.jobs (
    id          uuid        NOT NULL,
    kind        text        NOT NULL,
    args        jsonb       NOT NULL,
    status      text        NOT NULL,
    progress    jsonb       NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    started_at  timestamptz NULL,
    finished_at timestamptz NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT jobs_pk PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS {{schema}}.master_keys (
    tenant_id   text        NOT NULL DEFAULT '',
    key_version integer     NOT NULL,
    wrapped_key bytea       NOT NULL,
    wrapped_by  text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT master_keys_pk PRIMARY KEY (tenant_id, key_version)
);

CREATE TABLE IF NOT EXISTS {{schema}}.subject_keys (
    tenant_id   text        NOT NULL DEFAULT '',
    subject_id  text        NOT NULL,
    key_id      text        NOT NULL,
    wrapped_key bytea       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT subject_keys_pk PRIMARY KEY (tenant_id, subject_id)
);

CREATE TABLE IF NOT EXISTS {{schema}}.subject_streams (
    tenant_id  text NOT NULL DEFAULT '',
    subject_id text NOT NULL,
    stream_id  text NOT NULL,
    CONSTRAINT subject_streams_pk PRIMARY KEY (tenant_id, subject_id, stream_id)
);

INSERT INTO {{schema}}.schema_version (version) VALUES (1) ON CONFLICT (version) DO NOTHING;
