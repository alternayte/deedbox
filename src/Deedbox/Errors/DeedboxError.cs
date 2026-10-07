namespace Deedbox;

/// <summary>
/// Every code that <see cref="DeedboxException.Code"/> can hold. Compare against these constants instead of text, such as
/// <c>catch (DeedboxException e) when (e.Code == DeedboxError.StreamDeleted)</c>. A code is never reused for another error.
/// </summary>
public static class DeedboxError
{
    /// <summary>DBX001: The Deedbox schema is missing or older than this build.</summary>
    public const string SchemaBehind = Errors.SchemaBehind;

    /// <summary>DBX002: No database provider is configured.</summary>
    public const string NoProvider = Errors.NoProvider;

    /// <summary>DBX003: The schema name is not valid.</summary>
    public const string InvalidSchemaName = Errors.InvalidSchemaName;

    /// <summary>DBX004: A stream type or state type is registered twice.</summary>
    public const string DuplicateStream = Errors.DuplicateStream;

    /// <summary>DBX005: An event type or stored name is registered twice.</summary>
    public const string DuplicateEvent = Errors.DuplicateEvent;

    /// <summary>DBX006: An event type is not registered.</summary>
    public const string UnregisteredEvent = Errors.UnregisteredEvent;

    /// <summary>DBX007: A stored event type has no registered CLR type.</summary>
    public const string UnknownStoredEvent = Errors.UnknownStoredEvent;

    /// <summary>DBX008: A stream belongs to another stream type.</summary>
    public const string StreamTypeMismatch = Errors.StreamTypeMismatch;

    /// <summary>DBX009: A state type or stream type is not registered.</summary>
    public const string UnregisteredState = Errors.UnregisteredState;

    /// <summary>DBX010: One append holds events of several stream types.</summary>
    public const string MixedStreamTypes = Errors.MixedStreamTypes;

    /// <summary>DBX011: No JSON contract for a type.</summary>
    public const string JsonReflectionDisabled = Errors.JsonReflectionDisabled;

    /// <summary>DBX012: DbContexts do not share one connection.</summary>
    public const string DbContextConnectionMismatch = Errors.DbContextConnectionMismatch;

    /// <summary>DBX013: A database provider is configured twice.</summary>
    public const string ProviderAlreadySet = Errors.ProviderAlreadySet;

    /// <summary>DBX014: The stream is at another version.</summary>
    public const string Conflict = Errors.Conflict;

    /// <summary>DBX015: A stored name is not valid.</summary>
    public const string InvalidName = Errors.InvalidName;

    /// <summary>DBX016: Stored events have no mapping.</summary>
    public const string UnmappedStoredEvent = Errors.UnmappedStoredEvent;

    /// <summary>DBX017: Stored events are newer than this build.</summary>
    public const string StoredVersionAhead = Errors.StoredVersionAhead;

    /// <summary>DBX018: An event version has no upcaster.</summary>
    public const string MissingUpcaster = Errors.MissingUpcaster;

    /// <summary>DBX019: Stored events belong to another stream type.</summary>
    public const string StoredStreamTypeMismatch = Errors.StoredStreamTypeMismatch;

    /// <summary>DBX020: A projection or subscription is registered twice.</summary>
    public const string DuplicateProjection = Errors.DuplicateProjection;

    /// <summary>DBX021: A handler handles an unregistered event.</summary>
    public const string ProjectionHandlesUnregistered = Errors.ProjectionHandlesUnregistered;

    /// <summary>DBX022: The tenant ID is not valid.</summary>
    public const string InvalidTenant = Errors.InvalidTenant;

    /// <summary>DBX023: A projection cannot be rebuilt without ResetAsync.</summary>
    public const string ResetNotImplemented = Errors.ResetNotImplemented;

    /// <summary>DBX024: A batch projection is registered inline.</summary>
    public const string InlineBatchProjection = Errors.InlineBatchProjection;

    /// <summary>DBX025: Personal data needs a key mode.</summary>
    public const string NoKeyMode = Errors.NoKeyMode;

    /// <summary>DBX026: A personal-data property cannot be encrypted.</summary>
    public const string InvalidPersonalData = Errors.InvalidPersonalData;

    /// <summary>DBX027: A personal-data property has no subject.</summary>
    public const string MissingSubject = Errors.MissingSubject;

    /// <summary>DBX028: The stream was deleted.</summary>
    public const string StreamDeleted = Errors.StreamDeleted;

    /// <summary>DBX029: The master key cannot unwrap a key.</summary>
    public const string MasterKeyUnusable = Errors.MasterKeyUnusable;

    /// <summary>DBX030: Encrypted data or a key does not verify.</summary>
    public const string KeyMaterialCorrupt = Errors.KeyMaterialCorrupt;

    /// <summary>DBX031: A built-in event was appended or registered.</summary>
    public const string BuiltInEvent = Errors.BuiltInEvent;

    /// <summary>DBX032: A QueueBox publication is not valid.</summary>
    public const string QueueBoxMapping = Errors.QueueBoxMapping;

    /// <summary>DBX033: A projection or subscription name is not registered.</summary>
    public const string UnknownConsumer = Errors.UnknownConsumer;

    /// <summary>DBX034: Native json columns are not available or not applied.</summary>
    public const string StorageOptions = Errors.StorageOptions;

    /// <summary>DBX035: A live instance still registers the projection.</summary>
    public const string ProjectionInUse = Errors.ProjectionInUse;

    /// <summary>DBX036: The pseudonym period's secret was destroyed.</summary>
    public const string PseudonymPeriodDestroyed = Errors.PseudonymPeriodDestroyed;

    /// <summary>DBX037: The pseudonym prefix differs from the period's prefix.</summary>
    public const string PseudonymPrefixChanged = Errors.PseudonymPrefixChanged;

    /// <summary>DBX038: This instance was not counted as live.</summary>
    public const string InstanceEvicted = Errors.InstanceEvicted;

    /// <summary>DBX039: Polymorphic JSON cannot be read back on this database.</summary>
    public const string JsonKeyOrder = Errors.JsonKeyOrder;

    /// <summary>DBX040: The transaction's isolation level is not READ COMMITTED.</summary>
    public const string IsolationLevel = Errors.IsolationLevel;
}
