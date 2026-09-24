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

    public static async Task<Guid> Enqueue(DeedboxRuntime runtime, string kind, JsonObject args, CancellationToken ct)
    {
        var job = new JobRow(Uuid7.New(), kind, args.ToJsonString(), JobStatus.Queued, null, runtime.Clock.GetUtcNow(), null, null);
        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await runtime.Provider.InsertJob(connection, null, job, ct);
        return job.Id;
    }
}

internal sealed partial class JobLoop(DeedboxRuntime runtime, IServiceProvider services, AsyncRunner runner, WakeSignal wake, ILogger logger)
{
    private readonly ILogger _logger = logger;

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
            var job = await Provider.ClaimJob(connection, transaction, ct);
            if (job is null)
                return false;

            id = job.Id;
            var started = runtime.Clock.GetUtcNow();
            try
            {
                var progress = await Execute(job, connection, transaction, ct);
                await Provider.UpdateJob(connection, transaction, job with { Status = JobStatus.Done, Progress = progress.ToJsonString(), StartedAt = started, FinishedAt = runtime.Clock.GetUtcNow() }, ct);
                await transaction.CommitAsync(ct);
                LogJobDone(job.Kind, job.Id);
                runner.Wake();
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await transaction.RollbackAsync(ct);
                LogJobFailed(ex, job.Kind, job.Id);
                await MarkFailed(job, started, ex, ct);
                return true;
            }
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
            _ => throw new InvalidOperationException($"Unknown job kind '{job.Kind}'."),
        };
    }

    /// <summary>
    /// Resets a projection and replays it from position 0 in the background. The exclusive checkpoint lock waits
    /// for open appends that applied it inline, so the reset removes their writes too, and holds back new ones until
    /// the rebuilding status commits.
    /// </summary>
    private async Task<JsonObject> Rebuild(string name, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var projection = services.GetRequiredService<ProjectionSet>().All.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"'{name}' is not a registered projection. Subscriptions cannot be rebuilt.");

        var row = await Provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct)
            ?? throw new InvalidOperationException($"Projection '{name}' has no checkpoint row yet; start the app once first.");

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

    /// <summary>Moves a stalled consumer past the one event it stalled on. The job row records what was skipped.</summary>
    private async Task<JsonObject> Skip(string name, Guid eventId, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var row = await Provider.LockCheckpoint(connection, transaction, name, CheckpointLock.Exclusive, ct)
            ?? throw new InvalidOperationException($"'{name}' has no checkpoint row.");
        if (row.Status != CheckpointStatus.Stalled || ConsumerLoop.StallReason(row.Error) != "poison")
            throw new InvalidOperationException($"'{name}' is not stalled on a poison event, so there is nothing to skip.");

        var next = await Provider.ReadEventsAfter(connection, transaction, row.Position, 1, null, ct);
        if (next.Count == 0 || next[0].EventId != eventId)
            throw new InvalidOperationException($"'{name}' is stalled on another event, not {eventId}. Check the stalled event with deedbox status.");

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

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "Deedbox job {Kind} {Id} failed.")]
    private partial void LogJobFailed(Exception exception, string kind, Guid id);
}
