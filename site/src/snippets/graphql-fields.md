<!-- snippet: graphql-fields -->
```cs
// Fields on each manuscript in a list. Every field asks a DataLoader, so a page of 20 costs one query per loader, not 20.
[ExtendObjectType(typeof(ManuscriptSummary))]
public sealed class ManuscriptSummaryFields
{
    public async Task<IReadOnlyList<AuthorView>> GetAuthors([Parent] ManuscriptSummary m, DocumentByIdDataLoader documents, CancellationToken ct) =>
        (await documents.LoadRequiredAsync(m.Id, ct)).Authors;

    public async Task<VersionView?> GetLatest([Parent] ManuscriptSummary m, DocumentByIdDataLoader documents, CancellationToken ct) =>
        (await documents.LoadRequiredAsync(m.Id, ct)).Latest;

    public async Task<VersionView?> GetPublished([Parent] ManuscriptSummary m, DocumentByIdDataLoader documents, CancellationToken ct) =>
        (await documents.LoadRequiredAsync(m.Id, ct)).Published;

    public async Task<IReadOnlyList<RoundView>> GetRounds([Parent] ManuscriptSummary m, DocumentByIdDataLoader documents, CancellationToken ct) =>
        (await documents.LoadRequiredAsync(m.Id, ct)).Rounds;

    public async Task<VersionSummary[]> GetVersions([Parent] ManuscriptSummary m, VersionsByManuscriptIdDataLoader versions, CancellationToken ct) =>
        await versions.LoadRequiredAsync(m.Id, ct);
}
```
<!-- endSnippet -->
