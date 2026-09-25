using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Deedbox
{
    /// <summary>
    /// Healthy while every consumer progresses, including while it is behind or rebuilding. Unhealthy when a consumer
    /// is stalled, or events are waiting and its checkpoint has not moved for <see cref="RunnerOptions.StallAfter"/>.
    /// Degraded when the app registers a retired projection.
    /// Behind is not unhealthy, so Kubernetes does not restart an app during a rebuild.
    /// </summary>
    internal sealed class DeedboxHealthCheck(DeedboxRuntime runtime, ProjectionSet projections) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            List<CheckpointRow> rows;
            long head;
            await using (var connection = runtime.Provider.CreateConnection())
            {
                await connection.OpenAsync(cancellationToken);
                rows = await runtime.Provider.ReadCheckpoints(connection, cancellationToken);
                head = await runtime.Provider.ReadHead(connection, null, cancellationToken);
            }

            var registered = projections.All.Select(p => p.Name).Concat(projections.Subscriptions.Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
            var now = runtime.Clock.GetUtcNow();
            var data = new Dictionary<string, object>();
            var problems = new List<string>();
            var degraded = new List<string>();
            foreach (var row in rows.Where(r => registered.Contains(r.Name)))
            {
                if (row.Status == CheckpointStatus.Retired)
                {
                    data[row.Name] = row.Status;
                    degraded.Add($"'{row.Name}' is retired, so nothing applies it; rebuild it to use it again, or remove it from the app");
                    continue;
                }

                var lag = Math.Max(0, head - row.Position);
                var inlineRunning = row.Mode == CheckpointMode.Inline && row.Status == CheckpointStatus.Running;
                data[row.Name] = inlineRunning ? $"{row.Status}" : $"{row.Status}, position {row.Position}, lag {lag}";

                if (row.Status == CheckpointStatus.Stalled)
                    problems.Add($"'{row.Name}' is stalled");
                else if (!inlineRunning && lag > 0 && now - row.UpdatedAt > runtime.Options.Runner.StallAfter)
                    problems.Add($"'{row.Name}' has not moved for {(now - row.UpdatedAt).TotalMinutes:F0} minutes with {lag} events waiting");
            }

            if (problems.Count > 0)
                return HealthCheckResult.Unhealthy(string.Join("; ", problems.Concat(degraded)) + ".", data: data);
            return degraded.Count > 0
                ? HealthCheckResult.Degraded(string.Join("; ", degraded) + ".", data: data)
                : HealthCheckResult.Healthy("Deedbox consumers are progressing.", data);
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Adds the Deedbox health check.</summary>
    public static class DeedboxHealthCheckExtensions
    {
        /// <summary>
        /// Adds a check that reports unhealthy only when a projection or subscription is stalled or has stopped moving
        /// while events wait. Rebuilding and catching up report healthy, with detail in the check's data.
        /// </summary>
        /// <param name="builder">The health checks builder.</param>
        /// <param name="name">The check's name.</param>
        public static IHealthChecksBuilder AddDeedboxHealthChecks(this IHealthChecksBuilder builder, string name = "deedbox")
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddSingleton<DeedboxHealthCheck>();
            return builder.AddCheck<DeedboxHealthCheck>(name);
        }
    }
}
