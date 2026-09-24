using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Deedbox;

/// <summary>
/// The ActivitySource and Meter, both named Deedbox. Spans cover append, load, execute, stream deletion, projection
/// batches, handler calls and jobs. Instrument names start with <c>deedbox.</c>.
/// </summary>
internal static class DeedboxDiagnostics
{
    public const string Name = "Deedbox";

    public static readonly ActivitySource Source = new(Name, typeof(DeedboxDiagnostics).Assembly.GetName().Version?.ToString());

    public static readonly Meter Meter = new(Name, typeof(DeedboxDiagnostics).Assembly.GetName().Version?.ToString());

    public static readonly Histogram<double> AppendDuration = Meter.CreateHistogram<double>("deedbox.append.duration", "ms", "Time from the start of an append to its commit.");

    public static readonly Counter<long> EventsAppended = Meter.CreateCounter<long>("deedbox.events.appended", "{event}", "Events appended.");

    public static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("deedbox.append.conflicts", "{conflict}", "Appends that found the stream at another version.");

    public static readonly Counter<long> ExecuteRetries = Meter.CreateCounter<long>("deedbox.execute.retries", "{retry}", "Execute calls that ran their decision again after a conflict.");

    public static readonly Histogram<double> CounterDuration = Meter.CreateHistogram<double>("deedbox.counter.duration", "ms",
        "Time from the position counter update, including any wait for its lock, to the end of the append.");

    public static readonly Histogram<double> BatchDuration = Meter.CreateHistogram<double>("deedbox.consumer.batch.duration", "ms", "Time to apply one batch.");

    public static readonly Counter<long> HandlerFailures = Meter.CreateCounter<long>("deedbox.consumer.failures", "{failure}", "Events a handler failed on, before retries.");

    public static readonly Counter<long> Stalls = Meter.CreateCounter<long>("deedbox.consumer.stalls", "{stall}", "Consumers that stalled on a poison event.");

    public static readonly Counter<long> JobsFinished = Meter.CreateCounter<long>("deedbox.jobs", "{job}", "Jobs that finished, failed or were interrupted.");

    public static readonly Counter<long> ErasedStreams = Meter.CreateCounter<long>("deedbox.erasure.streams", "{stream}", "Streams an erasure job has handled.");

    public static readonly Counter<long> Decrypts = Meter.CreateCounter<long>("deedbox.personal_data.decrypts", "{field}", "Personal-data fields decrypted.");

    public static readonly Counter<long> Redactions = Meter.CreateCounter<long>("deedbox.personal_data.redactions", "{field}", "Personal-data fields read as erased.");

    /// <summary>Live consumer loops, for the lag and status gauges.</summary>
    public static readonly ConcurrentDictionary<ConsumerLoop, string> Loops = new();

    static DeedboxDiagnostics()
    {
        Meter.CreateObservableGauge("deedbox.consumer.lag", () => Loops.Select(l => new Measurement<long>(l.Key.Lag, Tags(l))), "{position}",
            "How many positions a consumer is behind the head.");
        Meter.CreateObservableGauge("deedbox.consumer.lag.seconds", () => Loops.Select(l => new Measurement<double>(l.Key.LagSeconds, Tags(l))), "s",
            "How old the last applied event was when the consumer was behind; 0 when caught up.");
        Meter.CreateObservableGauge("deedbox.consumer.status", () => Loops.Select(l => new Measurement<int>(l.Key.StatusCode, Tags(l))), "{status}",
            "0 running, 1 rebuilding, 2 stalled.");
    }

    public static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);

    private static KeyValuePair<string, object?>[] Tags(KeyValuePair<ConsumerLoop, string> loop) =>
        [Tag("deedbox.consumer", loop.Key.Consumer.Name), Tag("deedbox.schema", loop.Value)];
}
