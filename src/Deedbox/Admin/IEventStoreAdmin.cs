namespace Deedbox;

/// <summary>
/// Every operation of the deedbox CLI, in code. Jobs run in the background runner of whichever instance takes them;
/// the returned ID tracks them with <see cref="GetJobAsync"/>.
/// </summary>
public interface IEventStoreAdmin
{
    /// <summary>Every projection and subscription checkpoint, the head position, and recent jobs.</summary>
    /// <param name="ct">Cancels the call.</param>
    Task<StoreStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>One job, or null when no job has this ID.</summary>
    /// <param name="jobId">The job ID.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<JobInfo?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Queues an in-place rebuild: the projection's ResetAsync runs, then every event replays through it.</summary>
    /// <param name="projection">The stored projection name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX033 when no projection has this name.</exception>
    Task<Guid> RebuildAsync(string projection, CancellationToken ct = default);

    /// <summary>
    /// Queues an audited skip of the one event a stalled projection or subscription is stuck on. The job row records
    /// the event and the stall.
    /// </summary>
    /// <param name="consumer">The stalled projection or subscription.</param>
    /// <param name="eventId">The event it stalled on, from <see cref="GetStatusAsync"/>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX033 when no projection or subscription has this name.</exception>
    Task<Guid> SkipAsync(string consumer, Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// Erases a data subject in a tenant: deletes their key at once, then queues the job that appends SubjectErased
    /// and rebuilds the affected state. The same as <see cref="ISubjectErasure"/> with an explicit tenant.
    /// </summary>
    /// <param name="subjectId">The subject.</param>
    /// <param name="tenantId">The tenant; empty when the app has no tenants.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<Guid> EraseSubjectAsync(string subjectId, string tenantId = "", CancellationToken ct = default);

    /// <summary>
    /// Retires a projection or subscription that the app no longer registers. Its checkpoint stays, as retired: nothing
    /// applies it, and an instance that still registers the name starts with it idle and its health degraded.
    /// <see cref="RebuildAsync"/> brings a retired projection back. Retiring runs at once; it is not a job.
    /// </summary>
    /// <param name="projection">The stored projection or subscription name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX033 when no checkpoint has this name; DBX035 while a live instance registers it.</exception>
    /// <remarks>The default body exists so that test doubles written against 0.2 still compile; Deedbox's own admin overrides it.</remarks>
    Task RetireAsync(string projection, CancellationToken ct = default) =>
        throw new NotSupportedException("This IEventStoreAdmin does not support RetireAsync. Use the one that AddDeedbox registers.");

    /// <summary>Queues a job that rebuilds the stored state of every stream of a type from its events.</summary>
    /// <param name="streamType">The stored stream type name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX009 when no stream type has this name.</exception>
    Task<Guid> RebuildSnapshotsAsync(string streamType, CancellationToken ct = default);

    /// <summary>
    /// Re-wraps every tenant key with <paramref name="target"/>, such as when moving the master key out of the
    /// database or rotating it. No event is touched. Afterwards, configure the target as the key mode.
    /// </summary>
    /// <param name="target">The master key to wrap with from now on.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>How many tenant keys were re-wrapped.</returns>
    Task<int> RewrapKeysAsync(IMasterKeyProvider target, CancellationToken ct = default);

    /// <summary>
    /// Crypto-shreds a whole tenant at once: its keys are destroyed, so every personal field and every sealed state
    /// of the tenant reads as erased, on every instance. Events stay; data written afterwards uses a new key.
    /// This cannot be undone.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancels the call.</param>
    Task ShredTenantAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>The state of the store's background work.</summary>
/// <param name="Head">The highest committed global position.</param>
/// <param name="Consumers">Every projection and subscription checkpoint.</param>
/// <param name="Jobs">The most recent jobs, newest first.</param>
public sealed record StoreStatus(long Head, IReadOnlyList<ConsumerStatus> Consumers, IReadOnlyList<JobInfo> Jobs);

/// <summary>One projection or subscription.</summary>
/// <param name="Name">The stored name.</param>
/// <param name="Mode">inline, async or subscription.</param>
/// <param name="Status">running, rebuilding or stalled.</param>
/// <param name="Position">The last global position it applied or scanned.</param>
/// <param name="Lag">How many positions it is behind the head; 0 for an inline projection that is running.</param>
/// <param name="UpdatedAt">When its checkpoint last moved.</param>
/// <param name="Error">
/// For a stalled consumer, the stall as JSON: reason, event, stream, version and exception, and for a poison event the
/// attempts so far and <c>retryAt</c>, when the runner next retries it.
/// </param>
public sealed record ConsumerStatus(string Name, string Mode, string Status, long Position, long Lag, DateTimeOffset UpdatedAt, string? Error);

/// <summary>A queued, finished or failed job.</summary>
/// <param name="Id">The job ID.</param>
/// <param name="Kind">rebuild, skip, erase or snapshots.</param>
/// <param name="Args">The job's arguments as JSON.</param>
/// <param name="Status">queued, done or failed.</param>
/// <param name="Progress">What the job did or why it failed, as JSON.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="FinishedAt">When it finished, or null.</param>
public sealed record JobInfo(Guid Id, string Kind, string Args, string Status, string? Progress, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);
