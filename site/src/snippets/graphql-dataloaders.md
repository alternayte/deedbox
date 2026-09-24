<!-- snippet: graphql-dataloaders -->
```cs
// Each loader collects the keys of one request's resolvers, then reads them all in one query.
public sealed class DocumentByIdDataLoader(IDbContextFactory<PublishingDb> contexts, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<string, ManuscriptView>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<string, ManuscriptView>> LoadBatchAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Manuscripts.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Document }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => Documents.Read(r.Document));
    }
}

public sealed class VersionsByManuscriptIdDataLoader(IDbContextFactory<PublishingDb> contexts, IBatchScheduler scheduler, DataLoaderOptions options)
    : GroupedDataLoader<string, VersionSummary>(scheduler, options)
{
    protected override async Task<ILookup<string, VersionSummary>> LoadGroupedBatchAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var versions = await db.Versions.AsNoTracking().Where(v => ids.Contains(v.ManuscriptId)).OrderBy(v => v.Number)
            .Select(v => new VersionSummary(v.ManuscriptId, v.Number, v.Stage, v.BasedOn, v.Reason, v.FrozenAt)).ToListAsync(ct);
        return versions.ToLookup(v => v.ManuscriptId);
    }
}
```
<!-- endSnippet -->
