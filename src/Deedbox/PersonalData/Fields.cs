using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Deedbox;

/// <summary>One [PersonalData] property of an event type: its JSON name and the JSON name of its subject.</summary>
internal sealed record PersonalField(string Property, string JsonName, string SubjectJsonName, bool IsString);

/// <summary>Finds an event type's [PersonalData] properties. Only top-level properties are encrypted.</summary>
internal static class PersonalFields
{
    public static List<PropertyInfo> Properties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)];

    public static List<PersonalField> Map(Type type, IReadOnlyList<PropertyInfo> properties, JsonSerializerOptions options)
    {
        var fields = new List<PersonalField>();
        var subjects = properties.Where(p => p.GetCustomAttribute<DataSubjectAttribute>() is not null).ToList();
        foreach (var subject in subjects)
            RequireString(type, subject, "[DataSubject]");

        foreach (var property in properties)
        {
            var personal = property.GetCustomAttribute<PersonalDataAttribute>();
            if (personal is null)
                continue;

            var nullable = !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null;
            if (!nullable)
            {
                throw new DeedboxException(Errors.InvalidPersonalData,
                    $"{type.Name}.{property.Name} is [PersonalData] but its type {property.PropertyType.Name} cannot hold null, which it reads as after erasure. Make it a string or nullable.");
            }

            PropertyInfo subject;
            if (personal.Subject is { } name)
            {
                subject = properties.FirstOrDefault(p => p.Name == name) ?? throw new DeedboxException(Errors.MissingSubject,
                    $"{type.Name}.{property.Name} names subject property '{name}', which {type.Name} does not have.");
                RequireString(type, subject, "A subject");
            }
            else
            {
                subject = subjects.Count == 1 ? subjects[0] : throw new DeedboxException(Errors.MissingSubject,
                    $"{type.Name}.{property.Name} is [PersonalData] but {type.Name} has {subjects.Count} [DataSubject] properties. " +
                    "Mark exactly one, or name the subject with [PersonalData(Subject = nameof(...))].");
            }

            fields.Add(new PersonalField(property.Name, JsonName(property, options), JsonName(subject, options), property.PropertyType == typeof(string)));
        }

        return fields;
    }

    private static void RequireString(Type type, PropertyInfo property, string what)
    {
        if (property.PropertyType != typeof(string))
            throw new DeedboxException(Errors.MissingSubject, $"{what} property {type.Name}.{property.Name} must be a string subject ID.");
    }

    private static string JsonName(PropertyInfo property, JsonSerializerOptions options) =>
        property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name
        ?? options.PropertyNamingPolicy?.ConvertName(property.Name)
        ?? property.Name;
}

/// <summary>Encrypts an event's personal fields on append, and reveals or redacts them on read.</summary>
internal static partial class FieldCipher
{
    /// <summary>The event as JSON with each non-null personal field replaced by an encrypted marker.</summary>
    public static async Task<(string Payload, List<string> Subjects)> Protect(object @event, EventRegistration registration, SubjectKeys keys, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(@event, registration.Json)?.AsObject()
            ?? throw new JsonException($"{registration.ClrType.Name} did not serialize to a JSON object.");

        var pending = new List<(PersonalField Field, JsonNode Value, string Subject)>();
        foreach (var field in registration.PersonalFields)
        {
            if (node[field.JsonName] is not { } value)
                continue;
            var subject = node[field.SubjectJsonName]?.GetValue<string>();
            if (string.IsNullOrEmpty(subject))
            {
                throw new DeedboxException(Errors.MissingSubject,
                    $"{registration.ClrType.Name}.{field.Property} has a value but its subject ID is empty, so it cannot be encrypted under a subject's key.");
            }

            pending.Add((field, value, subject));
        }

        // Keys are fetched in subject order so two appends never wait on each other's subject rows in opposite order.
        foreach (var subject in pending.Select(p => p.Subject).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            await keys.ForSubject(subject, ct);

        foreach (var (field, value, subject) in pending)
        {
            var (keyId, key) = await keys.ForSubject(subject, ct);
            node[field.JsonName] = new JsonObject { [Crypto.Marker] = Crypto.EncryptField(key, keyId, value.ToJsonString()) };
        }

        return (node.ToJsonString(), pending.Select(p => p.Subject).Distinct(StringComparer.Ordinal).ToList());
    }

    public static bool HasMarkers(string payload) => payload.Contains("\"$enc\"", StringComparison.Ordinal);

    public static IEnumerable<string> KeyIds(string payload) => MarkerKeyId().Matches(payload).Select(m => m.Groups[1].Value);

    /// <summary>
    /// Decrypts every marker anywhere in the JSON, before upcasting. A marker whose subject key is gone becomes null,
    /// or the placeholder when the current registration knows the field as a string.
    /// </summary>
    public static (string Payload, List<string> ErasedSubjects) Reveal(string payload, EventRegistration? registration, SubjectKeys keys, string? placeholder)
    {
        var root = JsonNode.Parse(payload) ?? throw new JsonException("Stored payload is null.");
        var erased = new List<string>();
        Walk(root, null, root, registration, keys, placeholder, erased);
        return (root.ToJsonString(), erased.Distinct(StringComparer.Ordinal).ToList());
    }

    private static void Walk(JsonNode node, string? topLevelName, JsonNode root, EventRegistration? registration, SubjectKeys keys, string? placeholder, List<string> erased)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                        continue;
                    var top = ReferenceEquals(obj, root) ? name : topLevelName;
                    if (TryMarker(child, out var marker))
                        obj[name] = Open(marker, top, root, registration, keys, placeholder, erased);
                    else
                        Walk(child, top, root, registration, keys, placeholder, erased);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not { } child)
                        continue;
                    if (TryMarker(child, out var marker))
                        array[i] = Open(marker, null, root, registration, keys, placeholder, erased);
                    else
                        Walk(child, topLevelName, root, registration, keys, placeholder, erased);
                }

                break;
        }
    }

    private static bool TryMarker(JsonNode node, out string marker)
    {
        marker = "";
        if (node is not JsonObject { Count: 1 } obj || obj[Crypto.Marker] is not JsonValue value || !value.TryGetValue(out string? text))
            return false;
        marker = text;
        return Crypto.FieldKeyId(text) is not null;
    }

    private static JsonNode? Open(string marker, string? topLevelName, JsonNode root, EventRegistration? registration, SubjectKeys keys, string? placeholder, List<string> erased)
    {
        var key = keys.ById(Crypto.FieldKeyId(marker)!);
        if (key is not null)
        {
            try
            {
                var revealed = JsonNode.Parse(Crypto.DecryptField(key, marker));
                DeedboxDiagnostics.Decrypts.Add(1);
                return revealed;
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new DeedboxException(Errors.KeyMaterialCorrupt,
                    "An encrypted personal-data field does not verify. The payload or its key was altered; Deedbox will not read it as erased.", ex);
            }
        }

        DeedboxDiagnostics.Redactions.Add(1);
        var field = registration?.PersonalFields.FirstOrDefault(f => f.JsonName == topLevelName);
        if (field is not null && root[field.SubjectJsonName]?.GetValue<string>() is { } subject)
            erased.Add(subject);
        return field is { IsString: true } && placeholder is not null ? JsonValue.Create(placeholder) : null;
    }

    [GeneratedRegex("\"\\$enc\"\\s*:\\s*\"v1:([0-9a-f]{32}):")]
    private static partial Regex MarkerKeyId();
}
