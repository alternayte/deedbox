namespace Deedbox;

/// <summary>Where a projection runs. Each projection has exactly one run mode.</summary>
public enum Run
{
    /// <summary>In the append's transaction, so its writes commit with the events.</summary>
    Inline,

    /// <summary>In the background runner, after the events commit, with a checkpoint in the same transaction as its writes.</summary>
    Async,
}
