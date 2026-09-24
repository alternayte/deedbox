using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Deedbox;

/// <summary>
/// Deedbox's own JSON options. They never come from the app's global options, so changing the app's
/// JSON settings never changes how stored events read.
/// </summary>
internal sealed class DeedboxJson
{
    public DeedboxJson(IReadOnlyList<IJsonTypeInfoResolver> contexts, Action<JsonSerializerOptions>? configure)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        configure?.Invoke(options);

        if (contexts.Count > 0)
        {
            options.TypeInfoResolver = options.TypeInfoResolver is { } configured
                ? JsonTypeInfoResolver.Combine([.. contexts, configured])
                : JsonTypeInfoResolver.Combine([.. contexts]);
        }
        else
        {
            options.TypeInfoResolver ??= ReflectionResolver();
        }

        if (options.TypeInfoResolver is null)
        {
            throw new DeedboxException(Errors.JsonReflectionDisabled,
                "Reflection-based JSON is disabled in this app (trimmed or native AOT). Call UseJsonContext(...) with a JsonSerializerContext that includes every event and state type.");
        }

        options.MakeReadOnly();
        Options = options;
    }

    public JsonSerializerOptions Options { get; }

    public JsonTypeInfo TypeInfo(Type type)
    {
        try
        {
            return Options.GetTypeInfo(type);
        }
        catch (NotSupportedException ex)
        {
            throw new DeedboxException(Errors.JsonReflectionDisabled,
                $"No JSON contract for {type.Name}. Add [JsonSerializable(typeof({type.Name}))] to the context passed to UseJsonContext(...).", ex);
        }
    }

    public static string Serialize(object value, JsonTypeInfo typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    public static object Deserialize(string json, JsonTypeInfo typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new JsonException($"Stored JSON for {typeInfo.Type.Name} is null.");

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Used only when the app has reflection-based JSON enabled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Used only when the app has reflection-based JSON enabled.")]
    private static DefaultJsonTypeInfoResolver? ReflectionResolver() =>
        JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;
}
