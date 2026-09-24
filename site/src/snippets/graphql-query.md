<!-- snippet: graphql-query -->
```cs
// The root fields. The list returns summaries; the fields below load the rest only when a client asks for it.
public sealed class ManuscriptQuery
{
    public Task<ManuscriptPage> Manuscripts(ManuscriptQueries q, string? search, Status? status, string? after, int limit = 20, CancellationToken ct = default) =>
        q.List(search, status, after, Math.Clamp(limit, 1, 100), ct);

    public Task<ManuscriptView?> Manuscript(string id, ManuscriptQueries q, CancellationToken ct) => q.Manuscript(id, ct);

    public Task<VersionView?> Version(string id, int number, ManuscriptQueries q, CancellationToken ct) => q.Version(id, number, ct);

    public Task<List<SectionChange>> Changes(string id, int from, int to, ManuscriptQueries q, CancellationToken ct) => q.Changes(id, from, to, ct);
}
```
<!-- endSnippet -->
