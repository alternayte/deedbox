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
#if NET9_0_OR_GREATER
        // Postgres jsonb stores object keys in its own order, so "$type" and "$id" do not come back first.
        options.AllowOutOfOrderMetadataProperties = true;
#endif
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

    /// <summary>
    /// A member of <paramref name="type"/>, at any depth, whose JSON carries metadata properties that the serializer
    /// reads only when they come first: a polymorphic type's discriminator, or a preserved reference. Null when there is none.
    /// </summary>
    public string? NeedsKeyOrder(Type type)
    {
        // IgnoreCycles writes no "$id" or "$ref"; Preserve, and a handler of the app's own, may.
        if (Options.ReferenceHandler is { } handler && !ReferenceEquals(handler, System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles))
            return $"{type.Name} (the JSON options set a ReferenceHandler that preserves references)";
        return NeedsKeyOrder(type, []);
    }

    private string? NeedsKeyOrder(Type type, HashSet<Type> seen)
    {
        if (type.IsArray && type.GetElementType() is { } element && NeedsKeyOrder(element, seen) is { } inElement)
            return inElement;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (NeedsKeyOrder(argument, seen) is { } inArgument)
                    return inArgument;
            }
        }

        if (type.IsPrimitive || type == typeof(string) || !seen.Add(type))
            return null;

        JsonTypeInfo contract;
        try
        {
            contract = Options.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }

        if (contract.PolymorphismOptions is not null)
            return type.Name;
        if (contract.Kind != JsonTypeInfoKind.Object)
            return null;

        foreach (var property in contract.Properties)
        {
            if (NeedsKeyOrder(property.PropertyType, seen) is { } inProperty)
                return inProperty;
        }

        return null;
    }

    public static string Serialize(object value, JsonTypeInfo typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    public static object Deserialize(string json, JsonTypeInfo typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new JsonException($"Stored JSON for {typeInfo.Type.Name} is null.");

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Used only when the app has reflection-based JSON enabled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Used only when the app has reflection-based JSON enabled.")]
    private static DefaultJsonTypeInfoResolver? ReflectionResolver() =>
        JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;
}
