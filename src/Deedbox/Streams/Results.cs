namespace Deedbox;

/// <summary>A stream's current state and version. A stream with no events has its initial state and version 0.</summary>
/// <param name="State">The state after every event.</param>
/// <param name="Version">The version of the last event; pass it to <see cref="ExpectedVersion.Exact"/>.</param>
/// <typeparam name="TState">The stream's state type.</typeparam>
public readonly record struct LoadResult<TState>(TState State, long Version);

/// <summary>The outcome of an append.</summary>
/// <param name="Version">The stream's version after the append.</param>
/// <param name="Events">The appended events, in order.</param>
public sealed record AppendResult(long Version, IReadOnlyList<EventEnvelope> Events);

/// <summary>The outcome of <see cref="IEventStore.Execute"/>.</summary>
/// <param name="State">The state after the new events.</param>
/// <param name="Version">The stream's version after the new events.</param>
/// <param name="Events">The appended events; empty when the decision produced none.</param>
/// <typeparam name="TState">The stream's state type.</typeparam>
public sealed record ExecuteResult<TState>(TState State, long Version, IReadOnlyList<EventEnvelope> Events);

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
