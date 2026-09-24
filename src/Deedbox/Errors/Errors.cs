namespace Deedbox;

/// <summary>Every DBX code in one place, so a code is never reused.</summary>
internal static class Errors
{
    public const string SchemaBehind = "DBX001";
    public const string NoProvider = "DBX002";
    public const string InvalidSchemaName = "DBX003";
    public const string DuplicateStream = "DBX004";
    public const string DuplicateEvent = "DBX005";
    public const string UnregisteredEvent = "DBX006";
    public const string UnknownStoredEvent = "DBX007";
    public const string StreamTypeMismatch = "DBX008";
    public const string UnregisteredState = "DBX009";
    public const string MixedStreamTypes = "DBX010";
    public const string JsonReflectionDisabled = "DBX011";
    public const string DbContextConnectionMismatch = "DBX012";
    public const string ProviderAlreadySet = "DBX013";
    public const string Conflict = "DBX014";
    public const string InvalidName = "DBX015";
    public const string UnmappedStoredEvent = "DBX016";
    public const string StoredVersionAhead = "DBX017";
    public const string MissingUpcaster = "DBX018";
    public const string StoredStreamTypeMismatch = "DBX019";
    public const string DuplicateProjection = "DBX020";
    public const string ProjectionHandlesUnregistered = "DBX021";
    public const string InvalidTenant = "DBX022";
    public const string ResetNotImplemented = "DBX023";
    public const string InlineBatchProjection = "DBX024";
}
