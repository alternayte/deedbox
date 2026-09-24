namespace Deedbox;

/// <summary>
/// The tenant and metadata for everything one scope appends and loads. It is scoped: set it once per request,
/// for example in middleware, and every store resolved in that scope uses it.
/// </summary>
public sealed class DeedboxContext
{
    internal const int MaxTenantIdLength = 100;

    /// <summary>The tenant every load and append in this scope belongs to. Empty, the default, means no tenants.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The metadata every append in this scope stores, unless an append overrides it.</summary>
    public EventMetadata Metadata { get; set; } = EventMetadata.Empty;

    /// <summary>
    /// Makes this scope act on behalf of <paramref name="envelope"/>: its tenant, its correlation ID, and
    /// the event as the cause of anything the scope appends.
    /// </summary>
    /// <param name="envelope">The event being handled.</param>
    public void CausedBy(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        TenantId = envelope.TenantId;
        Metadata = new EventMetadata
        {
            CorrelationId = envelope.Metadata.CorrelationId,
            CausationId = envelope.EventId.ToString("D"),
            Actor = envelope.Metadata.Actor,
        };
    }

    internal static string ValidTenant(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (tenantId.Length > MaxTenantIdLength || (tenantId.Length > 0 && (char.IsWhiteSpace(tenantId[0]) || char.IsWhiteSpace(tenantId[^1]))))
        {
            throw new DeedboxException(Errors.InvalidTenant,
                $"Tenant ID '{tenantId}' is not valid. Use at most {MaxTenantIdLength} characters with no leading or trailing white space.");
        }

        return tenantId;
    }
}
