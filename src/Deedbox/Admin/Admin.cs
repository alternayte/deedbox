using System.Text.Json.Nodes;

namespace Deedbox;

/// <summary>
/// Admin operations that need only the database, shared by <see cref="IEventStoreAdmin"/> and the CLI. Operations
/// that need the app's registrations (rebuilds, erasure, snapshots) are queued as jobs for the app's runner.
/// </summary>
internal static class Admin
{
    public static async Task<StoreStatus> Status(DeedboxProvider provider, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        var head = await provider.ReadHead(connection, null, ct);
        var consumers = (await provider.ReadCheckpoints(connection, ct))
            .Select(r => new ConsumerStatus
            {
                Name = r.Name,
                Mode = Wire.Mode(r.Mode),
                Status = Wire.State(r.Status),
                Position = r.Position,
                Lag = (r.Mode == CheckpointMode.Inline && r.Status == CheckpointStatus.Running) || r.Status == CheckpointStatus.Retired ? 0 : Math.Max(0, head - r.Position),
                UpdatedAt = r.UpdatedAt,
                Error = r.Error,
            })
            .ToList();
        var jobs = (await provider.ReadJobs(connection, 20, ct)).Select(Info).ToList();
        return new StoreStatus { Head = head, Consumers = consumers, Jobs = jobs };
    }

    public static async Task<JobInfo?> Job(DeedboxProvider provider, Guid id, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        return await provider.ReadJob(connection, id, ct) is { } job ? Info(job) : null;
    }

    /// <summary>
    /// The immediate part of an erasure, for one or more subjects of a tenant. One transaction clears the stored state of
    /// the streams that hold their data, deletes their keys and queues one erasure job per subject, so a crash never
    /// leaves a key deleted with no job. An append that held a key's share lock commits before the key goes; it may have
    /// stored state for a stream the subject was new to, so that state is cleared again after the commit. When this
    /// returns, nothing reads the subjects' data.
    /// </summary>
    public static async Task<ErasureResult> Erase(DeedboxProvider provider, TimeProvider clock, string tenantId, IReadOnlyList<string> subjects, CancellationToken ct)
    {
        DeedboxContext.ValidTenant(tenantId);
        // No rule on the ID's shape here: a store written before 0.5.0 can hold IDs that an append now refuses, and
        // those subjects must stay erasable.
        foreach (var subject in subjects)
            ArgumentException.ThrowIfNullOrEmpty(subject, nameof(subjects));

        var jobs = new List<Guid>();
        var deleted = 0;
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            foreach (var subject in subjects)
            {
                await provider.ClearSubjectState(connection, transaction, tenantId, subject, ct);
                deleted += await provider.DeleteSubjectKey(connection, transaction, tenantId, subject, ct);
                var job = new JobRow(Uuid7.New(), Jobs.Erase, EraseArgs(tenantId, subject).ToJsonString(), JobStatus.Queued, null, clock.GetUtcNow(), null, null);
                await provider.InsertJob(connection, transaction, job, ct);
                jobs.Add(job.Id);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        // The keys are gone, so this part must finish even when the caller gave up.
        foreach (var subject in subjects)
            await provider.ClearSubjectState(connection, null, tenantId, subject, CancellationToken.None);
        return new ErasureResult { JobIds = jobs, KeysDeleted = deleted };
    }

    /// <summary>
    /// Erases an identity in a tenant: its subject in every period with a secret. The subject keys go in one
    /// transaction, so every period's data reads as erased at once; then one erasure job per period finishes the rest.
    /// </summary>
    public static async Task<ErasureResult> EraseIdentity(DeedboxProvider provider, Pseudonymizer pseudonyms, TimeProvider clock, string tenantId, string identity, CancellationToken ct)
    {
        Pseudonymizer.ValidateIdentity(identity);
        DeedboxContext.ValidTenant(tenantId);
        return await Erase(provider, clock, tenantId, await pseudonyms.SubjectsInEveryPeriod(tenantId, identity, ct), ct);
    }

    /// <summary>
    /// Destroys a period's pseudonym secret: the row becomes a tombstone, and a done job row records the operation, in
    /// one transaction. Returns true when the period had a secret.
    /// </summary>
    public static async Task<bool> DestroyPseudonymPeriod(DeedboxProvider provider, TimeProvider clock, string tenantId, string periodId, CancellationToken ct)
    {
        DeedboxContext.ValidTenant(tenantId);
        PseudonymPeriod.Validate(periodId);
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var destroyed = await provider.DestroyPseudonymKey(connection, transaction, tenantId, periodId, ct);
        var now = clock.GetUtcNow();
        await provider.InsertJob(connection, transaction, new JobRow(Uuid7.New(), Jobs.PseudonymsDestroyed,
            new JsonObject { ["tenantId"] = tenantId, ["periodId"] = periodId }.ToJsonString(), JobStatus.Done,
            new JsonObject { ["destroyed"] = destroyed }.ToJsonString(), now, now, now), ct);
        await transaction.CommitAsync(ct);
        return destroyed;
    }

    /// <summary>Shreds a tenant with the database alone. App instances still hold its key in memory until they reload.</summary>
    public static async Task ShredTenant(DeedboxProvider provider, string tenantId, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await provider.ShredTenant(connection, transaction, tenantId, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Retires a projection or subscription: its checkpoint becomes retired, so nothing applies it until a rebuild. Refuses
    /// while a live instance registers the name, because that instance would go on applying it.
    /// </summary>
    public static async Task Retire(DeedboxProvider provider, string name, TimeSpan liveFor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        var stored = (await provider.ReadCheckpoints(connection, ct)).FirstOrDefault(r => r.Name == name)
            ?? throw new DeedboxException(Errors.UnknownConsumer, $"No projection or subscription named '{name}' has a checkpoint in this store.");
        if (stored.Status == CheckpointStatus.Retired)
            return;

        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (stored.Mode == CheckpointMode.Inline)
            await provider.LockInlineGate(connection, transaction, name, ct);
        var row = (await provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct))!;

        var users = (await provider.ReadLiveInstances(connection, transaction, liveFor, ct)).Where(i => i.Consumers.Contains(name, StringComparer.Ordinal)).ToList();
        if (users.Count > 0)
        {
            throw new DeedboxException(Errors.ProjectionInUse,
                $"'{name}' cannot be retired while live instances register it: {Instances.Describe(users)}. Deploy a version without it first.");
        }

        await provider.UpdateCheckpoint(connection, transaction, row with { Status = CheckpointStatus.Retired, Error = null }, ct);
        await transaction.CommitAsync(ct);
    }

    public static JsonObject RebuildArgs(string projection) => new() { ["projection"] = Required(projection, nameof(projection)) };

    public static JsonObject SkipArgs(string consumer, Guid eventId) => new() { ["projection"] = Required(consumer, nameof(consumer)), ["eventId"] = eventId.ToString("D") };

    public static JsonObject EraseArgs(string tenantId, string subjectId) =>
        new() { ["tenantId"] = DeedboxContext.ValidTenant(tenantId), ["subjectId"] = Required(subjectId, nameof(subjectId)) };

    public static JsonObject SnapshotArgs(string streamType) => new() { ["streamType"] = Required(streamType, nameof(streamType)) };

    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static JobInfo Info(JobRow job) => new()
    {
        Id = job.Id,
        Kind = Wire.Kind(job.Kind),
        Args = job.Args,
        Status = Wire.JobState(job.Status),
        Progress = job.Progress,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
    };
}

internal sealed class EventStoreAdmin(DeedboxRuntime runtime) : IEventStoreAdmin
{
    public Task<StoreStatus> GetStatusAsync(CancellationToken ct = default) => Admin.Status(runtime.Provider, ct);

    public Task<JobInfo?> GetJobAsync(Guid jobId, CancellationToken ct = default) => Admin.Job(runtime.Provider, jobId, ct);

    public Task<Guid> RebuildAsync(string projection, CancellationToken ct = default)
    {
        var args = Admin.RebuildArgs(projection);
        if (!runtime.Options.Projections.Any(p => p.Name == projection))
            throw new DeedboxException(Errors.UnknownConsumer, $"'{projection}' is not a registered projection, so it cannot be rebuilt. Subscriptions cannot be rebuilt.");
        return Queue(Jobs.Rebuild, args, ct);
    }

    public Task<Guid> SkipAsync(string consumer, Guid eventId, CancellationToken ct = default)
    {
        var args = Admin.SkipArgs(consumer, eventId);
        if (!runtime.Options.Projections.Any(p => p.Name == consumer) && !runtime.Options.Subscriptions.Any(s => s.Name == consumer))
            throw new DeedboxException(Errors.UnknownConsumer, $"'{consumer}' is not a registered projection or subscription.");
        return Queue(Jobs.Skip, args, ct);
    }

    public Task<ErasureResult> EraseSubjectAsync(string subjectId, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        runtime.RequireKeys();
        return Admin.Erase(runtime.Provider, runtime.Clock, tenantId, [subjectId], ct);
    }

    public Task RetireAsync(string projection, CancellationToken ct = default) =>
        Admin.Retire(runtime.Provider, projection, runtime.Options.Runner.HeartbeatInterval * Instances.LiveIntervals, ct);

    public Task<Guid> RebuildSnapshotsAsync(string streamType, CancellationToken ct = default)
    {
        var args = Admin.SnapshotArgs(streamType);
        if (runtime.Registry.FindStream(streamType) is null)
            throw new DeedboxException(Errors.UnregisteredState, $"Stream type '{streamType}' is not registered, so its snapshots cannot be rebuilt.");
        return Queue(Jobs.Snapshots, args, ct);
    }

    public Task<int> RewrapKeysAsync(IMasterKeyProvider target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return runtime.RequireKeys().Rewrap(target, ct);
    }

    public Task ShredTenantAsync(string tenantId, CancellationToken ct = default) =>
        runtime.RequireKeys().Shred(DeedboxContext.ValidTenant(tenantId), ct);

    public Task<ErasureResult> EraseIdentityAsync(string identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        Pseudonymizer.ValidateIdentity(identity);
        return Admin.EraseIdentity(runtime.Provider, runtime.RequirePseudonyms(), runtime.Clock, tenantId, identity, ct);
    }

    public async Task<bool> DestroyPseudonymPeriodAsync(string periodId, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var destroyed = await Admin.DestroyPseudonymPeriod(runtime.Provider, runtime.Clock, tenantId, periodId, ct);
        runtime.Pseudonyms?.Forget(tenantId, periodId);
        return destroyed;
    }

    private Task<Guid> Queue(string kind, JsonObject args, CancellationToken ct) => Jobs.Enqueue(runtime.Provider, runtime.Clock, kind, args, ct);
}
