using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Deedbox;

internal sealed class StreamRegistration(string name, Type stateType, Func<object> initial, Func<object, object, object> evolve)
{
    public string Name { get; } = name;
    public Type StateType { get; } = stateType;
    public Func<object> Initial { get; } = initial;
    public Func<object, object, object> Evolve { get; } = evolve;
    public int StateVersion { get; set; } = 1;
    public SnapshotPolicy Snapshots { get; set; } = SnapshotPolicy.EveryAppend;
    public List<EventRegistration> Events { get; } = [];
    public JsonTypeInfo StateJson { get; set; } = null!;

    public object Fold(object state, IEnumerable<object> events)
    {
        foreach (var e in events)
            state = Evolve(state, e);
        return state;
    }
}

internal sealed class EventRegistration(Type clrType, string name, StreamRegistration stream)
{
    public Type ClrType { get; } = clrType;
    public string Name { get; set; } = name;
    public int Version { get; set; } = 1;
    public StreamRegistration Stream { get; } = stream;
    public List<string> Aliases { get; } = [];

    /// <summary>Upcast steps keyed by the version each one starts from.</summary>
    public SortedDictionary<int, UpcastStep> Steps { get; } = [];

    public JsonTypeInfo Json { get; set; } = null!;

    public void AddStep(UpcastStep step)
    {
        if (!Steps.TryAdd(step.From, step))
        {
            throw new DeedboxException(Errors.MissingUpcaster,
                $"Event {ClrType.Name} has two upcasters from version {step.From}. Keep one step per version.");
        }
    }
}

internal abstract record UpcastStep(int From);

/// <summary>Changes the stored JSON of version <see cref="UpcastStep.From"/> into the next version's JSON.</summary>
internal sealed record JsonUpcast(int From, Action<JsonObject> Apply) : UpcastStep(From);

/// <summary>Reads the last old version as its own record type and converts it to the current event type.</summary>
internal sealed record TypedUpcast(int From, Type OldType, Func<object, object> Convert) : UpcastStep(From)
{
    public JsonTypeInfo OldJson { get; set; } = null!;
}

/// <summary>Maps CLR types to stored names and back. Built once; every mistake fails at start-up.</summary>
internal sealed partial class EventRegistry
{
    private readonly Dictionary<Type, StreamRegistration> _byState = [];
    private readonly Dictionary<string, StreamRegistration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, EventRegistration> _byEvent = [];
    private readonly Dictionary<string, EventRegistration> _byStoredName = new(StringComparer.Ordinal);

    public EventRegistry(IEnumerable<StreamRegistration> streams, DeedboxJson json)
    {
        foreach (var stream in streams)
        {
            ValidateName(stream.Name, "Stream type");
            if (_byState.TryGetValue(stream.StateType, out var sameState))
            {
                throw new DeedboxException(Errors.DuplicateStream,
                    $"State type {stream.StateType.Name} is registered twice, as '{sameState.Name}' and '{stream.Name}'. Register each state type once.");
            }

            if (!_byName.TryAdd(stream.Name, stream))
            {
                throw new DeedboxException(Errors.DuplicateStream,
                    $"Stream type '{stream.Name}' is registered for both {_byName[stream.Name].StateType.Name} and {stream.StateType.Name}. Give one of them another name with Stream<T>(\"name\", ...).");
            }

            _byState[stream.StateType] = stream;
            stream.StateJson = json.TypeInfo(stream.StateType);

            foreach (var e in stream.Events)
                AddEvent(e, json);
        }
    }

    public IEnumerable<StreamRegistration> Streams => _byName.Values;

    public StreamRegistration ForState(Type stateType) =>
        _byState.TryGetValue(stateType, out var stream)
            ? stream
            : throw new DeedboxException(Errors.UnregisteredState,
                $"State type {stateType.Name} is not registered. Add .Stream<{stateType.Name}>(s => s.Events<...>()) in AddDeedbox.");

    public bool IsRegistered(Type eventType) => _byEvent.ContainsKey(eventType);

    /// <summary>Every stored name, current or alias, that reads as one of <paramref name="types"/>.</summary>
    public List<string> StoredNamesOf(IEnumerable<Type> types) =>
        types.Select(ForEvent).SelectMany(e => e.Aliases.Prepend(e.Name)).Distinct(StringComparer.Ordinal).ToList();

    public EventRegistration ForEvent(Type eventType) =>
        _byEvent.TryGetValue(eventType, out var e)
            ? e
            : throw new DeedboxException(Errors.UnregisteredEvent,
                $"Event {eventType.Name} is not registered. Add it to its stream with .Events<{eventType.Name}>() or .Event<{eventType.Name}>().");

    /// <summary>The registration for a stored name, which is either the current name or an alias.</summary>
    public EventRegistration? FindStoredName(string eventType) => _byStoredName.GetValueOrDefault(eventType);

    public EventRegistration ForStoredName(string eventType) =>
        FindStoredName(eventType) ?? throw new DeedboxException(Errors.UnmappedStoredEvent,
            $"Stored event type '{eventType}' has no registered CLR type. If you renamed the event class, add .Alias(\"{eventType}\") to it.");

    /// <summary>Turns a stored event into its current CLR type, running upcasters for older versions.</summary>
    public object Decode(string eventType, int eventVersion, string payload)
    {
        var registration = ForStoredName(eventType);
        if (eventVersion == registration.Version)
            return DeedboxJson.Deserialize(payload, registration.Json);

        if (eventVersion > registration.Version)
            throw VersionAhead(eventType, eventVersion, registration);

        JsonObject? json = null;
        for (var version = eventVersion; version < registration.Version; version++)
        {
            switch (registration.Steps[version])
            {
                case JsonUpcast step:
                    json ??= JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException($"Stored JSON for '{eventType}' is not an object.");
                    step.Apply(json);
                    break;
                case TypedUpcast step:
                    var old = json is null ? DeedboxJson.Deserialize(payload, step.OldJson) : json.Deserialize(step.OldJson)!;
                    return step.Convert(old);
            }
        }

        return json!.Deserialize(registration.Json) ?? throw new JsonException($"Upcast JSON for '{eventType}' is null.");
    }

    public static DeedboxException VersionAhead(string eventType, int storedVersion, EventRegistration registration) =>
        new(Errors.StoredVersionAhead,
            $"Stored events '{eventType}' have version {storedVersion}, but this build registers {registration.ClrType.Name} at version {registration.Version}. " +
            "A newer build wrote them; deploy that build or a later one.");

    private void AddEvent(EventRegistration e, DeedboxJson json)
    {
        ValidateName(e.Name, "Event type");
        if (_byEvent.TryGetValue(e.ClrType, out var sameType))
        {
            throw new DeedboxException(Errors.DuplicateEvent,
                $"Event {e.ClrType.Name} is registered twice, as '{sameType.Name}' and '{e.Name}'. One CLR type maps to one stored name; keep old names readable with an alias.");
        }

        foreach (var name in e.Aliases.Prepend(e.Name))
        {
            ValidateName(name, "Event type");
            if (!_byStoredName.TryAdd(name, e))
            {
                throw new DeedboxException(Errors.DuplicateEvent,
                    $"Event type name '{name}' is registered for both {_byStoredName[name].ClrType.Name} and {e.ClrType.Name}. Each current name and alias maps to one event.");
            }
        }

        for (var version = 1; version < e.Version; version++)
        {
            if (!e.Steps.TryGetValue(version, out var step))
            {
                throw new DeedboxException(Errors.MissingUpcaster,
                    $"Event {e.ClrType.Name} is at version {e.Version} but has no upcaster from version {version}. Add up.From({version}, json => ...) to its registration.");
            }

            if (step is TypedUpcast typed)
            {
                if (version != e.Version - 1)
                {
                    throw new DeedboxException(Errors.MissingUpcaster,
                        $"Event {e.ClrType.Name} has a typed upcaster from version {version}; a typed upcaster must be the last step, from version {e.Version - 1}.");
                }

                typed.OldJson = json.TypeInfo(typed.OldType);
            }
        }

        if (e.Steps.Keys.FirstOrDefault(v => v >= e.Version) is var extra and not 0)
        {
            throw new DeedboxException(Errors.MissingUpcaster,
                $"Event {e.ClrType.Name} has an upcaster from version {extra}, but the event is only at version {e.Version}.");
        }

        _byEvent[e.ClrType] = e;
        e.Json = json.TypeInfo(e.ClrType);
    }

    public static void ValidateName(string name, string what)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new DeedboxException(Errors.InvalidName,
                $"{what} name '{name}' is not valid. Use 1 to 200 letters, digits, '_', '.', ':' or '-'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,200}$")]
    private static partial Regex NamePattern();
}

internal static class Naming
{
    public static string StreamType(Type stateType) => JsonNamingPolicy.SnakeCaseLower.ConvertName(TypeName(stateType));

    public static string EventType(string streamType, Type eventType) =>
        streamType + "." + JsonNamingPolicy.SnakeCaseLower.ConvertName(TypeName(eventType));

    private static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
