using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Runner;

namespace Deedbox.Tests.Operations;

/// <summary>
/// Listeners are process-wide. A listener on Deedbox's spans makes the append span current, so tests running at the
/// same time would capture its trace context; these tests run alone.
/// </summary>
[CollectionDefinition(nameof(ProcessWideListeners), DisableParallelization = true)]
public sealed class ProcessWideListeners;

[Collection(nameof(ProcessWideListeners))]
public sealed class PostgresDiagnosticsTests(Databases databases) : DiagnosticsTests(databases, Db.Postgres);

[Collection(nameof(ProcessWideListeners))]
public sealed class SqlServerDiagnosticsTests(Databases databases) : DiagnosticsTests(databases, Db.SqlServer);

public abstract class DiagnosticsTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Fact]
    public async Task Appends_and_batches_emit_spans_and_metrics()
    {
        var spans = new ConcurrentQueue<Activity>();
        var measurements = new ConcurrentDictionary<string, double>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Deedbox",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.GetTagItem("deedbox.stream_id") is "diag-1" || a.GetTagItem("deedbox.consumer") is "diag_applied")
                    spans.Enqueue(a);
            },
        };
        ActivitySource.AddActivityListener(activities);
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Deedbox")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<long>((i, value, tags, _) => Record(measurements, i, value, tags));
        meters.SetMeasurementEventCallback<double>((i, value, tags, _) => Record(measurements, i, value, tags));
        meters.SetMeasurementEventCallback<int>((i, value, tags, _) => Record(measurements, i, value, tags));
        meters.Start();

        var host = await StartHost(NewProbe(), b => b.Projection<AsyncApplied>("diag_applied", Run.Async));
        var store = StoreOf(host);
        await store.Append("diag-1", ExpectedVersion.NoStream, [new ItemAdded("a", 1), new ItemAdded("b", 1)]);
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.Append("diag-1", ExpectedVersion.NoStream, [new ItemAdded("c", 1)]));
        await store.Load<Cart>("diag-1");
        await WaitForCaughtUp(host, "diag_applied");
        meters.RecordObservableInstruments();

        var names = spans.Select(s => s.OperationName).ToHashSet();
        Assert.Superset(new HashSet<string> { "deedbox.append", "deedbox.load", "deedbox.batch", "deedbox.handle diag_applied" }, names);
        var handle = spans.First(s => s.OperationName == "deedbox.handle diag_applied");
        var append = spans.First(s => s.OperationName == "deedbox.append");
        Assert.Equal(append.TraceId, handle.TraceId);

        Assert.True(measurements.GetValueOrDefault("deedbox.events.appended|cart") >= 2);
        Assert.True(measurements.GetValueOrDefault("deedbox.append.conflicts|cart") >= 1);
        Assert.True(measurements.ContainsKey("deedbox.append.duration|cart"));
        Assert.True(measurements.ContainsKey("deedbox.counter.duration|"));
        Assert.True(measurements.ContainsKey("deedbox.consumer.batch.duration|diag_applied"));
        Assert.Equal(0, measurements.GetValueOrDefault("deedbox.consumer.lag|diag_applied", -1));
        Assert.Equal(0, measurements.GetValueOrDefault("deedbox.consumer.status|diag_applied", -1));
    }

    private static void Record(ConcurrentDictionary<string, double> measurements, Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? tag = null;
        foreach (var t in tags)
        {
            if (t.Key is "deedbox.stream_type" or "deedbox.consumer")
                tag = t.Value?.ToString();
        }

        var key = $"{instrument.Name}|{tag}";
        if (instrument is ObservableGauge<long> or ObservableGauge<int> or ObservableGauge<double>)
            measurements[key] = value;
        else
            measurements.AddOrUpdate(key, value, (_, v) => v + value);
    }
}

public sealed class ErrorCatalogueTests
{
    [Fact]
    public void Every_error_code_is_unique_and_has_a_catalogue_title()
    {
        var codes = typeof(DeedboxException).Assembly.GetType("Deedbox.Errors")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^DBX[0-9]{3}$", c));
        Assert.Equal(codes.Order(), Errors.Titles.Keys.Order());
        Assert.All(Errors.Titles.Values, title => Assert.False(string.IsNullOrWhiteSpace(title)));
    }

    [Fact]
    public void Every_error_message_ends_with_its_docs_page()
    {
        var error = new DeedboxException("DBX014", "The stream moved.");

        Assert.Equal("DBX014: The stream moved. See https://deedbox-docs.pages.dev/reference/errors/dbx014/", error.Message);
    }
}
