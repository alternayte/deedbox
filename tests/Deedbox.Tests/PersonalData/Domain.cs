using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Deedbox.Tests.PersonalData;

public record ReviewerInvited(
    string ManuscriptId,
    [property: DataSubject] string ReviewerId,
    [property: PersonalData] string ReviewerName,
    [property: PersonalData] string? ReviewerEmail);

public record CoAuthorAdded(
    string ManuscriptId,
    string AuthorId,
    [property: PersonalData(Subject = "AuthorId")] string AuthorName,
    string ReviewerId,
    [property: PersonalData(Subject = "ReviewerId")] string? Note);

public record Submitted(string Title);

public record Manuscript(ImmutableList<string?> Names, int Events) : IState<Manuscript>
{
    public static Manuscript Initial { get; } = new(ImmutableList<string?>.Empty, 0);

    public static Manuscript Evolve(Manuscript s, object e) => e switch
    {
        ReviewerInvited r => new Manuscript(s.Names.Add(r.ReviewerName), s.Events + 1),
        CoAuthorAdded c => new Manuscript(s.Names.Add(c.AuthorName), s.Events + 1),
        _ => s with { Events = s.Events + 1 },
    };
}

public record BadAge([property: DataSubject] string PersonId, [property: PersonalData] int Age);

public record NoSubject([property: PersonalData] string Name);

public record Age(int Years) : IState<Age>
{
    public static Age Initial { get; } = new(0);

    public static Age Evolve(Age s, object e) => s;
}

public static class Keys
{
    public static string Ring(params string[] versions) =>
        string.Join(',', versions.Select(v => $"{v}:{Convert.ToBase64String(Material.GetOrAdd(v, _ => RandomNumberGenerator.GetBytes(32)))}"));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Material = new();

    public static void Streams(DeedboxBuilder b) => b.Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded, Submitted>());
}
