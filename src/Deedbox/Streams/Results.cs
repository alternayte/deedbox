namespace Deedbox;

/// <summary>A stream's current state and version. A stream with no events has its initial state and version 0.</summary>
/// <typeparam name="TState">The stream's state type.</typeparam>
public readonly record struct LoadResult<TState>
{
    /// <summary>The state after every event.</summary>
    public required TState State { get; init; }

    /// <summary>The version of the last event; pass it to <see cref="ExpectedVersion.Exact"/>.</summary>
    public required long Version { get; init; }

    /// <summary>Reads the result as <c>var (state, version) = await store.Load&lt;T&gt;(id)</c>.</summary>
    /// <param name="state">The state after every event.</param>
    /// <param name="version">The version of the last event.</param>
    public void Deconstruct(out TState state, out long version) => (state, version) = (State, Version);
}

/// <summary>The outcome of an append.</summary>
public sealed record AppendResult
{
    /// <summary>The stream's version after the append.</summary>
    public required long Version { get; init; }

    /// <summary>The appended events, in order.</summary>
    public required IReadOnlyList<EventEnvelope> Events { get; init; }
}

/// <summary>The outcome of <see cref="IEventStore.Execute"/>.</summary>
/// <typeparam name="TState">The stream's state type.</typeparam>
public sealed record ExecuteResult<TState>
{
    /// <summary>The state after the new events.</summary>
    public required TState State { get; init; }

    /// <summary>The stream's version after the new events.</summary>
    public required long Version { get; init; }

    /// <summary>The appended events; empty when the decision produced none.</summary>
    public required IReadOnlyList<EventEnvelope> Events { get; init; }
}

/// <summary>An append found the stream at a different version than it expected.</summary>
public sealed class ConcurrencyException : DeedboxException
{
    internal ConcurrencyException(string streamId, ExpectedVersion expected, long actual)
        : base(Errors.Conflict, $"Stream '{streamId}' is at version {actual}, but the append expected {expected}. Load the stream and decide again.")
    {
        StreamId = streamId;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The stream.</summary>
    public string StreamId { get; }

    /// <summary>What the append expected.</summary>
    public ExpectedVersion Expected { get; }

    /// <summary>The stream's version when the append ran; 0 when it did not exist.</summary>
    public long Actual { get; }
}
