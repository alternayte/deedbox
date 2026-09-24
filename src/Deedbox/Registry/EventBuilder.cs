using System.Text.Json.Nodes;

namespace Deedbox;

/// <summary>Sets one event type's stored name, old names and upcasters inside <c>Event&lt;TEvent&gt;(...)</c>.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EventBuilder<TEvent> where TEvent : notnull
{
    private readonly EventRegistration _event;

    internal EventBuilder(EventRegistration registration)
    {
        _event = registration;
    }

    /// <summary>Stores the event under <paramref name="name"/> instead of <c>{streamType}.{snake_case(TEvent)}</c>.</summary>
    /// <param name="name">The stored event type name.</param>
    public EventBuilder<TEvent> Name(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _event.Name = name;
        return this;
    }

    /// <summary>
    /// Keeps events stored under an old name readable as <typeparamref name="TEvent"/>, such as after a class rename.
    /// New events are stored under the current name.
    /// </summary>
    /// <param name="oldName">A name events of this type were stored under before.</param>
    public EventBuilder<TEvent> Alias(string oldName)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        _event.Aliases.Add(oldName);
        return this;
    }

    /// <summary>
    /// Upgrades the stored JSON of <paramref name="version"/> to the next version, such as
    /// <c>json =&gt; json["qty"] ??= 1</c>. Steps run in version order after decryption.
    /// </summary>
    /// <param name="version">The stored version this step reads, at least 1.</param>
    /// <param name="upcast">Changes the JSON object in place.</param>
    public EventBuilder<TEvent> From(int version, Action<JsonObject> upcast)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(upcast);
        _event.AddStep(new JsonUpcast(version, upcast));
        return this;
    }

    /// <summary>
    /// Reads the previous version as its own record type and converts it to <typeparamref name="TEvent"/>,
    /// for reshapes that JSON edits cannot express. It is always the last step.
    /// </summary>
    /// <param name="upcast">Converts the old record to the current one.</param>
    /// <typeparam name="TOld">The kept record type of the previous version.</typeparam>
    /// <typeparam name="TNew">The current event type.</typeparam>
    public EventBuilder<TEvent> Upcast<TOld, TNew>(Func<TOld, TNew> upcast)
        where TOld : notnull
        where TNew : TEvent
    {
        ArgumentNullException.ThrowIfNull(upcast);
        _event.AddStep(new TypedUpcast(_event.Version - 1, typeof(TOld), old => upcast((TOld)old)));
        return this;
    }
}
