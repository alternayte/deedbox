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
            .Select(r => new ConsumerStatus(r.Name, r.Mode, r.Status, r.Position,
                (r.Mode == CheckpointMode.Inline && r.Status == CheckpointStatus.Running) || r.Status == CheckpointStatus.Retired ? 0 : Math.Max(0, head - r.Position),
                r.UpdatedAt, r.Error))
            .ToList();
        var jobs = (await provider.ReadJobs(connection, 20, ct)).Select(Info).ToList();
        return new StoreStatus(head, consumers, jobs);
    }

    public static async Task<JobInfo?> Job(DeedboxProvider provider, Guid id, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        return await provider.ReadJob(connection, id, ct) is { } job ? Info(job) : null;
    }

    /// <summary>Deletes a subject's key and clears their streams' stored state: the immediate part of an erasure.</summary>
    public static async Task DeleteSubjectKey(DeedboxProvider provider, string tenantId, string subjectId, CancellationToken ct)
    {
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await provider.DeleteSubjectKey(connection, transaction, tenantId, subjectId, ct);
        await transaction.CommitAsync(ct);
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

    private static JobInfo Info(JobRow job) => new(job.Id, job.Kind, job.Args, job.Status, job.Progress, job.CreatedAt, job.FinishedAt);
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

    public async Task<Guid> EraseSubjectAsync(string subjectId, string tenantId = "", CancellationToken ct = default)
    {
        var args = Admin.EraseArgs(tenantId, subjectId);
        runtime.RequireKeys();
        await SubjectErasure.DeleteKey(runtime, tenantId, subjectId, ct);
        return await Queue(Jobs.Erase, args, ct);
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

    private Task<Guid> Queue(string kind, JsonObject args, CancellationToken ct) => Jobs.Enqueue(runtime.Provider, runtime.Clock, kind, args, ct);
}
