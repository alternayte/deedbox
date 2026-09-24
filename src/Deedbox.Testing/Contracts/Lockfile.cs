using System.Globalization;
using System.Text;

namespace Deedbox.Testing.Contracts;

/// <summary>The event contracts of every registered stream, as the lockfile records them.</summary>
internal sealed record Lockfile(IReadOnlyList<LockedStream> Streams)
{
    public const string Header = "# Deedbox event contracts. EventContracts.Verify writes this file; commit it.";

    public static Lockfile From(EventRegistry registry, DeedboxJson json) => new(
        registry.Streams
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new LockedStream(s.Name, s.StateType.Name, s.Events
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new LockedEvent(e.Name, e.Version, WithPersonalData(Shape.Of(e.ClrType, json.Options), e), [.. e.Aliases.Order(StringComparer.Ordinal)]))
                .ToList()))
            .ToList());

    public IEnumerable<LockedEvent> Events => Streams.SelectMany(s => s.Events);

    private static Shape WithPersonalData(Shape shape, EventRegistration registration)
    {
        if (registration.PersonalFields.Count == 0)
            return shape;
        var subjects = registration.PersonalFields.ToDictionary(f => f.JsonName, f => f.SubjectJsonName, StringComparer.Ordinal);
        return shape with
        {
            Members = shape.Members
                .Select(m => subjects.TryGetValue(m.Name, out var subject) ? (m.Name, m.Shape with { PersonalSubject = subject }) : m)
                .ToList(),
        };
    }

    public override string ToString()
    {
        var text = new StringBuilder().Append(Header).Append('\n');
        foreach (var stream in Streams)
        {
            text.Append('\n').Append("stream ").Append(stream.Name).Append(" (").Append(stream.StateType).Append(")\n");
            foreach (var e in stream.Events)
            {
                text.Append("  ").Append(e.Name).Append(" v").Append(e.Version.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Shape).Append('\n');
                foreach (var alias in e.Aliases)
                    text.Append("    alias ").Append(alias).Append('\n');
            }
        }

        return text.ToString();
    }

    public static Lockfile Parse(string text)
    {
        var streams = new List<LockedStream>();
        LockedStream? stream = null;
        LockedEvent? last = null;
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            try
            {
                if (line.StartsWith("stream ", StringComparison.Ordinal))
                {
                    var open = line.IndexOf(" (", StringComparison.Ordinal);
                    stream = new LockedStream(line[7..open], line[(open + 2)..^1], new List<LockedEvent>());
                    streams.Add(stream);
                }
                else if (line.StartsWith("    alias ", StringComparison.Ordinal))
                {
                    ((List<string>)last!.Aliases).Add(line[10..]);
                }
                else if (line.StartsWith("  ", StringComparison.Ordinal))
                {
                    var parts = line.Trim().Split(' ', 3);
                    last = new LockedEvent(parts[0], int.Parse(parts[1][1..], CultureInfo.InvariantCulture), Shape.Parse(parts[2]), new List<string>());
                    ((List<LockedEvent>)stream!.Events).Add(last);
                }
                else
                {
                    throw new FormatException("unknown line");
                }
            }
            catch (Exception ex) when (ex is FormatException or NullReferenceException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new EventContractException($"The lockfile cannot be read at line {number}: '{line}'. {ex.Message}");
            }
        }

        return new Lockfile(streams);
    }
}

internal sealed record LockedStream(string Name, string StateType, IReadOnlyList<LockedEvent> Events);

internal sealed record LockedEvent(string Name, int Version, Shape Shape, IReadOnlyList<string> Aliases);

/// <summary>Finds changes that make stored events unreadable or wrong.</summary>
internal static class Compatibility
{
    public static List<string> Breaks(Lockfile locked, Lockfile current)
    {
        var breaks = new List<string>();
        var byName = new Dictionary<string, LockedEvent>(StringComparer.Ordinal);
        foreach (var e in current.Events)
        {
            foreach (var name in e.Aliases.Prepend(e.Name))
                byName[name] = e;
        }

        foreach (var old in locked.Events)
        {
            foreach (var name in old.Aliases.Prepend(old.Name))
            {
                if (!byName.ContainsKey(name))
                    breaks.Add($"'{name}' was removed. Stored events with this name become unreadable; add .Alias(\"{name}\") to the event that replaces it.");
            }

            if (!byName.TryGetValue(old.Name, out var now))
                continue;

            // Personal-data markers must survive any version change, not only a same-version edit.
            foreach (var (member, _) in old.Shape.Members.Where(m => m.Shape.PersonalSubject is not null))
            {
                var shapeNow = now.Shape.Members.FirstOrDefault(m => m.Name == member).Shape;
                if (shapeNow is not null && shapeNow.PersonalSubject is null)
                    breaks.Add($"'{old.Name}': '{member}' is no longer [PersonalData]. New events would store it in plain text, and erasure would miss it.");
            }

            if (now.Version < old.Version)
            {
                breaks.Add($"'{old.Name}' went from v{old.Version} back to v{now.Version}. Event versions only go up.");
            }
            else if (now.Version == old.Version)
            {
                var problems = new List<string>();
                Compare(old.Shape, now.Shape, "", problems);
                foreach (var problem in problems)
                {
                    breaks.Add(problem.Contains("[PersonalData]", StringComparison.Ordinal) || problem.Contains("subject", StringComparison.Ordinal)
                        ? $"'{old.Name}' v{old.Version}: {problem}"
                        : $"'{old.Name}' v{old.Version}: {problem} Stored events no longer read correctly. " +
                          $"Register it as version {old.Version + 1} with an upcaster from version {old.Version}.");
                }
            }
        }

        return breaks;
    }

    private static void Compare(Shape old, Shape now, string path, List<string> problems)
    {
        var at = path.Length == 0 ? "the event" : $"'{path}'";
        if (old.Kind != now.Kind)
        {
            problems.Add($"{at} changed from {old.WithNullable(false)} to {now.WithNullable(false)}.");
            return;
        }

        if (old.Nullable && !now.Nullable)
            problems.Add($"{at} is no longer nullable, but stored events may hold null.");

        if (old.PersonalSubject is not null && now.PersonalSubject is not null && old.PersonalSubject != now.PersonalSubject)
            problems.Add($"{at} changed its subject from '{old.PersonalSubject}' to '{now.PersonalSubject}'; erasing the old subject would miss new events.");

        switch (old.Kind)
        {
            case Shape.Object:
                var members = now.Members.ToDictionary(m => m.Name, m => m.Shape, StringComparer.Ordinal);
                foreach (var (name, shape) in old.Members)
                {
                    var child = path.Length == 0 ? name : path + "." + name;
                    if (members.TryGetValue(name, out var nowShape))
                        Compare(shape, nowShape, child, problems);
                    else
                        problems.Add($"'{child}' was removed, so its stored values are dropped.");
                }

                var oldNames = old.Members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var (name, shape) in now.Members)
                {
                    if (!oldNames.Contains(name) && !shape.Nullable)
                        problems.Add($"'{(path.Length == 0 ? name : path + "." + name)}' was added as non-nullable, but stored events do not have it.");
                }

                break;
            case Shape.Array or Shape.Map:
                Compare(old.Element!, now.Element!, path + "[]", problems);
                break;
            case Shape.Enum:
                var values = now.Members.ToDictionary(m => m.Name, m => m.Shape.Kind, StringComparer.Ordinal);
                foreach (var (name, value) in old.Members)
                {
                    if (!values.TryGetValue(name, out var nowValue) || nowValue != value.Kind)
                        problems.Add($"{at} member {name}={value.Kind} was removed or renumbered.");
                }

                break;
        }
    }
}
