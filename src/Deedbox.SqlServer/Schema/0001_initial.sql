IF SCHEMA_ID(N'{{schema}}') IS NULL EXEC(N'CREATE SCHEMA [{{schema}}]');
GO

IF OBJECT_ID(N'[{{schema}}].[schema_version]', N'U') IS NULL
CREATE TABLE [{{schema}}].[schema_version] (
    version    int            NOT NULL CONSTRAINT schema_version_pk PRIMARY KEY,
    applied_at datetimeoffset NOT NULL CONSTRAINT schema_version_applied_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
);
GO

IF OBJECT_ID(N'[{{schema}}].[streams]', N'U') IS NULL
CREATE TABLE [{{schema}}].[streams] (
    tenant_id     nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT streams_tenant_id DEFAULT N'',
    stream_id     nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    stream_type   nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    version       bigint         NOT NULL,
    state         nvarchar(max)  NULL,
    state_version int            NOT NULL CONSTRAINT streams_state_version DEFAULT 0,
    state_at      bigint         NOT NULL CONSTRAINT streams_state_at DEFAULT 0,
    deleted_at    datetimeoffset NULL,
    created_at    datetimeoffset NOT NULL CONSTRAINT streams_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    updated_at    datetimeoffset NOT NULL CONSTRAINT streams_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT streams_pk PRIMARY KEY CLUSTERED (tenant_id, stream_id)
);
GO

IF OBJECT_ID(N'[{{schema}}].[events]', N'U') IS NULL
CREATE TABLE [{{schema}}].[events] (
    global_position bigint           NOT NULL,
    event_id        uniqueidentifier NOT NULL,
    tenant_id       nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT events_tenant_id DEFAULT N'',
    stream_id       nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    version         bigint           NOT NULL,
    stream_type     nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_type      nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_version   int              NOT NULL,
    payload         nvarchar(max)    NOT NULL,
    metadata        nvarchar(max)    NOT NULL,
    occurred_at     datetimeoffset   NOT NULL,
    CONSTRAINT events_pk PRIMARY KEY CLUSTERED (global_position),
    CONSTRAINT events_event_id UNIQUE (event_id),
    CONSTRAINT events_stream_version UNIQUE (tenant_id, stream_id, version)
);
GO

-- The serialized global position counter. One row; appends hold its lock from update to commit.
IF OBJECT_ID(N'[{{schema}}].[position]', N'U') IS NULL
CREATE TABLE [{{schema}}].[position] (
    id    bit    NOT NULL CONSTRAINT position_id DEFAULT 1,
    value bigint NOT NULL,
    CONSTRAINT position_pk PRIMARY KEY (id),
    CONSTRAINT position_single_row CHECK (id = 1)
);
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[position])
INSERT INTO [{{schema}}].[position] (id, value) VALUES (1, 0);
GO

IF OBJECT_ID(N'[{{schema}}].[event_types]', N'U') IS NULL
CREATE TABLE [{{schema}}].[event_types] (
    stream_type   nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_type    nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_version int            NOT NULL,
    first_seen    datetimeoffset NOT NULL CONSTRAINT event_types_first_seen DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT event_types_pk PRIMARY KEY (stream_type, event_type, event_version) WITH (IGNORE_DUP_KEY = ON)
);
GO

IF OBJECT_ID(N'[{{schema}}].[checkpoints]', N'U') IS NULL
CREATE TABLE [{{schema}}].[checkpoints] (
    name       nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    position   bigint         NOT NULL CONSTRAINT checkpoints_position DEFAULT 0,
    aux        bigint         NULL,
    mode       nvarchar(20)   NOT NULL,
    status     nvarchar(20)   NOT NULL,
    error      nvarchar(max)  NULL,
    updated_at datetimeoffset NOT NULL CONSTRAINT checkpoints_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT checkpoints_pk PRIMARY KEY (name)
);
GO

IF OBJECT_ID(N'[{{schema}}].[jobs]', N'U') IS NULL
CREATE TABLE [{{schema}}].[jobs] (
    id          uniqueidentifier NOT NULL,
    kind        nvarchar(100)    NOT NULL,
    args        nvarchar(max)    NOT NULL,
    status      nvarchar(20)     NOT NULL,
    progress    nvarchar(max)    NULL,
    created_at  datetimeoffset   NOT NULL CONSTRAINT jobs_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    started_at  datetimeoffset   NULL,
    finished_at datetimeoffset   NULL,
    updated_at  datetimeoffset   NOT NULL CONSTRAINT jobs_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT jobs_pk PRIMARY KEY (id)
);
GO

IF OBJECT_ID(N'[{{schema}}].[master_keys]', N'U') IS NULL
CREATE TABLE [{{schema}}].[master_keys] (
    tenant_id   nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT master_keys_tenant_id DEFAULT N'',
    key_version int            NOT NULL,
    wrapped_key varbinary(max) NOT NULL,
    wrapped_by  nvarchar(200)  NOT NULL,
    created_at  datetimeoffset NOT NULL CONSTRAINT master_keys_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT master_keys_pk PRIMARY KEY (tenant_id, key_version)
);
GO

IF OBJECT_ID(N'[{{schema}}].[subject_keys]', N'U') IS NULL
CREATE TABLE [{{schema}}].[subject_keys] (
    tenant_id   nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT subject_keys_tenant_id DEFAULT N'',
    subject_id  nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    key_id      nvarchar(100)  NOT NULL,
    wrapped_key varbinary(max) NOT NULL,
    created_at  datetimeoffset NOT NULL CONSTRAINT subject_keys_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT subject_keys_pk PRIMARY KEY (tenant_id, subject_id)
);
GO

IF OBJECT_ID(N'[{{schema}}].[subject_streams]', N'U') IS NULL
CREATE TABLE [{{schema}}].[subject_streams] (
    tenant_id  nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT subject_streams_tenant_id DEFAULT N'',
    subject_id nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    stream_id  nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT subject_streams_pk PRIMARY KEY (tenant_id, subject_id, stream_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 1)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (1);
GO
