-- Deleting a stream removes its subject pairs by stream; the primary key leads with the subject.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'subject_streams_by_stream' AND object_id = OBJECT_ID(N'[{{schema}}].[subject_streams]'))
CREATE INDEX subject_streams_by_stream ON [{{schema}}].[subject_streams] (tenant_id, stream_id);
GO

-- The job loop claims the oldest queued job; finished jobs stay as the audit trail and must not be scanned.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'jobs_queued' AND object_id = OBJECT_ID(N'[{{schema}}].[jobs]'))
CREATE INDEX jobs_queued ON [{{schema}}].[jobs] (created_at, id) WHERE status = N'queued';
GO

-- Stall and job records no longer keep exception messages: a handler's or the database's message can hold personal
-- data, and these rows outlive an erasure. Deedbox's own job rejections name no data and stay.
UPDATE [{{schema}}].[checkpoints]
SET error = JSON_MODIFY(JSON_MODIFY(CAST(error AS nvarchar(max)), '$.message', NULL), '$.stackTrace', NULL)
WHERE error IS NOT NULL
  AND (CAST(error AS nvarchar(max)) LIKE N'%"message"%' OR CAST(error AS nvarchar(max)) LIKE N'%"stackTrace"%');
GO

UPDATE [{{schema}}].[jobs]
SET progress = JSON_MODIFY(JSON_MODIFY(CAST(progress AS nvarchar(max)), '$.stall.message', NULL), '$.stall.stackTrace', NULL)
WHERE progress IS NOT NULL AND CAST(progress AS nvarchar(max)) LIKE N'%"stall"%'
  AND (CAST(progress AS nvarchar(max)) LIKE N'%"message"%' OR CAST(progress AS nvarchar(max)) LIKE N'%"stackTrace"%');
GO

UPDATE [{{schema}}].[jobs]
SET progress = JSON_MODIFY(CAST(progress AS nvarchar(max)), '$.error', NULL)
WHERE status = N'failed' AND progress IS NOT NULL
  AND ISNULL(JSON_VALUE(CAST(progress AS nvarchar(max)), '$.exception'), N'') NOT IN (N'Deedbox.JobRejected', N'Deedbox.DeedboxException');
GO

-- The newest storage format an instance can read. A version from before this column reads only format 1, and nobody
-- writes a newer format while such an instance has a heartbeat row.
IF COL_LENGTH(N'[{{schema}}].[instances]', N'formats') IS NULL
ALTER TABLE [{{schema}}].[instances] ADD formats int NOT NULL CONSTRAINT instances_formats DEFAULT 1;
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 6)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (6);
GO
