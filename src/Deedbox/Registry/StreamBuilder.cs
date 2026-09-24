using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Deedbox;

/// <summary>Registers one stream type's events and settings inside <c>Stream&lt;TState&gt;(...)</c>.</summary>
/// <typeparam name="TState">The stream's state type.</typeparam>
public sealed class StreamBuilder<TState> where TState : IState<TState>
{
    private readonly StreamRegistration _stream;

    internal StreamBuilder(StreamRegistration stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Registers one event type under <c>{streamType}.{snake_case(TEvent)}</c>, such as <c>cart.item_added</c>.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public StreamBuilder<TState> Event<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>() where TEvent : notnull => Event<TEvent>(_ => { });

    /// <summary>Registers one event type under an explicit stored name.</summary>
    /// <param name="name">The stored event type name, such as <c>cart.line_added</c>.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public StreamBuilder<TState> Event<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>(string name) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(name);
        return Event<TEvent>(e => e.Name(name));
    }

    /// <summary>Registers one event type and sets its name, aliases or upcasters.</summary>
    /// <param name="configure">Sets the event's name, aliases and upcasters.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public StreamBuilder<TState> Event<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>(Action<EventBuilder<TEvent>> configure) where TEvent : notnull =>
        Event(1, configure);

    /// <summary>
    /// Registers one event type whose shape is at <paramref name="version"/>. Older stored versions need an
    /// upcaster for every step, such as <c>up =&gt; up.From(1, json =&gt; json["qty"] ??= 1)</c>.
    /// </summary>
    /// <param name="version">The current shape version, at least 1.</param>
    /// <param name="configure">Sets the event's upcasters, name and aliases.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public StreamBuilder<TState> Event<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>(int version, Action<EventBuilder<TEvent>> configure) where TEvent : notnull
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(configure);
        var registration = new EventRegistration(typeof(TEvent), Naming.EventType(_stream.Name, typeof(TEvent)), _stream, PersonalFields.Properties(typeof(TEvent))) { Version = version };
        configure(new EventBuilder<TEvent>(registration));
        _stream.Events.Add(registration);
        return this;
    }

    /// <summary>
    /// Registers every public class or struct nested in <paramref name="container"/>, such as a static
    /// <c>CartEvents</c> class that holds the stream's event records, with conventional names.
    /// </summary>
    /// <param name="container">The type the events are nested in, such as <c>typeof(CartEvents)</c>.</param>
    [RequiresUnreferencedCode("Reads the nested types' properties with reflection. In trimmed apps, register each event with Event<T>().")]
    public StreamBuilder<TState> EventsNestedIn([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicNestedTypes)] Type container)
    {
        ArgumentNullException.ThrowIfNull(container);
        foreach (var type in container.GetNestedTypes(BindingFlags.Public).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || type.IsEnum || type.ContainsGenericParameters)
                continue;
            _stream.Events.Add(new EventRegistration(type, Naming.EventType(_stream.Name, type), _stream, NestedProperties(type)));
        }

        return this;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "EventsNestedIn itself requires unreferenced code.")]
    private static List<PropertyInfo> NestedProperties(Type type) => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)];

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1>()
        where T1 : notnull => Event<T1>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T2>()
        where T1 : notnull where T2 : notnull => Event<T1>().Event<T2>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T2, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T3>()
        where T1 : notnull where T2 : notnull where T3 : notnull => Event<T1>().Event<T2>().Event<T3>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T2, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T3, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T4>()
        where T1 : notnull where T2 : notnull where T3 : notnull where T4 : notnull =>
        Event<T1>().Event<T2>().Event<T3>().Event<T4>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    /// <typeparam name="T5">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T2, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T3, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T4, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T5>()
        where T1 : notnull where T2 : notnull where T3 : notnull where T4 : notnull where T5 : notnull =>
        Event<T1>().Event<T2>().Event<T3>().Event<T4>().Event<T5>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    /// <typeparam name="T5">An event type.</typeparam>
    /// <typeparam name="T6">An event type.</typeparam>
    public StreamBuilder<TState> Events<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T2, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T3, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T4, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T5, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T6>()
        where T1 : notnull where T2 : notnull where T3 : notnull where T4 : notnull where T5 : notnull where T6 : notnull =>
        Event<T1>().Event<T2>().Event<T3>().Event<T4>().Event<T5>().Event<T6>();

    /// <summary>
    /// The version of <typeparamref name="TState"/>'s shape, 1 by default. Raise it when you change the state type;
    /// each stream's stored state is then rebuilt from its events the next time it loads.
    /// </summary>
    /// <param name="version">The state version, at least 1.</param>
    public StreamBuilder<TState> StateVersion(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _stream.StateVersion = version;
        return this;
    }

    /// <summary>When to store the state; <see cref="SnapshotPolicy.EveryAppend"/> by default.</summary>
    /// <param name="policy">The snapshot policy.</param>
    public StreamBuilder<TState> Snapshots(SnapshotPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _stream.Snapshots = policy;
        return this;
    }
}
