namespace Deedbox;

/// <summary>When Deedbox stores a stream's state next to its events, so a load reads fewer events.</summary>
public sealed class SnapshotPolicy
{
    private SnapshotPolicy(int every)
    {
        Interval = every;
    }

    /// <summary>Stores the state on every append, so a load reads one row. The default.</summary>
    public static SnapshotPolicy EveryAppend { get; } = new(1);

    /// <summary>Never stores state; every load replays the stream.</summary>
    public static SnapshotPolicy Never { get; } = new(0);

    internal int Interval { get; }

    internal bool Enabled => Interval > 0;

    /// <summary>Stores the state each time the stream passes a multiple of <paramref name="events"/> events.</summary>
    /// <param name="events">The interval, at least 1.</param>
    public static SnapshotPolicy Every(int events)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(events, 1);
        return events == 1 ? EveryAppend : new(events);
    }

    internal bool IsDue(long from, long to) => Enabled && from / Interval != to / Interval;
}
