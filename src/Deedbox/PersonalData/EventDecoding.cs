using System.Data.Common;

namespace Deedbox;

/// <summary>A stored event that could not be turned into its CLR type.</summary>
internal sealed class DecodeFailure(StoredEvent stored, Exception inner)
    : Exception($"Stored event {stored.EventId} at position {stored.GlobalPosition} cannot be decoded.", inner)
{
    public StoredEvent Stored { get; } = stored;
}

internal sealed record DecodedEvent(StoredEvent Stored, object Event, IReadOnlyList<string> ErasedSubjects);

/// <summary>Turns stored events into CLR events: decrypt personal data (one key query per tenant), then upcast and deserialize.</summary>
internal static class EventDecoding
{
    public static async Task<List<DecodedEvent>> Decode(DeedboxRuntime runtime, DbConnection connection, DbTransaction? transaction, IReadOnlyList<StoredEvent> stored, CancellationToken ct)
    {
        var decoded = new List<DecodedEvent>(stored.Count);
        var keys = new Dictionary<string, SubjectKeys>(StringComparer.Ordinal);

        foreach (var tenant in stored.Where(e => e.Payload is not null && FieldCipher.HasMarkers(e.Payload)).GroupBy(e => e.TenantId))
        {
            var tenantKeys = new SubjectKeys(runtime.RequireKeys(), runtime.Provider, connection, transaction, tenant.Key);
            await tenantKeys.Prefetch(tenant.SelectMany(e => FieldCipher.KeyIds(e.Payload!)), ct);
            keys[tenant.Key] = tenantKeys;
        }

        foreach (var e in stored)
        {
            if (e.Payload is null)
                continue;

            try
            {
                var payload = e.Payload;
                IReadOnlyList<string> erased = [];
                if (keys.TryGetValue(e.TenantId, out var tenantKeys) && FieldCipher.HasMarkers(payload))
                    (payload, erased) = FieldCipher.Reveal(payload, runtime.Registry.FindStoredName(e.EventType), tenantKeys, runtime.Options.RedactedPlaceholder);
                decoded.Add(new DecodedEvent(e, runtime.Registry.Decode(e.EventType, e.EventVersion, payload), erased));
            }
            catch (Exception ex) when (ex is not DeedboxException { Code: Errors.KeyMaterialCorrupt or Errors.MasterKeyUnusable or Errors.NoKeyMode })
            {
                throw new DecodeFailure(e, ex);
            }
        }

        return decoded;
    }
}
