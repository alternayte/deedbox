<!-- snippet: publishing-state -->
```cs
// The state holds only what decisions need. The version history lives in the read model.
public record Manuscript(
    string? Title,
    Status Status,
    ImmutableList<SectionRef> Draft,   // the working copy that authors edit
    ImmutableList<SectionRef> Frozen,  // the sections of the latest version
    int LatestVersion,
    int Round) : IState<Manuscript>
{
    public static Manuscript Initial { get; } = new(null, Status.Draft, [], [], 0, 0);

    public static Manuscript Evolve(Manuscript m, object e) => e switch
    {
        ManuscriptStarted s => m with { Title = s.Title },
        SectionRevised r => m with { Draft = Put(m.Draft, new(r.SectionId, r.Heading, r.ContentHash)) },
        VersionFrozen v => m with { Frozen = [.. v.Sections], LatestVersion = v.Number },
        ReviewRoundOpened o => m with { Status = Status.UnderReview, Round = o.Round },
        DecisionMade { Decision: Decision.Accept } => m with { Status = Status.Accepted },
        DecisionMade { Decision: Decision.Reject } => m with { Status = Status.Rejected },
        DecisionMade => m with { Status = Status.InRevision },
        Published => m with { Status = Status.Published },
        UpdateIssued { Type: UpdateType.Retraction } => m with { Status = Status.Retracted },
        _ => m,
    };

    public static ImmutableList<SectionRef> Put(ImmutableList<SectionRef> sections, SectionRef section) =>
        sections.FindIndex(s => s.SectionId == section.SectionId) is var i and >= 0 ? sections.SetItem(i, section) : sections.Add(section);
}
```
<!-- endSnippet -->
