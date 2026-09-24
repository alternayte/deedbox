-- Reads find a subject key by the key ID in an encrypted field.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'subject_keys_key_id' AND object_id = OBJECT_ID(N'[{{schema}}].[subject_keys]'))
CREATE UNIQUE INDEX subject_keys_key_id ON [{{schema}}].[subject_keys] (tenant_id, key_id);
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 3)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (3);
GO
