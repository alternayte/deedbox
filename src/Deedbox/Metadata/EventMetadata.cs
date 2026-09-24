using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deedbox;

/// <summary>
/// Who and what caused an event. Metadata is stored as plain JSON and never encrypted: use pseudonymous IDs
/// such as <c>user:123</c>, and keep personal data out of headers.
/// </summary>
public sealed record EventMetadata
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>No metadata.</summary>
    public static EventMetadata Empty { get; } = new();

    /// <summary>Ties together every event caused by one request or workflow.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The ID of the event or command that caused this event. Handlers that append set it to the triggering event's ID.</summary>
    public string? CausationId { get; init; }

    /// <summary>Who caused the event, as a pseudonymous ID such as <c>user:123</c>.</summary>
    public string? Actor { get; init; }

    /// <summary>The W3C trace context of the append, taken from <see cref="System.Diagnostics.Activity.Current"/>.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Any other values, as strings.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    /// <summary>Value equality, with headers compared by content.</summary>
    /// <param name="other">The other metadata.</param>
    public bool Equals(EventMetadata? other) =>
        other is not null
        && CorrelationId == other.CorrelationId
        && CausationId == other.CausationId
        && Actor == other.Actor
        && TraceParent == other.TraceParent
        && Headers.Count == other.Headers.Count
        && Headers.All(h => other.Headers.TryGetValue(h.Key, out var value) && value == h.Value);

    /// <summary>A hash of the first-class fields.</summary>
    public override int GetHashCode() => HashCode.Combine(CorrelationId, CausationId, Actor, TraceParent, Headers.Count);

    internal string ToJson() => JsonSerializer.Serialize(
        new StoredMetadata(CorrelationId, CausationId, Actor, TraceParent, Headers.Count == 0 ? null : new Dictionary<string, string>(Headers)),
        MetadataJson.Default.StoredMetadata);

    internal static EventMetadata FromJson(string json)
    {
        if (json is "{}" or "")
            return Empty;
        var stored = JsonSerializer.Deserialize(json, MetadataJson.Default.StoredMetadata);
        return stored is null ? Empty : new EventMetadata
        {
            CorrelationId = stored.CorrelationId,
            CausationId = stored.CausationId,
            Actor = stored.Actor,
            TraceParent = stored.TraceParent,
            Headers = stored.Headers is null ? NoHeaders : new ReadOnlyDictionary<string, string>(stored.Headers),
        };
    }
}

/// <summary>The stored JSON form: absent fields and empty headers are left out.</summary>
internal sealed record StoredMetadata(string? CorrelationId, string? CausationId, string? Actor, string? TraceParent, Dictionary<string, string>? Headers);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredMetadata))]
internal sealed partial class MetadataJson : JsonSerializerContext;
