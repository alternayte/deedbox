using System.Text.RegularExpressions;
using Deedbox.QueueBox;

namespace Deedbox;

/// <summary>Publishes selected events to QueueBox, inside <c>UseQueueBox(...)</c>.</summary>
public sealed partial class QueueBoxBuilder
{
    internal QueueBoxBuilder()
    {
    }

    internal string Table { get; private set; } = "outbox";

    internal string? Schema { get; private set; }

    internal QueueBoxColumns Columns { get; } = new();

    internal Dictionary<Type, Publication> Publications { get; } = [];

    /// <summary>The QueueBox outbox table, when it is not <c>outbox</c> in the default schema.</summary>
    /// <param name="name">The table name.</param>
    /// <param name="schema">The schema, or null for the connection's default schema.</param>
    public QueueBoxBuilder UseTable(string name, string? schema = null)
    {
        Table = Identifier(name);
        Schema = schema is null ? null : Identifier(schema);
        return this;
    }

    /// <summary>The outbox column names, when QueueBox runs with a custom column mapping.</summary>
    /// <param name="configure">Changes the column names.</param>
    public QueueBoxBuilder UseColumns(Action<QueueBoxColumns> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Columns);
        return this;
    }

    /// <summary>
    /// Publishes every <typeparamref name="TEvent"/> to <paramref name="topic"/>, with the event's JSON as the payload.
    /// An event with [PersonalData] needs the payload overload, so personal data never reaches the outbox by default.
    /// </summary>
    /// <param name="topic">The QueueBox topic, such as <c>cart.checked_out</c>.</param>
    /// <typeparam name="TEvent">The event type, or a built-in such as <see cref="SubjectErased"/>.</typeparam>
    public QueueBoxBuilder Publish<TEvent>(string topic) where TEvent : notnull =>
        Add(typeof(TEvent), topic, null);

    /// <summary>Publishes every <typeparamref name="TEvent"/> to <paramref name="topic"/>, with a payload you shape.</summary>
    /// <param name="topic">The QueueBox topic.</param>
    /// <param name="payload">Builds the message payload from the event; Deedbox's JSON options serialize it.</param>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public QueueBoxBuilder Publish<TEvent>(string topic, Func<TEvent, PendingEvent, object> payload) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Add(typeof(TEvent), topic, (e, pending) => payload((TEvent)e, pending));
    }

    private QueueBoxBuilder Add(Type type, string topic, Func<object, PendingEvent, object>? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (topic.Length > 255)
            throw new DeedboxException(Errors.QueueBoxMapping, $"QueueBox topic '{topic}' is longer than 255 characters.");
        if (!Publications.TryAdd(type, new Publication(topic, payload)))
            throw new DeedboxException(Errors.QueueBoxMapping, $"{type.Name} is published twice. Publish each event type to one topic.");
        return this;
    }

    internal static string Identifier(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IdentifierPattern().IsMatch(name))
            throw new DeedboxException(Errors.QueueBoxMapping, $"'{name}' is not a plain SQL identifier. Use letters, digits and underscores.");
        return name;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentifierPattern();
}

/// <summary>The outbox column names Deedbox writes. The defaults are QueueBox's own.</summary>
public sealed class QueueBoxColumns
{
    private string _id = "id";
    private string _topic = "topic";
    private string _key = "key";
    private string _payload = "payload";
    private string _headers = "headers";
    private string _aggregateType = "aggregate_type";

    /// <summary>The message ID column; Deedbox writes the event ID.</summary>
    public string Id { get => _id; set => _id = QueueBoxBuilder.Identifier(value); }

    /// <summary>The topic column.</summary>
    public string Topic { get => _topic; set => _topic = QueueBoxBuilder.Identifier(value); }

    /// <summary>The key column; Deedbox writes the stream ID, so one stream's messages keep their order.</summary>
    public string Key { get => _key; set => _key = QueueBoxBuilder.Identifier(value); }

    /// <summary>The payload column.</summary>
    public string Payload { get => _payload; set => _payload = QueueBoxBuilder.Identifier(value); }

    /// <summary>The headers column.</summary>
    public string Headers { get => _headers; set => _headers = QueueBoxBuilder.Identifier(value); }

    /// <summary>The aggregate type column; Deedbox writes the stream type.</summary>
    public string AggregateType { get => _aggregateType; set => _aggregateType = QueueBoxBuilder.Identifier(value); }
}

/// <summary>Sends selected events to QueueBox for delivery.</summary>
public static class DeedboxQueueBoxExtensions
{
    /// <summary>
    /// Writes a QueueBox outbox row for each published event, in the append's transaction, so a message exists exactly
    /// when its event commits. The row ID is the event ID, the key is the stream ID, and the aggregate type is the stream
    /// type. Headers carry X-Correlation-Id, traceparent and the event's identity. QueueBox then delivers at least once.
    /// </summary>
    /// <param name="builder">The Deedbox builder.</param>
    /// <param name="configure">Chooses the events, topics and payloads.</param>
    public static DeedboxBuilder UseQueueBox(this DeedboxBuilder builder, Action<QueueBoxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new QueueBoxBuilder();
        configure(options);

        builder.AddCheck(runtime => Outbox.Check(options, runtime));
        builder.AddServices(services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, options));
        return builder.OnAppending<OutboxHook>();
    }
}
