using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Deedbox.Testing;

/// <summary>
/// Given/When/Then for pure decisions: given past events, when a decision runs on the folded state, then it
/// returns the expected events. Works with any test framework; a failure throws <see cref="DeciderAssertionException"/>.
/// </summary>
public static class Decider
{
    /// <summary>Folds <paramref name="events"/> over <typeparamref name="TState"/>'s initial state.</summary>
    /// <param name="events">The stream's past events, oldest first; none for a new stream.</param>
    /// <typeparam name="TState">The stream's state type.</typeparam>
    public static DeciderScenario<TState> Given<TState>(params object[] events) where TState : IState<TState>
    {
        ArgumentNullException.ThrowIfNull(events);
        var state = TState.Initial;
        foreach (var e in events)
            state = TState.Evolve(state, e);
        return new DeciderScenario<TState>(state);
    }
}

/// <summary>The state after the given events.</summary>
/// <typeparam name="TState">The stream's state type.</typeparam>
public sealed class DeciderScenario<TState>
{
    internal DeciderScenario(TState state)
    {
        State = state;
    }

    /// <summary>The state after the given events.</summary>
    public TState State { get; }

    /// <summary>Runs the decision on <see cref="State"/> and captures its events or its exception.</summary>
    /// <param name="decide">The decision, as passed to <c>Execute</c>.</param>
    public DeciderOutcome When(Func<TState, IEnumerable<object>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        try
        {
            return new DeciderOutcome(decide(State).ToList(), null);
        }
#pragma warning disable CA1031 // The exception is the outcome under test.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new DeciderOutcome(null, ex);
        }
    }
}

/// <summary>What a decision returned, checked with Then, ThenNothing or ThenThrows.</summary>
public sealed class DeciderOutcome
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private readonly IReadOnlyList<object>? _events;
    private readonly Exception? _exception;

    internal DeciderOutcome(IReadOnlyList<object>? events, Exception? exception)
    {
        _events = events;
        _exception = exception;
    }

    /// <summary>
    /// Checks that the decision returned exactly <paramref name="expected"/>, in order. Events compare by type
    /// and by their JSON, so records holding collections compare by content.
    /// </summary>
    /// <param name="expected">The expected events.</param>
    public void Then(params object[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var actual = Events();
        var want = expected.Select(Describe).ToList();
        var got = actual.Select(Describe).ToList();
        if (!want.SequenceEqual(got, StringComparer.Ordinal))
        {
            throw new DeciderAssertionException(
                $"Expected events:{Environment.NewLine}{List(want)}{Environment.NewLine}Actual events:{Environment.NewLine}{List(got)}");
        }
    }

    /// <summary>Checks that the decision returned no events.</summary>
    public void ThenNothing() => Then();

    /// <summary>Checks that the decision threw <typeparamref name="TException"/> or a subtype, and returns it.</summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    public TException ThenThrows<TException>() where TException : Exception
    {
        if (_exception is TException expected)
            return expected;
        throw new DeciderAssertionException(_exception is null
            ? $"Expected {typeof(TException).Name}, but the decision returned events:{Environment.NewLine}{List(_events!.Select(Describe))}"
            : $"Expected {typeof(TException).Name}, but the decision threw {_exception.GetType().Name}: {_exception.Message}", _exception);
    }

    private IReadOnlyList<object> Events() => _exception is null
        ? _events!
        : throw new DeciderAssertionException($"The decision threw {_exception.GetType().Name}: {_exception.Message}", _exception);

    private static string Describe(object e) => e.GetType().Name + " " + JsonSerializer.Serialize(e, e.GetType(), Json);

    private static string List(IEnumerable<string> items)
    {
        var lines = items.Select(i => "  " + i).ToList();
        return lines.Count == 0 ? "  (none)" : string.Join(Environment.NewLine, lines);
    }
}

/// <summary>A Given/When/Then check failed.</summary>
public sealed class DeciderAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was expected and what happened.</param>
    /// <param name="innerException">The exception the decision threw, if any.</param>
    public DeciderAssertionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
