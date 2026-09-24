<!-- snippet: publishing-events -->
```cs
// A section's text lives in blob storage; events hold its content hash.
public record SectionRef(string SectionId, string Heading, string ContentHash);

public record ManuscriptStarted(string Title);

public record SectionRevised(string SectionId, string Heading, string ContentHash);

// Flat on purpose: Deedbox encrypts top-level [PersonalData] properties only.
public record AuthorAdded(
    [property: DataSubject] string AuthorId,
    [property: PersonalData] string Name,
    string Affiliation);

// A version is a frozen list of sections. Nothing changes it later.
public record VersionFrozen(int Number, Stage Stage, IReadOnlyList<SectionRef> Sections, int? BasedOn, string? Reason);

public record ReviewRoundOpened(int Round, int Version);

public record DecisionMade(int Round, Decision Decision);

public record Published(int Version, string Doi);

public record UpdateIssued(UpdateType Type, string NoticeDoi, int? Version);
```
<!-- endSnippet -->
