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
    public const string NoKeyMode = "DBX025";
    public const string InvalidPersonalData = "DBX026";
    public const string MissingSubject = "DBX027";
    public const string StreamDeleted = "DBX028";
    public const string MasterKeyUnusable = "DBX029";
    public const string KeyMaterialCorrupt = "DBX030";
    public const string BuiltInEvent = "DBX031";
    public const string QueueBoxMapping = "DBX032";
    public const string UnknownConsumer = "DBX033";

    /// <summary>The error catalogue: each code's page title. The docs build one page per entry; a test checks every code has one.</summary>
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [SchemaBehind] = "The Deedbox schema is missing or older than this build",
        [NoProvider] = "No database provider is configured",
        [InvalidSchemaName] = "The schema name is not valid",
        [DuplicateStream] = "A stream type or state type is registered twice",
        [DuplicateEvent] = "An event type or stored name is registered twice",
        [UnregisteredEvent] = "An event type is not registered",
        [UnknownStoredEvent] = "A stored event type has no registered CLR type",
        [StreamTypeMismatch] = "A stream belongs to another stream type",
        [UnregisteredState] = "A state type or stream type is not registered",
        [MixedStreamTypes] = "One append holds events of several stream types",
        [JsonReflectionDisabled] = "No JSON contract for a type",
        [DbContextConnectionMismatch] = "DbContexts do not share one connection",
        [ProviderAlreadySet] = "A database provider is configured twice",
        [Conflict] = "The stream is at another version",
        [InvalidName] = "A stored name is not valid",
        [UnmappedStoredEvent] = "Stored events have no mapping",
        [StoredVersionAhead] = "Stored events are newer than this build",
        [MissingUpcaster] = "An event version has no upcaster",
        [StoredStreamTypeMismatch] = "Stored events belong to another stream type",
        [DuplicateProjection] = "A projection or subscription is registered twice",
        [ProjectionHandlesUnregistered] = "A handler handles an unregistered event",
        [InvalidTenant] = "The tenant ID is not valid",
        [ResetNotImplemented] = "A projection cannot be rebuilt without ResetAsync",
        [InlineBatchProjection] = "A batch projection is registered inline",
        [NoKeyMode] = "Personal data needs a key mode",
        [InvalidPersonalData] = "A personal-data property cannot hold null",
        [MissingSubject] = "A personal-data property has no subject",
        [StreamDeleted] = "The stream was deleted",
        [MasterKeyUnusable] = "The master key cannot unwrap a key",
        [KeyMaterialCorrupt] = "Encrypted data or a key does not verify",
        [BuiltInEvent] = "A built-in event was appended or registered",
        [QueueBoxMapping] = "A QueueBox publication is not valid",
        [UnknownConsumer] = "A projection or subscription name is not registered",
    };
}
