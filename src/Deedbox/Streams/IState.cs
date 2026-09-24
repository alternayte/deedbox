namespace Deedbox;

/// <summary>
/// The state of one stream type: where a stream starts, and how each event changes it.
/// Keep <see cref="Evolve"/> pure; Deedbox replays it to rebuild state.
/// </summary>
/// <typeparam name="TSelf">The state type itself.</typeparam>
public interface IState<TSelf> where TSelf : IState<TSelf>
{
    /// <summary>The state of a stream with no events.</summary>
    static abstract TSelf Initial { get; }

    /// <summary>Applies one event. Return <paramref name="state"/> unchanged for events it ignores.</summary>
    /// <param name="state">The state before the event.</param>
    /// <param name="event">The event.</param>
    static abstract TSelf Evolve(TSelf state, object @event);
}
