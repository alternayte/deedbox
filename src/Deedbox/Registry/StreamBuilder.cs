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
    /// Registers one event type. The stored name defaults to <c>{streamType}.{snake_case(TEvent)}</c>,
    /// such as <c>cart.item_added</c>.
    /// </summary>
    /// <param name="name">A stored name that overrides the convention.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public StreamBuilder<TState> Event<TEvent>(string? name = null) where TEvent : notnull
    {
        _stream.Events.Add(new EventRegistration(typeof(TEvent), name ?? Naming.EventType(_stream.Name, typeof(TEvent)), _stream));
        return this;
    }

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1>()
        where T1 : notnull => Event<T1>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1, T2>()
        where T1 : notnull where T2 : notnull => Event<T1>().Event<T2>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1, T2, T3>()
        where T1 : notnull where T2 : notnull where T3 : notnull => Event<T1>().Event<T2>().Event<T3>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1, T2, T3, T4>()
        where T1 : notnull where T2 : notnull where T3 : notnull where T4 : notnull =>
        Event<T1>().Event<T2>().Event<T3>().Event<T4>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    /// <typeparam name="T5">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1, T2, T3, T4, T5>()
        where T1 : notnull where T2 : notnull where T3 : notnull where T4 : notnull where T5 : notnull =>
        Event<T1>().Event<T2>().Event<T3>().Event<T4>().Event<T5>();

    /// <summary>Registers event types with conventional names.</summary>
    /// <typeparam name="T1">An event type.</typeparam>
    /// <typeparam name="T2">An event type.</typeparam>
    /// <typeparam name="T3">An event type.</typeparam>
    /// <typeparam name="T4">An event type.</typeparam>
    /// <typeparam name="T5">An event type.</typeparam>
    /// <typeparam name="T6">An event type.</typeparam>
    public StreamBuilder<TState> Events<T1, T2, T3, T4, T5, T6>()
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
