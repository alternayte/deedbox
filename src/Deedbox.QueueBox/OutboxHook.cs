using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deedbox.QueueBox;

/// <summary>
/// How one event type is published: a fixed topic with the event or a mapped payload, or a message callback.
/// </summary>
internal sealed record Publication(string? Topic, Func<object, PendingEvent, object>? Payload, Func<object, PendingEvent, QueueBoxMessage?>? Message)
{
    /// <summary>True when the app chooses what reaches the outbox, which the [PersonalData] rule requires.</summary>
    public bool Maps => Payload is not null || Message is not null;
}

/// <summary>Writes one QueueBox outbox row per published event, in the append's transaction.</summary>
internal sealed class OutboxHook(QueueBoxBuilder options, DeedboxRuntime runtime) : IAppendingHook
{
    public async Task OnAppending(AppendingContext context, CancellationToken ct)
    {
        var rows = new List<(PendingEvent Event, string Topic, string Payload, string Headers)>();
        foreach (var e in context.Events)
        {
            if (options.Publications.TryGetValue(e.Event.GetType(), out var publication) && Row(context, e, publication) is { } row)
                rows.Add(row);
        }

        if (rows.Count == 0)
            return;

        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
#pragma warning disable CA2100 // Identifiers are validated plain names; values are parameters.
        command.CommandText = Outbox.Insert(options, runtime.Provider.Name, rows.Count);
#pragma warning restore CA2100
        for (var i = 0; i < rows.Count; i++)
        {
            var (e, topic, payload, headers) = rows[i];
            Add(command, $"id{i}", e.EventId);
            Add(command, $"topic{i}", topic);
            Add(command, $"key{i}", context.StreamId);
            Add(command, $"payload{i}", payload);
            Add(command, $"headers{i}", headers);
            Add(command, $"aggregate{i}", context.StreamType);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The outbox row for one event, or null when a message callback skips it.</summary>
    private (PendingEvent, string, string, string)? Row(AppendingContext context, PendingEvent e, Publication publication)
    {
        if (publication.Message is null)
        {
            var payload = publication.Payload is null
                ? JsonSerializer.Serialize(e.Event, runtime.Registry.ForEvent(e.Event.GetType()).Json)
                : Serialize(publication.Payload(e.Event, e));
            return (e, publication.Topic!, payload, Headers(context, e, null));
        }

        QueueBoxMessage? message;
        try
        {
            message = publication.Message(e.Event, e);
        }
        catch (Exception ex) when (ex is not DeedboxException and not OperationCanceledException)
        {
            throw new DeedboxException(Errors.QueueBoxMapping,
                $"The QueueBox message for {e.Event.GetType().Name} '{e.EventType}' failed, so the append rolls back: {ex.Message}", ex);
        }

        if (message is null)
            return null;
        if (message.Payload is null)
            throw new DeedboxException(Errors.QueueBoxMapping, $"The QueueBox message for {e.Event.GetType().Name} has no payload.");
        return (e, QueueBoxBuilder.Topic(message.Topic), Serialize(message.Payload), Headers(context, e, message.Headers));
    }

    private string Serialize(object payload) => JsonSerializer.Serialize(payload, runtime.Json.TypeInfo(payload.GetType()));

    /// <summary>Deedbox's default headers, then the app's headers, which replace a default of the same name.</summary>
    private static string Headers(AppendingContext context, PendingEvent e, IReadOnlyDictionary<string, string>? extra)
    {
        var headers = new JsonObject
        {
            ["x-deedbox-event-id"] = e.EventId.ToString("D"),
            ["x-deedbox-event-type"] = e.EventType,
            ["x-deedbox-event-version"] = e.EventVersion.ToString(CultureInfo.InvariantCulture),
            ["x-deedbox-stream-id"] = context.StreamId,
            ["x-deedbox-stream-version"] = e.Version.ToString(CultureInfo.InvariantCulture),
        };
        if (context.TenantId.Length > 0)
            headers["x-deedbox-tenant-id"] = context.TenantId;
        if (e.Metadata.CorrelationId is { } correlation)
            headers["X-Correlation-Id"] = correlation;
        if (e.Metadata.CausationId is { } causation)
            headers["x-deedbox-causation-id"] = causation;
        if (e.Metadata.TraceParent is { } traceParent)
            headers["traceparent"] = traceParent;
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(name) || value is null)
                throw new DeedboxException(Errors.QueueBoxMapping, $"A QueueBox header for {e.Event.GetType().Name} has an empty name or a null value.");
            // Header names are case-insensitive on every transport, so the app's name replaces a default in any case.
            if (headers.Select(h => h.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
                headers.Remove(existing);
            headers[name] = value;
        }

        return headers.ToJsonString();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal static class Outbox
{
    /// <summary>Fails start-up for a publication of an unregistered event, or of personal data with no payload mapping.</summary>
    public static void Check(QueueBoxBuilder options, EventRegistry registry)
    {
        foreach (var (type, publication) in options.Publications)
        {
            if (!registry.IsRegistered(type))
                throw new DeedboxException(Errors.QueueBoxMapping, $"{type.Name} is published to QueueBox but is not a registered event.");

            if (!publication.Maps && registry.ForEvent(type).PersonalFields.Count > 0)
            {
                throw new DeedboxException(Errors.QueueBoxMapping,
                    $"{type.Name} has [PersonalData], so publishing it whole would put personal data in the outbox in plain text. " +
                    $"Use Publish<{type.Name}>(topic, (e, info) => new {{ ... }}) or a message callback, and include only what the receiver needs.");
            }
        }
    }

    public static string Insert(QueueBoxBuilder options, string provider, int rows)
    {
        var postgres = provider == "postgres";
        string Quote(string name) => postgres ? $"\"{name}\"" : $"[{name}]";
        var table = options.Schema is null ? Quote(options.Table) : $"{Quote(options.Schema)}.{Quote(options.Table)}";
        var c = options.Columns;
        var json = postgres ? "CAST({0} AS jsonb)" : "{0}";

        var sql = new StringBuilder()
            .Append("INSERT INTO ").Append(table).Append(" (")
            .AppendJoin(", ", new[] { c.Id, c.Topic, c.Key, c.Payload, c.Headers, c.AggregateType }.Select(Quote))
            .Append(") VALUES ");
        for (var i = 0; i < rows; i++)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append(CultureInfo.InvariantCulture, $"(@id{i}, @topic{i}, @key{i}, ")
                .Append(string.Format(CultureInfo.InvariantCulture, json, $"@payload{i}")).Append(", ")
                .Append(string.Format(CultureInfo.InvariantCulture, json, $"@headers{i}"))
                .Append(CultureInfo.InvariantCulture, $", @aggregate{i})");
        }

        return sql.ToString();
    }
}
