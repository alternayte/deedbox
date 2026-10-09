namespace Deedbox;

/// <summary>
/// Every operation of the deedbox CLI, in code. Jobs run in the background runner of whichever instance takes them;
/// the returned ID tracks them with <see cref="GetJobAsync"/>.
/// </summary>
/// <remarks>
/// Apps call this interface; they do not implement it. A later release can add a member. In a test, use the instance
/// that AddDeedbox registers against a test database.
/// </remarks>
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
    /// <param name="tenantId">The tenant; pass an empty string when the app has no tenants.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>
    /// The erasure job, and how many keys the call deleted. Zero keys means the tenant holds no personal data under this
    /// subject ID: the ID or the tenant is wrong, or the subject was erased before.
    /// </returns>
    Task<ErasureResult> EraseSubjectAsync(string subjectId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Retires a projection or subscription that the app no longer registers. Its checkpoint stays, as retired: nothing
    /// applies it, and an instance that still registers the name starts with it idle and its health degraded.
    /// <see cref="RebuildAsync"/> brings a retired projection back. Retiring runs at once; it is not a job.
    /// </summary>
    /// <param name="projection">The stored projection or subscription name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX033 when no checkpoint has this name; DBX035 while a live instance registers it.</exception>
    Task RetireAsync(string projection, CancellationToken ct = default);

    /// <summary>Queues a job that rebuilds the stored state of every stream of a type from its events.</summary>
    /// <param name="streamType">The stored stream type name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="DeedboxException">DBX009 when no stream type has this name.</exception>
    Task<Guid> RebuildSnapshotsAsync(string streamType, CancellationToken ct = default);

    /// <summary>
    /// Re-wraps every tenant key and pseudonym secret with <paramref name="target"/>, such as when moving the master
    /// key out of the database or rotating it. No event is touched, and no pseudonymous subject ID changes. Afterwards,
    /// configure the target as the key mode.
    /// </summary>
    /// <param name="target">The master key to wrap with from now on.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>How many tenant keys and pseudonym secrets were re-wrapped.</returns>
    Task<int> RewrapKeysAsync(IMasterKeyProvider target, CancellationToken ct = default);

    /// <summary>
    /// Crypto-shreds a whole tenant at once: its keys are destroyed, so every personal field and every sealed state
    /// of the tenant reads as erased, on every instance. Its pseudonym secrets are deleted, so none of its subject IDs
    /// can be linked to an identity again. Events stay; data written afterwards uses new keys and new secrets.
    /// This cannot be undone.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancels the call.</param>
    Task ShredTenantAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Erases an identity in a tenant: computes its pseudonymous subject ID in every period whose secret still exists,
    /// deletes those subjects' keys at once, and queues one erasure job per period. The same as
    /// <see cref="IPseudonyms.EraseIdentityAsync"/> with an explicit tenant. The identity is never stored.
    /// </summary>
    /// <param name="identity">The identity, such as <c>github:alice</c>.</param>
    /// <param name="tenantId">The tenant; pass an empty string when the app has no tenants.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The erasure jobs, one per period, in period order, and how many keys the call deleted.</returns>
    Task<ErasureResult> EraseIdentityAsync(string identity, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Destroys the pseudonym secret of one period in a tenant, so its subject IDs can never be linked to an identity
    /// again, even by an admin. The period stays closed: computing a subject ID in it fails with DBX036. A done
    /// <c>pseudonyms_destroyed</c> job records the operation. Personal data stays readable, and erasure by subject ID
    /// still works. This cannot be undone.
    /// </summary>
    /// <param name="periodId">The period, such as <c>2026-Q1</c>.</param>
    /// <param name="tenantId">The tenant; pass an empty string when the app has no tenants.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>True when the period had a secret; false when it had none or was already destroyed.</returns>
    Task<bool> DestroyPseudonymPeriodAsync(string periodId, string tenantId, CancellationToken ct = default);
}

/// <summary>What an erasure call did before it returned.</summary>
public sealed record ErasureResult
{
    /// <summary>The queued erasure jobs, one per subject.</summary>
    public required IReadOnlyList<Guid> JobIds { get; init; }

    /// <summary>How many subject keys the call deleted. Zero means it found no personal data to erase.</summary>
    public required int KeysDeleted { get; init; }
}

/// <summary>The state of the store's background work.</summary>
public sealed record StoreStatus
{
    /// <summary>The highest committed global position.</summary>
    public required long Head { get; init; }

    /// <summary>Every projection and subscription checkpoint.</summary>
    public required IReadOnlyList<ConsumerStatus> Consumers { get; init; }

    /// <summary>The most recent jobs, newest first.</summary>
    public required IReadOnlyList<JobInfo> Jobs { get; init; }
}

/// <summary>How a projection or subscription runs.</summary>
public enum ConsumerMode
{
    /// <summary>A mode that this version of Deedbox does not know; a newer version wrote it.</summary>
    Unknown,

    /// <summary>A projection applied in the transaction of each append.</summary>
    Inline,

    /// <summary>A projection applied by the background runner.</summary>
    Async,

    /// <summary>A subscription.</summary>
    Subscription,
}

/// <summary>What a projection or subscription is doing.</summary>
public enum ConsumerState
{
    /// <summary>A state that this version of Deedbox does not know; a newer version wrote it.</summary>
    Unknown,

    /// <summary>It applies events as they come.</summary>
    Running,

    /// <summary>It replays events to catch up, after a rebuild or as a new inline projection.</summary>
    Rebuilding,

    /// <summary>It stopped on an event or a change that needs attention; <see cref="ConsumerStatus.Error"/> says why.</summary>
    Stalled,

    /// <summary>It was retired; nothing applies it until a rebuild.</summary>
    Retired,
}

/// <summary>One projection or subscription.</summary>
public sealed record ConsumerStatus
{
    /// <summary>The stored name.</summary>
    public required string Name { get; init; }

    /// <summary>How it runs.</summary>
    public required ConsumerMode Mode { get; init; }

    /// <summary>What it is doing.</summary>
    public required ConsumerState Status { get; init; }

    /// <summary>The last global position it applied or scanned.</summary>
    public required long Position { get; init; }

    /// <summary>How many positions it is behind the head; 0 for an inline projection that is running, and for a retired one.</summary>
    public required long Lag { get; init; }

    /// <summary>When its checkpoint last moved.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// A note about the consumer as JSON, or null. Its <c>reason</c> is <c>poison</c> for a consumer stalled on an event,
    /// with the event, the stream, the exception type, the stack frames, the attempts so far and <c>retryAt</c>;
    /// <c>mode_changed</c> for a projection whose run mode changed; and <c>slow_catch_up</c> for an inline projection
    /// whose catch-up cannot keep up with appends. An exception's message is never stored.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>The kind of a job.</summary>
public enum JobKind
{
    /// <summary>A kind that this version of Deedbox does not know; a newer version wrote it.</summary>
    Unknown,

    /// <summary>A rebuild of a projection.</summary>
    Rebuild,

    /// <summary>An audited skip of the event a consumer stalled on.</summary>
    Skip,

    /// <summary>The part of an erasure that appends SubjectErased and rebuilds the affected state.</summary>
    Erase,

    /// <summary>A rebuild of the stored state of every stream of a type.</summary>
    Snapshots,

    /// <summary>The record of a destroyed pseudonym period; it is written as done and never runs.</summary>
    PseudonymsDestroyed,
}

/// <summary>Where a job is.</summary>
public enum JobState
{
    /// <summary>A state that this version of Deedbox does not know; a newer version wrote it.</summary>
    Unknown,

    /// <summary>It waits for a runner.</summary>
    Queued,

    /// <summary>It finished.</summary>
    Done,

    /// <summary>It failed; <see cref="JobInfo.Progress"/> says why.</summary>
    Failed,
}

/// <summary>A queued, finished or failed job.</summary>
public sealed record JobInfo
{
    /// <summary>The job ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>What the job does.</summary>
    public required JobKind Kind { get; init; }

    /// <summary>The job's arguments as JSON.</summary>
    public required string Args { get; init; }

    /// <summary>Where the job is.</summary>
    public required JobState Status { get; init; }

    /// <summary>What the job did or why it failed, as JSON.</summary>
    public string? Progress { get; init; }

    /// <summary>When it was queued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a runner started it, or null.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it finished, or null.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>The stored text of each status value, which the CLI prints and the database holds.</summary>
internal static class Wire
{
    public static ConsumerMode Mode(string text) => text switch
    {
        CheckpointMode.Inline => ConsumerMode.Inline,
        CheckpointMode.Async => ConsumerMode.Async,
        CheckpointMode.Subscription => ConsumerMode.Subscription,
        _ => ConsumerMode.Unknown,
    };

    public static ConsumerState State(string text) => text switch
    {
        CheckpointStatus.Running => ConsumerState.Running,
        CheckpointStatus.Rebuilding => ConsumerState.Rebuilding,
        CheckpointStatus.Stalled => ConsumerState.Stalled,
        CheckpointStatus.Retired => ConsumerState.Retired,
        _ => ConsumerState.Unknown,
    };

    public static JobKind Kind(string text) => text switch
    {
        Jobs.Rebuild => JobKind.Rebuild,
        Jobs.Skip => JobKind.Skip,
        Jobs.Erase => JobKind.Erase,
        Jobs.Snapshots => JobKind.Snapshots,
        Jobs.PseudonymsDestroyed => JobKind.PseudonymsDestroyed,
        _ => JobKind.Unknown,
    };

    public static JobState JobState(string text) => text switch
    {
        JobStatus.Queued => Deedbox.JobState.Queued,
        JobStatus.Done => Deedbox.JobState.Done,
        JobStatus.Failed => Deedbox.JobState.Failed,
        _ => Deedbox.JobState.Unknown,
    };

    public static string Text(ConsumerMode mode) => mode switch
    {
        ConsumerMode.Inline => CheckpointMode.Inline,
        ConsumerMode.Async => CheckpointMode.Async,
        ConsumerMode.Subscription => CheckpointMode.Subscription,
        _ => "unknown",
    };

    public static string Text(ConsumerState state) => state switch
    {
        ConsumerState.Running => CheckpointStatus.Running,
        ConsumerState.Rebuilding => CheckpointStatus.Rebuilding,
        ConsumerState.Stalled => CheckpointStatus.Stalled,
        ConsumerState.Retired => CheckpointStatus.Retired,
        _ => "unknown",
    };

    public static string Text(JobKind kind) => kind switch
    {
        JobKind.Rebuild => Jobs.Rebuild,
        JobKind.Skip => Jobs.Skip,
        JobKind.Erase => Jobs.Erase,
        JobKind.Snapshots => Jobs.Snapshots,
        JobKind.PseudonymsDestroyed => Jobs.PseudonymsDestroyed,
        _ => "unknown",
    };

    public static string Text(JobState state) => state switch
    {
        Deedbox.JobState.Queued => JobStatus.Queued,
        Deedbox.JobState.Done => JobStatus.Done,
        Deedbox.JobState.Failed => JobStatus.Failed,
        _ => "unknown",
    };
}
