using System.Globalization;

namespace Deedbox;

/// <summary>The stream version an append expects, checked atomically with the write.</summary>
public readonly record struct ExpectedVersion
{
    private const long AnyValue = -1;

    private ExpectedVersion(long value)
    {
        Value = value;
    }

    /// <summary>Appends whatever the stream's version is, and creates the stream if it does not exist.</summary>
    public static ExpectedVersion Any { get; } = new(AnyValue);

    /// <summary>Appends only when the stream does not exist yet. The same as <c>Exact(0)</c>.</summary>
    public static ExpectedVersion NoStream { get; } = new(0);

    internal long Value { get; }

    internal bool IsAny => Value == AnyValue;

    /// <summary>Appends only when the stream is at <paramref name="version"/>. Version 0 means the stream does not exist.</summary>
    /// <param name="version">The version <c>Load</c> returned.</param>
    public static ExpectedVersion Exact(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return new(version);
    }

    internal bool Matches(long actual) => IsAny || Value == actual;

    /// <summary>Any, NoStream or Exact(n).</summary>
    public override string ToString() => Value switch
    {
        AnyValue => "Any",
        0 => "NoStream",
        _ => "Exact(" + Value.ToString(CultureInfo.InvariantCulture) + ")",
    };
}
