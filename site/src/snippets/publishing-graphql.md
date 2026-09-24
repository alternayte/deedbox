<!-- snippet: publishing-graphql -->
```cs
// Hot Chocolate: the same queries, as GraphQL fields.
public sealed class ManuscriptQuery
{
    public Task<ManuscriptView?> Manuscript(string id, ManuscriptQueries q, CancellationToken ct) => q.Manuscript(id, ct);

    public Task<List<VersionSummary>> Versions(string id, ManuscriptQueries q, CancellationToken ct) => q.Versions(id, ct);

    public Task<VersionView?> Version(string id, int number, ManuscriptQueries q, CancellationToken ct) => q.Version(id, number, ct);

    public Task<List<SectionChange>> Changes(string id, int from, int to, ManuscriptQueries q, CancellationToken ct) => q.Changes(id, from, to, ct);
}
```
<!-- endSnippet -->
