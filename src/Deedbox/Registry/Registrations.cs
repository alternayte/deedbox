using System.Text.Json;
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
    public string Name { get; } = name;
    public int Version { get; } = 1;
    public StreamRegistration Stream { get; } = stream;
    public JsonTypeInfo Json { get; set; } = null!;
}

/// <summary>Maps CLR types to stored names and back. Built once; duplicates fail at start-up.</summary>
internal sealed partial class EventRegistry
{
    private readonly Dictionary<Type, StreamRegistration> _byState = [];
    private readonly Dictionary<string, StreamRegistration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, EventRegistration> _byEvent = [];
    private readonly Dictionary<string, EventRegistration> _byEventName = new(StringComparer.Ordinal);

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
            {
                ValidateName(e.Name, "Event type");
                if (_byEvent.TryGetValue(e.ClrType, out var sameType))
                {
                    throw new DeedboxException(Errors.DuplicateEvent,
                        $"Event {e.ClrType.Name} is registered twice, as '{sameType.Name}' and '{e.Name}'. One CLR type maps to one stored name; keep old names readable with an alias.");
                }

                if (!_byEventName.TryAdd(e.Name, e))
                {
                    throw new DeedboxException(Errors.DuplicateEvent,
                        $"Event type name '{e.Name}' is registered for both {_byEventName[e.Name].ClrType.Name} and {e.ClrType.Name}. Give one of them another name with Event<T>(name: ...).");
                }

                _byEvent[e.ClrType] = e;
                e.Json = json.TypeInfo(e.ClrType);
            }
        }
    }

    public StreamRegistration ForState(Type stateType) =>
        _byState.TryGetValue(stateType, out var stream)
            ? stream
            : throw new DeedboxException(Errors.UnregisteredState,
                $"State type {stateType.Name} is not registered. Add .Stream<{stateType.Name}>(s => s.Events<...>()) in AddDeedbox.");

    public EventRegistration ForEvent(Type eventType) =>
        _byEvent.TryGetValue(eventType, out var e)
            ? e
            : throw new DeedboxException(Errors.UnregisteredEvent,
                $"Event {eventType.Name} is not registered. Add it to its stream with .Events<{eventType.Name}>() or .Event<{eventType.Name}>().");

    public EventRegistration ForStoredName(string eventType) =>
        _byEventName.TryGetValue(eventType, out var e)
            ? e
            : throw new DeedboxException(Errors.UnknownStoredEvent,
                $"Stored event type '{eventType}' has no registered CLR type. If you renamed the event class, register the old name as an alias.");

    private static void ValidateName(string name, string what)
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
