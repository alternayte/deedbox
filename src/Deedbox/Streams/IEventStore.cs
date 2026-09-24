using System.Data.Common;

namespace Deedbox;

/// <summary>Loads streams and appends events. Resolve it from DI; it is scoped.</summary>
public interface IEventStore
{
    /// <summary>
    /// Loads a stream's state and version. A stream with no events returns its initial state and version 0.
    /// </summary>
    /// <param name="streamId">The stream ID: 1 to 200 characters, no leading or trailing white space.</param>
    /// <param name="ct">Cancels the load.</param>
    /// <typeparam name="TState">The stream's registered state type.</typeparam>
    Task<LoadResult<TState>> Load<TState>(string streamId, CancellationToken ct = default) where TState : IState<TState>;

    /// <summary>
    /// Appends events to one stream if it is at the expected version. Every event must belong to the same stream type.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="expected">The version the stream must be at.</param>
    /// <param name="events">One or more events.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <exception cref="ConcurrencyException">The stream is at another version.</exception>
    Task<AppendResult> Append(string streamId, ExpectedVersion expected, IEnumerable<object> events, CancellationToken ct = default);

    /// <summary>
    /// Loads a stream, runs <paramref name="decide"/> on its state, and appends the events it returns, in one
    /// transaction. The stream row stays locked from load to commit. A conflict when two writers create the same
    /// stream reruns the decision, up to the configured retry count.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="decide">A pure function from the current state to new events; return none to append nothing.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <typeparam name="TState">The stream's registered state type.</typeparam>
    Task<ExecuteResult<TState>> Execute<TState>(string streamId, Func<TState, IEnumerable<object>> decide, CancellationToken ct = default)
        where TState : IState<TState>;

    /// <summary>
    /// A store that runs every operation in <paramref name="transaction"/>, for Dapper and plain ADO.NET.
    /// Deedbox never commits or rolls back that transaction; the caller does.
    /// </summary>
    /// <param name="transaction">An open transaction on the Deedbox database.</param>
    IEventStore UseTransaction(DbTransaction transaction);
}
