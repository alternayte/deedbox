using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Deedbox;

/// <summary>One [PersonalData] property of an event type: its JSON name and the JSON name of its subject.</summary>
internal sealed record PersonalField(string Property, string JsonName, string SubjectJsonName, bool IsString);

/// <summary>
/// Finds an event type's [PersonalData] properties. Only top-level properties are encrypted. The JSON names come from
/// the serializer's own contract, never from a guess: a field that the append cannot find in the JSON is stored in plain
/// text, so every way a property can miss the contract fails here, at start-up.
/// </summary>
internal static class PersonalFields
{
    public static List<PropertyInfo> Properties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)];

    public static List<PersonalField> Map(Type type, IReadOnlyList<PropertyInfo> properties, JsonTypeInfo contract)
    {
        var written = contract.Kind == JsonTypeInfoKind.Object ? contract.Properties.Where(p => p.Get is not null).ToList() : [];
        var byMember = new Dictionary<string, JsonPropertyInfo>(StringComparer.Ordinal);
        foreach (var property in written)
        {
            if (property.AttributeProvider is MemberInfo member)
                byMember.TryAdd(member.Name, property);
        }

        // A contract from an older source generator names no members; the JSON name is then derived and must exist.
        JsonPropertyInfo? Find(MemberInfo member) =>
            byMember.Count > 0
                ? byMember.GetValueOrDefault(member.Name)
                : written.FirstOrDefault(p => p.Name == (member.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name
                    ?? contract.Options.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name));

        // Public properties, and any other member the contract writes, such as a private property with [JsonInclude].
        var members = properties.Cast<MemberInfo>()
            .Concat(byMember.Values.Select(p => (MemberInfo)p.AttributeProvider!))
            .DistinctBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        JsonPropertyInfo Subject(MemberInfo member, string what)
        {
            var json = Find(member) ?? throw new DeedboxException(Errors.MissingSubject,
                $"{what} property {type.Name}.{member.Name} is not written to the event's JSON, so no personal data can be stored under it.");
            if (json.PropertyType != typeof(string))
                throw new DeedboxException(Errors.MissingSubject, $"{what} property {type.Name}.{member.Name} must be a string subject ID.");
            return json;
        }

        var subjects = members.Where(m => m.GetCustomAttribute<DataSubjectAttribute>() is not null).Select(m => Subject(m, "[DataSubject]")).ToList();
        var fields = new List<PersonalField>();
        foreach (var member in members)
        {
            var personal = member.GetCustomAttribute<PersonalDataAttribute>();
            if (personal is null)
                continue;

            var json = Find(member);
            if (json is null)
            {
                // A property the serializer never writes holds nothing to encrypt.
                if (contract.Kind == JsonTypeInfoKind.Object && contract.Properties.Any(p => p.Get is null && (p.AttributeProvider as MemberInfo)?.Name == member.Name))
                    continue;

                throw new DeedboxException(Errors.InvalidPersonalData,
                    $"{type.Name}.{member.Name} is [PersonalData], but the JSON contract of {type.Name} does not write it as a property of the event, so Deedbox cannot encrypt it. " +
                    "A custom converter or a contract change hides it. Serialize the event as a plain object, or remove the attribute.");
            }

            var nullable = !json.PropertyType.IsValueType || Nullable.GetUnderlyingType(json.PropertyType) is not null;
            if (!nullable)
            {
                throw new DeedboxException(Errors.InvalidPersonalData,
                    $"{type.Name}.{member.Name} is [PersonalData] but its type {json.PropertyType.Name} cannot hold null, which it reads as after erasure. Make it a string or nullable.");
            }

            JsonPropertyInfo subject;
            if (personal.Subject is { } name)
            {
                var named = members.FirstOrDefault(m => m.Name == name) ?? throw new DeedboxException(Errors.MissingSubject,
                    $"{type.Name}.{member.Name} names subject property '{name}', which {type.Name} does not have.");
                subject = Subject(named, "A subject");
            }
            else
            {
                subject = subjects.Count == 1 ? subjects[0] : throw new DeedboxException(Errors.MissingSubject,
                    $"{type.Name}.{member.Name} is [PersonalData] but {type.Name} has {subjects.Count} [DataSubject] properties. " +
                    "Mark exactly one, or name the subject with [PersonalData(Subject = nameof(...))].");
            }

            fields.Add(new PersonalField(member.Name, json.Name, subject.Name, json.PropertyType == typeof(string)));
        }

        // A property that is itself personal data is encrypted whole, so what its type marks inside is covered.
        var whole = fields.Select(f => f.JsonName).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<Type> { type };
        foreach (var property in written.Where(p => !whole.Contains(p.Name)))
            RejectNested(type, MemberName(property), property.PropertyType, contract.Options, seen);
        return fields;
    }

    private static string MemberName(JsonPropertyInfo property) => (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;

    /// <summary>
    /// Fails when a type inside the event carries the attributes. Only the event's own properties are encrypted, so a
    /// marked member of a nested type would be stored in plain text while its attribute says otherwise.
    /// </summary>
    private static void RejectNested(Type eventType, string path, Type type, JsonSerializerOptions options, HashSet<Type> seen)
    {
        if (type.IsArray && type.GetElementType() is { } element)
            RejectNested(eventType, path, element, options, seen);
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                RejectNested(eventType, path, argument, options, seen);
        }

        if (type.IsPrimitive || type == typeof(string) || !seen.Add(type))
            return;

        JsonTypeInfo contract;
        try
        {
            contract = options.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return; // No contract, so the serializer cannot write the type as an object either.
        }

        if (contract.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var property in contract.Properties)
        {
            if (property.AttributeProvider is { } attributes
                && (attributes.IsDefined(typeof(PersonalDataAttribute), true) || attributes.IsDefined(typeof(DataSubjectAttribute), true)))
            {
                throw new DeedboxException(Errors.InvalidPersonalData,
                    $"{type.Name}.{MemberName(property)} is marked as personal data, but {type.Name} is nested in {eventType.Name}.{path}. " +
                    $"Deedbox encrypts only the top-level properties of an event, so it would be stored in plain text. Move the property to {eventType.Name}, or mark {eventType.Name}.{path} itself.");
            }

            RejectNested(eventType, path, property.PropertyType, options, seen);
        }
    }
}

/// <summary>Encrypts an event's personal fields on append, and reveals or redacts them on read.</summary>
internal static partial class FieldCipher
{
    /// <summary>The event as JSON with each non-null personal field replaced by an encrypted marker.</summary>
    public static async Task<(string Payload, List<string> Subjects)> Protect(object @event, Guid eventId, EventRegistration registration, SubjectKeys keys, int format, CancellationToken ct)
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

            if (!SubjectId.IsValid(subject))
            {
                throw new DeedboxException(Errors.MissingSubject,
                    $"The subject ID of {registration.ClrType.Name}.{field.Property} is not valid. Use 1 to {SubjectId.MaxLength} characters with no leading or trailing white space.");
            }

            pending.Add((field, value, subject));
        }

        // Keys are fetched in subject order so two appends never wait on each other's subject rows in opposite order.
        foreach (var subject in pending.Select(p => p.Subject).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            await keys.ForSubject(subject, ct);

        foreach (var (field, value, subject) in pending)
        {
            var (keyId, key) = await keys.ForSubject(subject, ct);
            node[field.JsonName] = new JsonObject { [Crypto.Marker] = Crypto.EncryptField(key, keyId, value.ToJsonString(), eventId, field.JsonName, format) };
        }

        return (node.ToJsonString(), pending.Select(p => p.Subject).Distinct(StringComparer.Ordinal).ToList());
    }

    public static bool HasMarkers(string payload) => payload.Contains("\"$enc\"", StringComparison.Ordinal);

    public static IEnumerable<string> KeyIds(string payload) => MarkerKeyId().Matches(payload).Select(m => m.Groups[1].Value);

    /// <summary>
    /// Decrypts each marker, before upcasting. A marker is the whole value of a top-level property: that is the only
    /// place an append writes one, and its name is part of what a format 2 field verifies against. A marker whose
    /// subject key is gone becomes null, or the placeholder when the current registration knows the field as a string.
    /// </summary>
    public static (string Payload, List<string> ErasedSubjects) Reveal(string payload, Guid eventId, EventRegistration? registration, SubjectKeys keys, string? placeholder)
    {
        var root = JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException("Stored payload is null.");
        var erased = new List<string>();
        foreach (var (name, child) in root.ToList())
        {
            if (child is not null && TryMarker(child, out var marker))
                root[name] = Open(marker, eventId, name, root, registration, keys, placeholder, erased);
        }

        return (root.ToJsonString(), erased.Distinct(StringComparer.Ordinal).ToList());
    }

    private static bool TryMarker(JsonNode node, out string marker)
    {
        marker = "";
        if (node is not JsonObject { Count: 1 } obj || obj[Crypto.Marker] is not JsonValue value || !value.TryGetValue(out string? text))
            return false;
        marker = text;
        return Crypto.FieldKeyId(text) is not null;
    }

    private static JsonNode? Open(string marker, Guid eventId, string name, JsonObject root, EventRegistration? registration, SubjectKeys keys, string? placeholder, List<string> erased)
    {
        var key = keys.ById(Crypto.FieldKeyId(marker)!);
        if (key is not null)
        {
            try
            {
                var revealed = JsonNode.Parse(Crypto.DecryptField(key, marker, eventId, name));
                DeedboxDiagnostics.Decrypts.Add(1);
                return revealed;
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new DeedboxException(Errors.KeyMaterialCorrupt,
                    "An encrypted personal-data field does not verify. The payload or its key was altered, the field was copied from another event or property, " +
                    "or a newer version of Deedbox wrote it. Deedbox will not read it as erased.", ex);
            }
        }

        DeedboxDiagnostics.Redactions.Add(1);
        var field = registration?.PersonalFields.FirstOrDefault(f => f.JsonName == name);
        if (field is not null && root[field.SubjectJsonName]?.GetValue<string>() is { } subject)
            erased.Add(subject);
        return field is { IsString: true } && placeholder is not null ? JsonValue.Create(placeholder) : null;
    }

    [GeneratedRegex("\"\\$enc\"\\s*:\\s*\"v[1-9]:([0-9a-f]{32}):")]
    private static partial Regex MarkerKeyId();
}
