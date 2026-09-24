<!-- snippet: publishing-views -->
```cs
// The shapes the API returns. ManuscriptView is also stored whole, as one JSON document per manuscript.
public record ManuscriptView(
    string Id, string Title, Status Status, string? Doi,
    IReadOnlyList<AuthorView> Authors,
    VersionView? Latest,      // the newest version, for authors and editors
    VersionView? Published,   // the version readers see: the Version of Record, or its latest correction
    IReadOnlyList<RoundView> Rounds,
    IReadOnlyList<UpdateView> Updates);

public record ManuscriptSummary(string Id, string Title, Status Status, int? LatestVersion, int? PublishedVersion, string? Doi, DateTimeOffset UpdatedAt);

public record ManuscriptPage(IReadOnlyList<ManuscriptSummary> Items, string? Next);

public record VersionView(int Number, Stage Stage, int? BasedOn, string? Reason, DateTimeOffset FrozenAt, IReadOnlyList<SectionView> Sections);

public record VersionSummary(string ManuscriptId, int Number, Stage Stage, int? BasedOn, string? Reason, DateTimeOffset FrozenAt);

public record SectionView(string SectionId, string Heading, string ContentUrl);

public record SectionChange(string SectionId, string Heading, string Change);  // added, changed or removed

public record AuthorView(string AuthorId, string? Name, string Affiliation);

public record RoundView(int Round, int Version, Decision? Decision);

public record UpdateView(UpdateType Type, string NoticeDoi, int? Version, DateTimeOffset IssuedAt);
```
<!-- endSnippet -->
