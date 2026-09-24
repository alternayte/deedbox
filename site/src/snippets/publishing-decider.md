<!-- snippet: publishing-decider -->
```cs
public static class Editorial
{
    public static IEnumerable<object> Start(Manuscript m, string title)
    {
        if (m.Title is not null)
            throw new InvalidOperationException("The manuscript exists already.");
        yield return new ManuscriptStarted(title);
    }

    public static IEnumerable<object> AddAuthor(Manuscript m, string personId, string name, string? orcid, string affiliation, bool corresponding = false)
    {
        if (m.Status is not (Status.Draft or Status.InRevision))
            throw new InvalidOperationException($"Authors cannot change while the manuscript is {m.Status}.");
        if (m.Authors.Contains(personId))
            throw new InvalidOperationException($"{personId} is an author already.");
        yield return new AuthorAdded(personId, name, orcid, affiliation, corresponding);
    }

    // Authors edit the working copy before submission and during a revision, never while reviewers read it.
    public static IEnumerable<object> Revise(Manuscript m, string sectionId, string heading, string contentHash)
    {
        if (m.Status is not (Status.Draft or Status.InRevision))
            throw new InvalidOperationException($"Sections cannot change while the manuscript is {m.Status}.");
        yield return new SectionRevised(sectionId, heading, contentHash);
    }

    // Submitting freezes the working copy, and pins the new review round to exactly that version.
    public static IEnumerable<object> Submit(Manuscript m)
    {
        if (m.Status is not (Status.Draft or Status.InRevision) || m.Draft.IsEmpty)
            throw new InvalidOperationException($"A {m.Status} manuscript with {m.Draft.Count} sections cannot be submitted.");
        var version = Freeze(m, Stage.SubmittedUnderReview, m.Draft, m.Round == 0 ? null : $"Response to round {m.Round}");
        yield return version;
        yield return new ReviewRoundOpened(m.Round + 1, version.Number);
    }

    // Acceptance freezes the reviewed content again, as the Accepted Manuscript.
    public static IEnumerable<object> Decide(Manuscript m, Decision decision)
    {
        if (m.Status is not Status.UnderReview)
            throw new InvalidOperationException("Only a manuscript under review gets a decision.");
        yield return new DecisionMade(m.Round, decision);
        if (decision is Decision.Accept)
            yield return Freeze(m, Stage.AcceptedManuscript, m.Frozen, null);
    }

    // An editor fixes an accepted manuscript without a new review round: a new version at the same stage, with a reason.
    public static IEnumerable<object> EditorialChange(Manuscript m, string sectionId, string heading, string contentHash, string reason)
    {
        if (m.Status is not Status.Accepted)
            throw new InvalidOperationException("Changes without review apply to accepted manuscripts only.");
        yield return new SectionRevised(sectionId, heading, contentHash);
        yield return Freeze(m, Stage.AcceptedManuscript, Manuscript.Put(m.Frozen, new(sectionId, heading, contentHash)), reason);
    }

    public static IEnumerable<object> Publish(Manuscript m, string doi)
    {
        if (m.Status is not Status.Accepted)
            throw new InvalidOperationException("Only an accepted manuscript is published.");
        var record = Freeze(m, Stage.VersionOfRecord, m.Frozen, null);
        yield return record;
        yield return new Published(record.Number, doi);
    }

    // Crossref: the article keeps its DOI, the notice gets its own, and the published version never changes.
    public static IEnumerable<object> Correct(Manuscript m, string sectionId, string heading, string contentHash, string noticeDoi)
    {
        if (m.Status is not Status.Published)
            throw new InvalidOperationException("Only a published article is corrected.");
        var corrected = Freeze(m, Stage.CorrectedVersionOfRecord, Manuscript.Put(m.Frozen, new(sectionId, heading, contentHash)), "Correction");
        yield return new SectionRevised(sectionId, heading, contentHash);
        yield return corrected;
        yield return new UpdateIssued(UpdateType.Correction, noticeDoi, corrected.Number);
    }

    // NISO CREC: a retracted article stays readable and marked. No version is deleted.
    public static IEnumerable<object> Retract(Manuscript m, string noticeDoi)
    {
        if (m.Status is not Status.Published)
            throw new InvalidOperationException("Only a published article is retracted.");
        yield return new UpdateIssued(UpdateType.Retraction, noticeDoi, null);
    }

    private static VersionFrozen Freeze(Manuscript m, Stage stage, IReadOnlyList<SectionRef> sections, string? reason) =>
        new(m.LatestVersion + 1, stage, sections, m.LatestVersion == 0 ? null : m.LatestVersion, reason);
}
```
<!-- endSnippet -->
