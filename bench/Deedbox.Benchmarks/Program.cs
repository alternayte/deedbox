using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Deedbox;
using Deedbox.Benchmarks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

// Measures append throughput and latency across the design doc's matrix:
// writers x events per append x provider x mode, and the time appends hold the position counter.
var options = BenchOptions.Parse(args);
var results = new List<CellResult>();
using var counter = new CounterListener();

foreach (var provider in options.Providers)
{
    await using var database = await Database.Start(provider);
    foreach (var mode in options.Modes)
    {
        foreach (var events in options.Events)
        {
            foreach (var writers in options.Writers)
            {
                var result = await Cell.Run(database, mode, writers, events, options, counter);
                results.Add(result);
                Console.WriteLine(result.ToLine());
            }
        }
    }
}

var markdown = Report.Markdown(results);
Console.WriteLine();
Console.WriteLine(markdown);
if (options.Output is { } output)
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, BenchJson.Default.ListCellResult));
if (options.Markdown is { } markdownFile)
    await File.WriteAllTextAsync(markdownFile, markdown);

if (options.Baseline is not { } baselineFile)
    return 0;

var baseline = JsonSerializer.Deserialize(await File.ReadAllTextAsync(baselineFile), BenchJson.Default.ListCellResult)!;
var regressions = Report.Regressions(baseline, results, options.Threshold);
foreach (var line in regressions)
    Console.Error.WriteLine("REGRESSION " + line);
return regressions.Count == 0 ? 0 : 1;

namespace Deedbox.Benchmarks
{
    internal sealed record BenchOptions(
        string[] Providers, string[] Modes, int[] Writers, int[] Events, TimeSpan Warmup, TimeSpan Duration,
        string? Output, string? Markdown, string? Baseline, double Threshold)
    {
        public static BenchOptions Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < args.Length; i += 2)
                map[args[i].TrimStart('-')] = args[i + 1];

            string[] List(string key, string fallback) => (map.GetValueOrDefault(key) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries);
            int[] Numbers(string key, string fallback) => List(key, fallback).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            TimeSpan Seconds(string key, string fallback) => TimeSpan.FromSeconds(double.Parse(map.GetValueOrDefault(key) ?? fallback, CultureInfo.InvariantCulture));

            return new BenchOptions(
                List("providers", "postgres,sqlserver"), List("modes", "neither,efcore"), Numbers("writers", "1,8,32,128"), Numbers("events", "1,10"),
                Seconds("warmup", "2"), Seconds("duration", "8"), map.GetValueOrDefault("out"), map.GetValueOrDefault("markdown"),
                map.GetValueOrDefault("baseline"), double.Parse(map.GetValueOrDefault("threshold") ?? "0.3", CultureInfo.InvariantCulture));
        }
    }

    internal sealed record CellResult(
        string Provider, string Mode, int Writers, int Events, double AppendsPerSecond, double EventsPerSecond,
        double P50Ms, double P99Ms, double CounterP50Ms, double CounterP99Ms)
    {
        public string Key => $"{Provider}/{Mode}/w{Writers}/e{Events}";

        public string ToLine() => string.Create(CultureInfo.InvariantCulture,
            $"{Key,-28} {AppendsPerSecond,9:F0} appends/s  p50 {P50Ms,7:F2} ms  p99 {P99Ms,7:F2} ms  counter p50 {CounterP50Ms,6:F2} ms  p99 {CounterP99Ms,6:F2} ms");
    }

    internal sealed class Database(string provider, string connectionString, IAsyncDisposable? container) : IAsyncDisposable
    {
        public string Provider { get; } = provider;

        public string ConnectionString { get; } = connectionString;

        public static async Task<Database> Start(string provider)
        {
            var external = Environment.GetEnvironmentVariable(provider == "postgres" ? "DEEDBOX_BENCH_POSTGRES" : "DEEDBOX_BENCH_SQLSERVER");
            if (!string.IsNullOrEmpty(external))
                return new Database(provider, external, null);

            if (provider == "postgres")
            {
                var postgres = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=500").Build();
                await postgres.StartAsync();
                return new Database(provider, postgres.GetConnectionString() + ";Maximum Pool Size=300", postgres);
            }

            var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await sql.StartAsync();
            return new Database(provider, sql.GetConnectionString() + ";Max Pool Size=300", sql);
        }

        public ValueTask DisposeAsync() => container?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    internal static class Cell
    {
        public static async Task<CellResult> Run(Database database, string mode, int writers, int events, BenchOptions options, CounterListener counter)
        {
            var schema = "bench_" + Guid.NewGuid().ToString("N")[..10];
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDeedbox(b =>
            {
                (database.Provider == "postgres" ? b.UsePostgres(database.ConnectionString) : b.UseSqlServer(database.ConnectionString))
                    .Schema(schema)
                    .ApplySchemaOnStartup()
                    .Runner(r => r.Enabled = false)
                    .Stream<Tally>(s => s.Events<Counted>());
                if (mode == "efcore")
                    b.Projection<TallyProjection>("tally", Deedbox.Run.Inline);
            });
            services.AddDbContextFactory<BenchDb>(o => Use(o, database));
            await using var provider = services.BuildServiceProvider();
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StartAsync(CancellationToken.None);
            if (mode == "efcore")
                await BenchDb.CreateTable(provider, database.Provider);

            var batch = Enumerable.Range(0, events).Select(i => (object)new Counted(i)).ToArray();
            var latencies = new ConcurrentBag<double>();
            var measuring = false;
            using var stop = new CancellationTokenSource();

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                var stream = $"tally-{w}";
                using var scope = provider.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
                while (!stop.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    if (mode == "efcore")
                    {
                        await using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BenchDb>>().CreateDbContext();
                        await store.UseDbContext(db).Append(stream, ExpectedVersion.Any, batch);
                    }
                    else
                    {
                        await store.Append(stream, ExpectedVersion.Any, batch);
                    }

                    if (Volatile.Read(ref measuring))
                        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            })).ToList();

            await Task.Delay(options.Warmup);
            counter.Reset();
            Volatile.Write(ref measuring, true);
            var window = Stopwatch.StartNew();
            await Task.Delay(options.Duration);
            Volatile.Write(ref measuring, false);
            var elapsed = window.Elapsed.TotalSeconds;
            var holds = counter.Snapshot();
            await stop.CancelAsync();
            await Task.WhenAll(tasks);

            var sorted = latencies.Order().ToArray();
            return new CellResult(database.Provider, mode, writers, events,
                sorted.Length / elapsed, sorted.Length * events / elapsed,
                Percentile(sorted, 0.50), Percentile(sorted, 0.99), Percentile(holds, 0.50), Percentile(holds, 0.99));
        }

        private static void Use(DbContextOptionsBuilder options, Database database)
        {
            if (database.Provider == "postgres")
                options.UseNpgsql(database.ConnectionString);
            else
                options.UseSqlServer(database.ConnectionString);
        }

        private static double Percentile(double[] sorted, double p) =>
            sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    /// <summary>Collects deedbox.counter.duration: how long appends hold, or wait for, the position counter.</summary>
    internal sealed class CounterListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private ConcurrentBag<double> _values = [];

        public CounterListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Deedbox" && instrument.Name == "deedbox.counter.duration")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((_, value, _, _) => _values.Add(value));
            _listener.Start();
        }

        public void Reset() => _values = [];

        public double[] Snapshot() => [.. _values.Order()];

        public void Dispose() => _listener.Dispose();
    }

    internal static class Report
    {
        public static string Markdown(IEnumerable<CellResult> results)
        {
            var text = new StringBuilder()
                .AppendLine("| Provider | Mode | Writers | Events per append | Appends/s | Events/s | p50 ms | p99 ms | Counter p50 ms | Counter p99 ms |")
                .AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var r in results)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.Provider} | {r.Mode} | {r.Writers} | {r.Events} | {r.AppendsPerSecond:F0} | {r.EventsPerSecond:F0} | {r.P50Ms:F2} | {r.P99Ms:F2} | {r.CounterP50Ms:F2} | {r.CounterP99Ms:F2} |"));
            }

            return text.ToString();
        }

        /// <summary>Cells whose throughput fell more than <paramref name="threshold"/> below the baseline.</summary>
        public static List<string> Regressions(IEnumerable<CellResult> baseline, IEnumerable<CellResult> current, double threshold)
        {
            var now = current.ToDictionary(r => r.Key);
            return baseline
                .Where(b => now.TryGetValue(b.Key, out var c) && c.AppendsPerSecond < b.AppendsPerSecond * (1 - threshold))
                .Select(b => string.Create(CultureInfo.InvariantCulture,
                    $"{b.Key}: {now[b.Key].AppendsPerSecond:F0} appends/s against a baseline of {b.AppendsPerSecond:F0} (more than {threshold:P0} lower)"))
                .ToList();
        }
    }

    [System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(List<CellResult>))]
    internal sealed partial class BenchJson : System.Text.Json.Serialization.JsonSerializerContext;
}
