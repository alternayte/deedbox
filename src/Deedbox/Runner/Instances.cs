using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>
/// One live app instance, as its heartbeat row records it: what it runs, and what it can append. Event types are
/// written as <c>streamType/eventType</c>, with current stored names.
/// </summary>
internal sealed record InstanceRow(
    Guid Id, string Host, string App, IReadOnlyList<string> Consumers, IReadOnlyList<string> Inline, IReadOnlyList<string> Events, DateTimeOffset SeenAt);

/// <summary>
/// The events a projection handles, stored with its checkpoint so that an instance without the projection's code can
/// tell whether its own appends would skip it. Events are <c>streamType/eventType</c>, current names and aliases.
/// </summary>
internal sealed record Handles(IReadOnlyList<string> Events, IReadOnlyList<string> BuiltIns)
{
    public static Handles Of(EventRegistry registry, IEnumerable<Type> types)
    {
        var registrations = types.Select(registry.ForEvent).ToList();
        return new Handles(
            [.. registrations.Where(e => !e.IsBuiltIn).SelectMany(e => e.Aliases.Prepend(e.Name).Select(n => $"{e.Stream!.Name}/{n}")).Distinct(StringComparer.Ordinal)],
            [.. registrations.Where(e => e.IsBuiltIn).Select(e => e.Name)]);
    }

    public string ToJson() => JsonSerializer.Serialize(this, InstanceJson.Default.Handles);

    public static Handles? FromJson(string? json) => json is null ? null : JsonSerializer.Deserialize(json, InstanceJson.Default.Handles);
}

internal static class Instances
{
    /// <summary>An instance counts as live while its heartbeat is younger than this many intervals.</summary>
    public const int LiveIntervals = 3;

    /// <summary>The liveness window the CLI uses; apps derive theirs from <see cref="RunnerOptions.HeartbeatInterval"/>.</summary>
    public static readonly TimeSpan DefaultLiveFor = TimeSpan.FromSeconds(30);

    public static InstanceRow Self(DeedboxRuntime runtime, ProjectionSet projections, string app) => new(
        runtime.InstanceId,
        Environment.MachineName,
        app,
        [.. projections.All.Select(p => p.Name).Concat(projections.Subscriptions.Select(s => s.Name))],
        [.. projections.Inline.Select(p => p.Name)],
        [.. runtime.Registry.Streams.SelectMany(s => s.Events.Select(e => $"{s.Name}/{e.Name}"))],
        DateTimeOffset.MinValue);

    /// <summary>
    /// True when <paramref name="instance"/> can append an event that <paramref name="projection"/> handles, but does not
    /// run the projection inline, so its appends would skip it. Built-in events go on streams of the instance's own
    /// stream types, so they count only where those types meet the projection's.
    /// </summary>
    public static bool Skips(InstanceRow instance, string projection, Handles handles)
    {
        if (instance.Inline.Contains(projection, StringComparer.Ordinal))
            return false;
        if (handles.Events.Intersect(instance.Events, StringComparer.Ordinal).Any())
            return true;
        if (handles.BuiltIns.Count == 0)
            return false;

        var projectionStreams = handles.Events.Select(StreamOf).ToHashSet(StringComparer.Ordinal);
        var instanceStreams = instance.Events.Select(StreamOf).ToList();
        return projectionStreams.Count == 0 ? instanceStreams.Count > 0 : instanceStreams.Any(projectionStreams.Contains);
    }

    public static string Describe(IEnumerable<InstanceRow> instances) =>
        string.Join(", ", instances.Select(i => $"{i.App} on {i.Host} ({i.Id:D})"));

    public static string ListJson(IReadOnlyList<string> names) => JsonSerializer.Serialize(names.ToArray(), InstanceJson.Default.StringArray);

    public static IReadOnlyList<string> ReadList(string json) => JsonSerializer.Deserialize(json, InstanceJson.Default.StringArray) ?? [];

    public static string StreamOf(string pair) => pair[..pair.IndexOf('/', StringComparison.Ordinal)];
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Handles))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class InstanceJson : JsonSerializerContext;

/// <summary>
/// Keeps this instance's heartbeat row current, and deletes it on a clean stop. Start-up writes the first beat, before
/// anything else looks at the other instances.
/// </summary>
internal sealed partial class InstanceHeartbeat(DeedboxRuntime runtime, IServiceProvider services, ILogger<InstanceHeartbeat> logger) : BackgroundService
{
    private readonly ILogger _logger = logger;
    private InstanceRow? _self;

    public InstanceRow Self => _self ??= Instances.Self(runtime, services.GetRequiredService<ProjectionSet>(),
        services.GetService<IHostEnvironment>()?.ApplicationName ?? "app");

    public TimeSpan LiveFor => runtime.Options.Runner.HeartbeatInterval * Instances.LiveIntervals;

    public async Task Beat(CancellationToken ct)
    {
        await using var connection = runtime.Provider.CreateConnection();
        await connection.OpenAsync(ct);
        await runtime.Provider.Beat(connection, Self, LiveFor, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await AsyncRunner.Delay(runtime.Options.Runner.HeartbeatInterval, stoppingToken);
            try
            {
                await Beat(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogBeatFailed(ex);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await using var connection = runtime.Provider.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await runtime.Provider.Leave(connection, runtime.InstanceId, cancellationToken);
        }
        catch (Exception ex)
        {
            // The row ages out instead.
            LogLeaveFailed(ex);
        }
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Deedbox could not write this instance's heartbeat; it retries.")]
    private partial void LogBeatFailed(Exception ex);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Deedbox could not remove this instance's heartbeat; it ages out.")]
    private partial void LogLeaveFailed(Exception ex);
}
