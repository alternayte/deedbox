-- One row per live app instance: what it runs and what it can append, so an inline projection never misses an
-- append from an instance that does not know it. Rows are refreshed every few seconds and removed on a clean stop.
IF OBJECT_ID(N'[{{schema}}].[instances]', N'U') IS NULL
CREATE TABLE [{{schema}}].[instances] (
    instance_id        uniqueidentifier NOT NULL,
    host               nvarchar(200)    NOT NULL,
    app                nvarchar(200)    NOT NULL,
    consumers          nvarchar(max)    NOT NULL,
    inline_projections nvarchar(max)    NOT NULL,
    event_types        nvarchar(max)    NOT NULL,
    started_at         datetimeoffset   NOT NULL CONSTRAINT instances_started_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    seen_at            datetimeoffset   NOT NULL CONSTRAINT instances_seen_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    CONSTRAINT instances_pk PRIMARY KEY (instance_id)
);
GO

-- The events each projection handles, so an instance without the projection's code can tell whether it would skip it.
IF COL_LENGTH(N'[{{schema}}].[checkpoints]', N'handles') IS NULL
ALTER TABLE [{{schema}}].[checkpoints] ADD handles nvarchar(max) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 4)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (4);
GO
