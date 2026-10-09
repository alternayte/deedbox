-- The events each instance handles per projection and subscription. An instance of a version that handles fewer events
-- leaves a checkpoint alone while a live instance handles more, so it never moves the checkpoint past their events.
IF COL_LENGTH(N'[{{schema}}].[instances]', N'handles') IS NULL
ALTER TABLE [{{schema}}].[instances] ADD handles nvarchar(max) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 7)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (7);
GO
