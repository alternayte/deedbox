using GreenDonut;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Publishing;

// begin-snippet: graphql-query
// The root fields. The list returns summaries; the fields below load the rest only when a client asks for it.
public sealed class ManuscriptQuery
{
    public Task<ManuscriptPage> Manuscripts(ManuscriptQueries q, string? search, Status? status, string? after, int limit = 20, CancellationToken ct = default) =>
        q.List(search, status, after, Math.Clamp(limit, 1, 100), ct);

    public Task<ManuscriptView?> Manuscript(string id, ManuscriptQueries q, CancellationToken ct) => q.Manuscript(id, ct);

    public Task<VersionView?> Version(string id, int number, ManuscriptQueries q, CancellationToken ct) => q.Version(id, number, ct);

    public Task<List<SectionChange>> Changes(string id, int from, int to, ManuscriptQueries q, CancellationToken ct) => q.Changes(id, from, to, ct);
}
// end-snippet

// begin-snippet: graphql-fields
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
// end-snippet

// begin-snippet: graphql-dataloaders
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
// end-snippet

public static class GraphQLSetup
{
    public static void Register(WebApplicationBuilder builder)
    {
        // begin-snippet: graphql-register
        builder.Services.AddGraphQLServer()
            .AddQueryType<ManuscriptQuery>()
            .AddTypeExtension<ManuscriptSummaryFields>()
            .AddDataLoader<DocumentByIdDataLoader>()
            .AddDataLoader<VersionsByManuscriptIdDataLoader>();
        // end-snippet
    }

    public static void Map(WebApplication app)
    {
        // begin-snippet: graphql-map
        app.MapGraphQL();   // POST /graphql
        // end-snippet
    }
}
