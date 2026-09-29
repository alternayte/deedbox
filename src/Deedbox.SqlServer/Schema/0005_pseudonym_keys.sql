-- One secret per tenant and period for pseudonymous subject IDs, wrapped by the master key. A destroyed period keeps
-- its row as a tombstone with no key material, so the period can never get a new secret.
IF OBJECT_ID(N'[{{schema}}].[pseudonym_keys]', N'U') IS NULL
CREATE TABLE [{{schema}}].[pseudonym_keys] (
    tenant_id    nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT pseudonym_keys_tenant_id DEFAULT N'',
    period_id    nvarchar(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    prefix       nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    wrapped_key  varbinary(max) NOT NULL,
    wrapped_by   nvarchar(200)  NOT NULL,
    created_at   datetimeoffset NOT NULL CONSTRAINT pseudonym_keys_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), 0),
    destroyed_at datetimeoffset NULL,
    CONSTRAINT pseudonym_keys_pk PRIMARY KEY (tenant_id, period_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE version = 5)
INSERT INTO [{{schema}}].[schema_version] (version) VALUES (5);
GO
