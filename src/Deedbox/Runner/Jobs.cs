using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>Queued admin work: rebuilds and audited poison skips. Rows stay in the jobs table as the audit trail.</summary>
internal static class Jobs
{
    public const string Rebuild = "rebuild";
    public const string Skip = "skip";
    public const string Erase = "erase";
    public const string Snapshots = "snapshots";

    public static Task<Guid> Enqueue(DeedboxRuntime runtime, string kind, JsonObject args, CancellationToken ct) =>
        Enqueue(runtime.Provider, runtime.Clock, kind, args, ct);

    public static async Task<Guid> Enqueue(DeedboxProvider provider, TimeProvider clock, string kind, JsonObject args, CancellationToken ct)
    {
        var job = new JobRow(Uuid7.New(), kind, args.ToJsonString(), JobStatus.Queued, null, clock.GetUtcNow(), null, null);
        await using var connection = provider.CreateConnection();
        await connection.OpenAsync(ct);
        await provider.InsertJob(connection, null, job, ct);
        return job.Id;
    }
}

/// <summary>A job that cannot run as asked; it fails at once instead of being retried.</summary>
internal sealed class JobRejected(string message) : Exception(message);

internal sealed partial class JobLoop(DeedboxRuntime runtime, IServiceProvider services, AsyncRunner runner, WakeSignal wake, ILogger logger)
{
    private const int InterruptionRetries = 10;
    private readonly ILogger _logger = logger;
    private readonly Dictionary<Guid, int> _interruptions = [];
    private readonly Dictionary<Guid, DateTimeOffset> _leftForOthers = [];

    private DeedboxProvider Provider => runtime.Provider;

    public async Task Run(CancellationToken ct)
    {
        var delay = runtime.Options.Runner.MinPollDelay;
        while (!ct.IsCancellationRequested)
        {
            bool ran;
            try
            {
                ran = await RunOne(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogJobLoopFailed(ex);
                ran = false;
            }

            if (ran)
            {
                delay = runtime.Options.Runner.MinPollDelay;
                continue;
            }

            await wake.Wait(delay, ct);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, runtime.Options.Runner.MaxPollDelay.Ticks));
        }
    }

    /// <summary>Runs the oldest queued job in one transaction with its own row lock; a crash rolls it back for a retry.</summary>
    public async Task<bool> RunOne(CancellationToken ct)
    {
        Guid id;
        await using (var connection = Provider.CreateConnection())
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // A job left for another instance is looked at again after one liveness window, in case that instance stopped.
            var now = runtime.Clock.GetUtcNow();
            foreach (var expired in _leftForOthers.Where(l => l.Value <= now).Select(l => l.Key).ToList())
                _leftForOthers.Remove(expired);
            var job = await Provider.ClaimJob(connection, transaction, _leftForOthers.Keys, ct);
            if (job is null)
                return false;

            // A job that needs code this instance lacks waits for an instance that has it; the rollback releases it.
            if (await LeaveForAnother(job, connection, transaction, ct))
            {
                _leftForOthers[job.Id] = now + runtime.Options.Runner.HeartbeatInterval * Instances.LiveIntervals;
                LogJobLeft(job.Kind, job.Id);
                return false;
            }

            id = job.Id;
            var started = runtime.Clock.GetUtcNow();
            using var activity = DeedboxDiagnostics.Source.StartActivity("deedbox.job");
            activity?.SetTag("deedbox.job.kind", job.Kind);
            activity?.SetTag("deedbox.job.id", job.Id.ToString());
            try
            {
                var progress = await Execute(job, connection, transaction, ct);
                await Provider.UpdateJob(connection, transaction, job with { Status = JobStatus.Done, Progress = progress.ToJsonString(), StartedAt = started, FinishedAt = runtime.Clock.GetUtcNow() }, ct);
                await transaction.CommitAsync(ct);
                Finished(job.Kind, JobStatus.Done);
                LogJobDone(job.Kind, job.Id);
                runner.Wake();
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not (JobRejected or DeedboxException) && Retry(job.Id))
            {
                // Jobs are idempotent, so anything but a rejection, such as a lost connection or a killed session, is
                // treated like a crash: the job stays queued and runs again.
                Finished(job.Kind, "interrupted");
                LogJobInterrupted(ex, job.Kind, job.Id);
                return false;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await TryRollback(transaction);
                Finished(job.Kind, JobStatus.Failed);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                LogJobFailed(ex, job.Kind, job.Id);
                await MarkFailed(job, started, ex, ct);
                return true;
            }
        }
    }

    /// <summary>
    /// True when this instance cannot run the job, a rebuild of a projection or of a stream type's snapshots that it
    /// does not register, but a live instance can. With no such instance, the job runs here and fails with the reason.
    /// </summary>
    private async Task<bool> LeaveForAnother(JobRow job, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var args = JsonNode.Parse(job.Args)!.AsObject();
        Func<InstanceRow, bool>? canRun = job.Kind switch
        {
            Jobs.Rebuild when args["projection"]?.GetValue<string>() is { } name
                && !services.GetRequiredService<ProjectionSet>().All.Any(p => p.Name == name) => i => i.Consumers.Contains(name, StringComparer.Ordinal),
            Jobs.Snapshots when args["streamType"]?.GetValue<string>() is { } streamType
                && runtime.Registry.FindStream(streamType) is null => i => i.Events.Any(e => Instances.StreamOf(e) == streamType),
            _ => null,
        };
        if (canRun is null)
            return false;

        var live = await Provider.ReadLiveInstances(connection, transaction, runtime.Options.Runner.HeartbeatInterval * Instances.LiveIntervals, ct);
        return live.Any(i => i.Id != runtime.InstanceId && canRun(i));
    }

    private static void Finished(string kind, string status) =>
        DeedboxDiagnostics.JobsFinished.Add(1, DeedboxDiagnostics.Tag("deedbox.job.kind", kind), DeedboxDiagnostics.Tag("deedbox.job.status", status));

    private bool Retry(Guid id)
    {
        var failures = _interruptions.GetValueOrDefault(id) + 1;
        _interruptions[id] = failures;
        return failures <= InterruptionRetries;
    }

    private static async Task TryRollback(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException)
        {
            // The connection is already gone; the database rolled the transaction back.
        }
    }

    private async Task MarkFailed(JobRow job, DateTimeOffset started, Exception ex, CancellationToken ct)
    {
        await using var connection = Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var progress = new JsonObject { ["error"] = ex.Message, ["exception"] = ex.GetType().FullName };
        await Provider.UpdateJob(connection, transaction, job with { Status = JobStatus.Failed, Progress = progress.ToJsonString(), StartedAt = started, FinishedAt = runtime.Clock.GetUtcNow() }, ct);
        await transaction.CommitAsync(ct);
    }

    private Task<JsonObject> Execute(JobRow job, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var args = JsonNode.Parse(job.Args)!.AsObject();
        return job.Kind switch
        {
            Jobs.Rebuild => Rebuild(args["projection"]!.GetValue<string>(), connection, transaction, ct),
            Jobs.Skip => Skip(args["projection"]!.GetValue<string>(), Guid.Parse(args["eventId"]!.GetValue<string>()), connection, transaction, ct),
            Jobs.Erase => Erase(args["tenantId"]!.GetValue<string>(), args["subjectId"]!.GetValue<string>(), ct),
            Jobs.Snapshots => RebuildSnapshots(args["streamType"]!.GetValue<string>(), ct),
            _ => throw new JobRejected($"Unknown job kind '{job.Kind}'."),
        };
    }

    /// <summary>
    /// Resets a projection and replays it from position 0 in the background. For an inline projection, the exclusive
    /// gate lock waits for open appends that applied it inline, so the reset removes their writes too, and holds back
    /// new ones until the rebuilding status commits.
    /// </summary>
    private async Task<JsonObject> Rebuild(string name, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var projection = services.GetRequiredService<ProjectionSet>().All.FirstOrDefault(p => p.Name == name)
            ?? throw new JobRejected($"'{name}' is not a registered projection. Subscriptions cannot be rebuilt.");

        if (projection.Run == Deedbox.Run.Inline)
            await Provider.LockInlineGate(connection, transaction, name, ct);
        var row = await Provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct)
            ?? throw new JobRejected($"Projection '{name}' has no checkpoint row yet; start the app once first.");

        await using (var scope = services.CreateAsyncScope())
        await using (var work = new TransactionWork(connection, transaction, scope.ServiceProvider, [], ct))
        {
            await projection.Instance.Reset(work);
            await work.RunBeforeCounter();
        }

        var mode = projection.Run == Deedbox.Run.Inline ? CheckpointMode.Inline : CheckpointMode.Async;
        await Provider.UpdateCheckpoint(connection, transaction, row with { Position = 0, Status = CheckpointStatus.Rebuilding, Mode = mode, Error = null }, ct);
        return new JsonObject { ["projection"] = name, ["previousPosition"] = row.Position, ["previousStatus"] = row.Status };
    }

    /// <summary>
    /// Erases a subject: deletes the key again (a no-op after the first time), then handles each stream that held their
    /// data in its own transaction. Each stream's pair is removed as it is handled, so a crash resumes where it stopped.
    /// This job's row stays locked by the claiming transaction until it finishes, so no other runner takes it meanwhile.
    /// </summary>
    private async Task<JsonObject> Erase(string tenantId, string subjectId, CancellationToken ct)
    {
        await SubjectErasure.DeleteKey(runtime, tenantId, subjectId, ct);

        List<string> streams;
        await using (var connection = Provider.CreateConnection())
        {
            await connection.OpenAsync(ct);
            streams = await Provider.ReadSubjectStreams(connection, null, tenantId, subjectId, ct);
        }

        foreach (var stream in streams)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenantId;
            var store = (EventStore)scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.EraseFromStream(stream, subjectId, ct);
            DeedboxDiagnostics.ErasedStreams.Add(1);
        }

        return new JsonObject { ["tenantId"] = tenantId, ["subjectId"] = subjectId, ["streams"] = streams.Count };
    }

    /// <summary>
    /// Rebuilds the stored state of every stream of a type from its events, one stream per transaction, in key order.
    /// A rerun after a crash repeats streams already done, which is harmless.
    /// </summary>
    private async Task<JsonObject> RebuildSnapshots(string streamType, CancellationToken ct)
    {
        if (runtime.Registry.FindStream(streamType) is null)
            throw new JobRejected($"Stream type '{streamType}' is not registered in this app.");

        var (tenant, stream, count) = ("", "", 0);
        while (true)
        {
            List<(string TenantId, string StreamId)> page;
            await using (var connection = Provider.CreateConnection())
            {
                await connection.OpenAsync(ct);
                page = await Provider.ReadStreamKeys(connection, streamType, tenant, stream, 100, ct);
            }

            if (page.Count == 0)
                return new JsonObject { ["streamType"] = streamType, ["streams"] = count };

            foreach (var (tenantId, streamId) in page)
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<DeedboxContext>().TenantId = tenantId;
                await ((EventStore)scope.ServiceProvider.GetRequiredService<IEventStore>()).RebuildSnapshot(streamId, ct);
                count++;
            }

            (tenant, stream) = page[^1];
        }
    }

    /// <summary>Moves a stalled consumer past the one event it stalled on. The job row records what was skipped.</summary>
    private async Task<JsonObject> Skip(string name, Guid eventId, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var row = await Provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct)
            ?? throw new JobRejected($"'{name}' has no checkpoint row.");
        if (row.Status != CheckpointStatus.Stalled || ConsumerLoop.StallReason(row.Error) != "poison")
            throw new JobRejected($"'{name}' is not stalled on a poison event, so there is nothing to skip.");

        var next = await Provider.ReadEventsAfter(connection, transaction, row.Position, 1, null, ct);
        if (next.Count == 0 || next[0].EventId != eventId)
            throw new JobRejected($"'{name}' is stalled on another event, not {eventId}. Check the stalled event with deedbox status.");

        var skipped = next[0];
        await Provider.UpdateCheckpoint(connection, transaction, row with { Position = skipped.GlobalPosition, Status = CheckpointStatus.Running, Error = null }, ct);
        return new JsonObject
        {
            ["consumer"] = name,
            ["skipped"] = new JsonObject
            {
                ["eventId"] = skipped.EventId.ToString("D"),
                ["globalPosition"] = skipped.GlobalPosition,
                ["tenantId"] = skipped.TenantId,
                ["streamId"] = skipped.StreamId,
                ["version"] = skipped.Version,
                ["eventType"] = skipped.EventType,
            },
            ["stall"] = row.Error is null ? null : JsonNode.Parse(row.Error),
        };
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Error, Message = "Deedbox job loop failed; retrying.")]
    private partial void LogJobLoopFailed(Exception exception);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Deedbox job {Kind} {Id} done.")]
    private partial void LogJobDone(string kind, Guid id);

    [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "Deedbox job {Kind} {Id} was interrupted; it runs again.")]
    private partial void LogJobInterrupted(Exception exception, string kind, Guid id);

    [LoggerMessage(EventId = 34, Level = LogLevel.Information, Message = "Deedbox job {Kind} {Id} needs code this instance lacks; a live instance that has it runs it.")]
    private partial void LogJobLeft(string kind, Guid id);

        [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "Deedbox job {Kind} {Id} failed.")]
    private partial void LogJobFailed(Exception exception, string kind, Guid id);
}
