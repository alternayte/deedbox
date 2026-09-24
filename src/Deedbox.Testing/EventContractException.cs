namespace Deedbox.Testing;

/// <summary>An event contract check failed. The message lists every problem and its fix.</summary>
public sealed class EventContractException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Every problem and its fix.</param>
    public EventContractException(string message)
        : base(message)
    {
    }
}
