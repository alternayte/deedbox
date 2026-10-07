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

    /// <summary>
    /// How many times a failing event is retried before its consumer stalls; default 5. A stalled consumer still retries
    /// the event every 5 minutes, and runs again once it succeeds.
    /// </summary>
    public int HandlerRetries { get; set; } = 5;

    /// <summary>The wait before the first retry of a failing event; it doubles on each retry, up to 5 minutes. Default 1 second.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest one handler call may run; default 5 minutes, at most 24 days. For a batch projection it is one call
    /// for the whole batch. A call that passes it is cancelled through the handler's cancellation token and counts as a
    /// failed attempt, so the event is retried and, after <see cref="HandlerRetries"/>, the consumer stalls. A
    /// subscription handler that ignores the token is abandoned after 10 more seconds. A projection handler writes in
    /// the batch's transaction, so the runner waits for it: pass the token to everything a projection handler awaits.
    /// </summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The health check reports a consumer unhealthy when events are waiting and its checkpoint has not moved for
    /// this long; default 10 minutes. A consumer that is behind but moving stays healthy.
    /// </summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest wait between retries of a failing event, and the interval at which a consumer stalled on a poison
    /// event retries it. Internal: tests shorten it.
    /// </summary>
    internal TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest time a cut-over holds the position counter while it applies the last events of a catch-up. Every
    /// append in the store waits for that long. Internal: tests shorten it.
    /// </summary>
    internal TimeSpan CutOverHold { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a subscription's handler gets to stop after <see cref="HandlerTimeout"/> cancelled its token, before
    /// the runner abandons it. Internal: tests shorten it.
    /// </summary>
    internal TimeSpan HandlerGrace { get; set; } = TimeSpan.FromSeconds(10);

    internal HandlerLimits HandlerLimits => new(HandlerTimeout, HandlerGrace);

    /// <summary>How often each instance writes its heartbeat. Internal: tests shorten it.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MinPollDelay, TimeSpan.Zero, nameof(MinPollDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPollDelay, MinPollDelay, nameof(MaxPollDelay));
        ArgumentOutOfRangeException.ThrowIfNegative(HandlerRetries, nameof(HandlerRetries));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero, nameof(RetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandlerTimeout, TimeSpan.Zero, nameof(HandlerTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HandlerTimeout, TimeSpan.FromDays(24), nameof(HandlerTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StallAfter, TimeSpan.Zero, nameof(StallAfter));
    }
}
