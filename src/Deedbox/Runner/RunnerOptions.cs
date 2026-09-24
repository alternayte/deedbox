namespace Deedbox;

/// <summary>Settings for the background runner that executes async projections, subscriptions and jobs.</summary>
public sealed class RunnerOptions
{
    /// <summary>Runs the background runner in this process. Turn it off in processes that only append; default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most events one batch reads; default 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The first wait after a poll finds nothing; it doubles up to <see cref="MaxPollDelay"/>. Default 50 ms.</summary>
    public TimeSpan MinPollDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The longest wait between polls when idle; default 5 seconds. On Postgres, LISTEN/NOTIFY wakes the runner sooner.
    /// </summary>
    public TimeSpan MaxPollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times a failing event is retried before its consumer stalls; default 5.</summary>
    public int HandlerRetries { get; set; } = 5;

    /// <summary>The wait before the first retry of a failing event; it doubles on each retry. Default 1 second.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The health check reports a consumer unhealthy when events are waiting and its checkpoint has not moved for
    /// this long; default 10 minutes. A consumer that is behind but moving stays healthy.
    /// </summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MinPollDelay, TimeSpan.Zero, nameof(MinPollDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPollDelay, MinPollDelay, nameof(MaxPollDelay));
        ArgumentOutOfRangeException.ThrowIfNegative(HandlerRetries, nameof(HandlerRetries));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero, nameof(RetryDelay));
    }
}
