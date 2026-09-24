-- SQL Server has no push notification Deedbox uses; async runners poll with backoff.
IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 2)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (2);
GO
