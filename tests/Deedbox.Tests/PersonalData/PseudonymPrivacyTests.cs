using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Operations;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Deedbox.Tests.PersonalData.PseudonymTests;

namespace Deedbox.Tests.PersonalData;

// Its span and metric listeners are process-wide, so it runs apart from tests that assert on trace context.
[Collection(nameof(ProcessWideListeners))]
public sealed class PostgresPseudonymPrivacyTests(Databases databases) : PseudonymPrivacyTests(databases, Db.Postgres);

[Collection(nameof(ProcessWideListeners))]
public sealed class SqlServerPseudonymPrivacyTests(Databases databases) : PseudonymPrivacyTests(databases, Db.SqlServer);

public abstract class PseudonymPrivacyTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Fact]
    public async Task No_table_log_span_metric_or_error_holds_the_identity_after_writes_and_erasure()
    {
        using var capture = new Telemetry();
        var identity = $"email:{Guid.NewGuid():N}@example.org";
        var probe = NewProbe();
        var host = await StartHost(probe, b => { InDatabase(b); b.Subscription<ReviewerMail>("mail"); }, services: capture.Register);
        var acme = Pseudonyms(host, "acme");
        var q1 = await acme.SubjectForAsync(identity, "2026-Q1");
        var q2 = await acme.SubjectForAsync(identity, "2026-Q2");
        var bob = await acme.SubjectForAsync("github:bob", "2026-Q2");
        await Store(host, "acme").Append("m-1", ExpectedVersion.NoStream, [Invite(q1, "Alice"), Invite(q2, "Alice again"), Invite(bob, "Bob")]);
        await Store(host, "acme").Append("m-2", ExpectedVersion.NoStream, [Invite(q2, "Alice")]);
        await WaitForCaughtUp(host, "mail");

        foreach (var job in (await AdminOf(host).EraseIdentityAsync(identity, "acme")).JobIds)
            Assert.Equal("done", (await WaitForJob(host, job)).Status);
        await AdminOf(host).DestroyPseudonymPeriodAsync("2026-Q1", "acme");
        await WaitForCaughtUp(host, "mail");

        // Errors on the pseudonym path never echo the identity.
        var errors = new List<Exception>
        {
            await Assert.ThrowsAsync<DeedboxException>(() => acme.SubjectForAsync(identity, "2026-Q1")),
            await Assert.ThrowsAsync<ArgumentException>(() => acme.SubjectForAsync(identity + " ", "2026-Q2")),
            await Assert.ThrowsAsync<ArgumentException>(() => acme.SubjectForAsync(identity, "not a period")),
        };
        var renamed = await StartHost(probe, b => { InDatabase(b); b.PseudonymPrefix("user:"); }, services: capture.Register);
        errors.Add(await Assert.ThrowsAsync<DeedboxException>(() => Pseudonyms(renamed, "acme").SubjectForAsync(identity, "2026-Q2")));
        Assert.All(errors, e => Assert.DoesNotContain(identity, e.ToString(), StringComparison.Ordinal));

        // The scan finds a subject ID that is stored, so a miss for the identity means something.
        Assert.NotEmpty(await ColumnsHolding(bob));
        Assert.Empty(await ColumnsHolding(identity));
        Assert.Empty(await ColumnsHolding(identity["email:".Length..]));
        await StopHost(renamed);
        await StopHost(host);
        Assert.NotEmpty(capture.Texts);
        Assert.DoesNotContain(capture.Texts, t => t.Contains(identity["email:".Length..], StringComparison.Ordinal));
    }

    /// <summary>Every (table, column) of the test schema whose value, as text or as UTF-8 bytes, contains <paramref name="text"/>.</summary>
    private async Task<List<string>> ColumnsHolding(string text)
    {
        await using var connection = await OpenConnection();
        var columns = new List<(string Table, string Column, string Type)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = $"SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = '{Schema}'";
            await using var reader = await list.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Contains(columns, c => c.Table == "pseudonym_keys");
        var hits = new List<string>();
        foreach (var (table, column, type) in columns)
        {
            await using var command = connection.CreateCommand();
            var binary = type is "bytea" or "varbinary" or "binary";
            command.CommandText = Db == Db.Postgres
                ? binary
                    ? $"SELECT COUNT(*) FROM {Schema}.\"{table}\" WHERE position(convert_to(@text, 'UTF8') in \"{column}\") > 0"
                    : $"SELECT COUNT(*) FROM {Schema}.\"{table}\" WHERE strpos(CAST(\"{column}\" AS text), @text) > 0"
                : binary
                    ? $"SELECT COUNT(*) FROM [{Schema}].[{table}] WHERE CHARINDEX(CAST(@text AS varchar(max)), CAST([{column}] AS varchar(max))) > 0"
                    : $"SELECT COUNT(*) FROM [{Schema}].[{table}] WHERE CHARINDEX(@text, CAST([{column}] AS nvarchar(max))) > 0";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "text";
            parameter.Value = text;
            command.Parameters.Add(parameter);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) > 0)
                hits.Add($"{table}.{column}");
        }

        return hits;
    }

    /// <summary>Every log message, span and metric tag the process emits while it lives.</summary>
    private sealed class Telemetry : IDisposable
    {
        private readonly ActivityListener _spans;
        private readonly MeterListener _metrics = new();

        public Telemetry()
        {
            _spans = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Deedbox",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Texts.Add(string.Join(" ", new[] { activity.DisplayName, activity.StatusDescription ?? "" }
                    .Concat(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"))
                    .Concat(activity.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}"))))),
            };
            ActivitySource.AddActivityListener(_spans);
            _metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Deedbox")
                    listener.EnableMeasurementEvents(instrument);
            };
            _metrics.SetMeasurementEventCallback<long>((i, _, tags, _) => Record(i, tags));
            _metrics.SetMeasurementEventCallback<int>((i, _, tags, _) => Record(i, tags));
            _metrics.SetMeasurementEventCallback<double>((i, _, tags, _) => Record(i, tags));
            _metrics.Start();
        }

        public ConcurrentBag<string> Texts { get; } = [];

        public void Register(IServiceCollection services) =>
            services.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(new Provider(this)));

        public void Dispose()
        {
            _spans.Dispose();
            _metrics.Dispose();
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = instrument.Name;
            foreach (var tag in tags)
                text += $" {tag.Key}={tag.Value}";
            Texts.Add(text);
        }

        private sealed class Provider(Telemetry telemetry) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Logger(telemetry);

            public void Dispose()
            {
            }
        }

        private sealed class Logger(Telemetry telemetry) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                telemetry.Texts.Add(formatter(state, exception) + " " + exception);
        }
    }
}
