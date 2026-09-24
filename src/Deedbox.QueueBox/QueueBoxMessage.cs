namespace Deedbox;

/// <summary>
/// One QueueBox message, as a <c>Publish&lt;TEvent&gt;((e, pending) =&gt; ...)</c> callback builds it for one event.
/// Deedbox still writes the event ID as the row ID, the stream ID as the key and the stream type as the aggregate type.
/// </summary>
public sealed class QueueBoxMessage
{
    /// <summary>Creates a message.</summary>
    /// <param name="topic">The QueueBox topic, at most 255 characters.</param>
    /// <param name="payload">The payload; Deedbox's JSON options serialize it.</param>
    public QueueBoxMessage(string topic, object payload)
    {
        Topic = topic;
        Payload = payload;
    }

    /// <summary>The QueueBox topic.</summary>
    public string Topic { get; }

    /// <summary>The payload.</summary>
    public object Payload { get; }

    /// <summary>
    /// Headers added to Deedbox's default headers. A header with the name of a default replaces its value; the defaults
    /// cannot be removed. Deedbox does not forward <see cref="EventMetadata.Headers"/>; copy them here to send them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
