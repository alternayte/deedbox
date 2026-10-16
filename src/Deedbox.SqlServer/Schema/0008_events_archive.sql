-- The events of archived streams, moved out of events by the archive job. Same columns and keys as events, so global
-- positions and event IDs stay unique across both tables.
IF OBJECT_ID(N'[{{schema}}].[events_archive]', N'U') IS NULL
CREATE TABLE [{{schema}}].[events_archive] (
    global_position bigint           NOT NULL,
    event_id        uniqueidentifier NOT NULL,
    tenant_id       nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT events_archive_tenant_id DEFAULT N'',
    stream_id       nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    version         bigint           NOT NULL,
    stream_type     nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_type      nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    event_version   int              NOT NULL,
    payload         nvarchar(max)    NOT NULL,
    metadata        nvarchar(max)    NOT NULL,
    occurred_at     datetimeoffset   NOT NULL,
    CONSTRAINT events_archive_pk PRIMARY KEY CLUSTERED (global_position),
    CONSTRAINT events_archive_event_id UNIQUE (event_id),
    CONSTRAINT events_archive_stream_version UNIQUE (tenant_id, stream_id, version)
);
GO

-- The highest stream version whose event is in events_archive; 0 when the stream was never archived.
IF COL_LENGTH(N'[{{schema}}].[streams]', N'archived_to') IS NULL
ALTER TABLE [{{schema}}].[streams] ADD archived_to bigint NOT NULL CONSTRAINT streams_archived_to DEFAULT 0;
GO

-- A rebuild that includes archived events reads both tables while its position is below this; null reads events only.
IF COL_LENGTH(N'[{{schema}}].[checkpoints]', N'archive_until') IS NULL
ALTER TABLE [{{schema}}].[checkpoints] ADD archive_until bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 8)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (8);
GO
