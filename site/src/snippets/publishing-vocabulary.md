<!-- snippet: publishing-vocabulary -->
```cs
// Version stages from NISO Journal Article Versions (RP-8-2008).
public enum Stage { SubmittedUnderReview, AcceptedManuscript, VersionOfRecord, CorrectedVersionOfRecord }

public enum Status { Draft, UnderReview, InRevision, Accepted, Rejected, Published, Retracted }

public enum Decision { MinorRevision, MajorRevision, Accept, Reject }

// Crossref Crossmark update types, as NISO CREC uses them.
public enum UpdateType { Correction, ExpressionOfConcern, Retraction }
```
<!-- endSnippet -->
