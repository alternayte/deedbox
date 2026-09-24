using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Publishing;

// begin-snippet: publishing-queries
// Every read goes through here. Each method opens its own context, so parallel callers such as GraphQL resolvers are safe.
public sealed class ManuscriptQueries(IDbContextFactory<PublishingDb> contexts)
{
    // The list: newest change first, with keyset paging and a case-insensitive title search.
    public async Task<ManuscriptPage> List(string? search, Status? status, string? after, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.Manuscripts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(m => EF.Functions.ILike(m.Title, $"%{Escape(search)}%", @"\"));
        if (status is not null)
            query = query.Where(m => m.Status == status);
        if (Cursor.Read(after) is var (updatedAt, id))
            query = query.Where(m => m.UpdatedAt < updatedAt || (m.UpdatedAt == updatedAt && string.Compare(m.Id, id) > 0));

        var rows = await query.OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id).Take(limit + 1)
            .Select(m => new ManuscriptSummary(m.Id, m.Title, m.Status, m.LatestVersion, m.PublishedVersion, m.Doi, m.UpdatedAt))
            .ToListAsync(ct);
        var next = rows.Count > limit ? Cursor.Write(rows[limit - 1].UpdatedAt, rows[limit - 1].Id) : null;
        return new ManuscriptPage(rows.Take(limit).ToList(), next);
    }

    // The detail page: one primary-key read of the stored document.
    public async Task<ManuscriptView?> Manuscript(string id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var document = await db.Manuscripts.Where(m => m.Id == id).Select(m => m.Document).SingleOrDefaultAsync(ct);
        return document is null ? null : Documents.Read(document);
    }

    public async Task<List<VersionSummary>> Versions(string id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Versions.AsNoTracking().Where(v => v.ManuscriptId == id).OrderBy(v => v.Number)
            .Select(v => new VersionSummary(v.ManuscriptId, v.Number, v.Stage, v.BasedOn, v.Reason, v.FrozenAt)).ToListAsync(ct);
    }

    // One version with its sections, in one query.
    public async Task<VersionView?> Version(string id, int number, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Versions.AsNoTracking().Where(v => v.ManuscriptId == id && v.Number == number)
            .Select(v => new VersionView(v.Number, v.Stage, v.BasedOn, v.Reason, v.FrozenAt,
                db.VersionSections.Where(s => s.ManuscriptId == id && s.Version == number).OrderBy(s => s.Position)
                    .Select(s => new SectionView(s.SectionId, s.Heading, "/content/" + s.ContentHash)).ToList()))
            .SingleOrDefaultAsync(ct);
    }

    // What changed between two versions, section by section, from the content hashes. One query reads both versions.
    public async Task<List<SectionChange>> Changes(string id, int from, int to, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sections = await db.VersionSections.AsNoTracking()
            .Where(s => s.ManuscriptId == id && (s.Version == from || s.Version == to)).OrderBy(s => s.Position).ToListAsync(ct);
        var before = sections.Where(s => s.Version == from).ToDictionary(s => s.SectionId);
        var changes = new List<SectionChange>();
        foreach (var s in sections.Where(s => s.Version == to))
        {
            if (!before.Remove(s.SectionId, out var old))
                changes.Add(new(s.SectionId, s.Heading, "added"));
            else if (old.ContentHash != s.ContentHash || old.Heading != s.Heading)
                changes.Add(new(s.SectionId, s.Heading, "changed"));
        }

        changes.AddRange(before.Values.Select(s => new SectionChange(s.SectionId, s.Heading, "removed")));
        return changes;
    }

    private static string Escape(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

// An opaque cursor: the sort values of the last row on the page.
public static class Cursor
{
    public static string Write(DateTimeOffset updatedAt, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedAt.UtcTicks}:{id}"));

    public static (DateTimeOffset UpdatedAt, string Id)? Read(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var colon = text.IndexOf(':');
        return (new DateTimeOffset(long.Parse(text[..colon]), TimeSpan.Zero), text[(colon + 1)..]);
    }
}
// end-snippet

// begin-snippet: publishing-rest
public static class ManuscriptEndpoints
{
    public static void MapManuscripts(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/manuscripts");

        // Reads come from the projection.
        api.MapGet("/", async (string? search, Status? status, string? after, int? limit, ManuscriptQueries q, CancellationToken ct) =>
            Results.Ok(await q.List(search, status, after, Math.Clamp(limit ?? 20, 1, 100), ct)));
        api.MapGet("/{id}", async (string id, ManuscriptQueries q, CancellationToken ct) =>
            await q.Manuscript(id, ct) is { } m ? Results.Ok(m) : Results.NotFound());
        api.MapGet("/{id}/versions", async (string id, ManuscriptQueries q, CancellationToken ct) =>
            Results.Ok(await q.Versions(id, ct)));
        api.MapGet("/{id}/versions/{number:int}", async (string id, int number, ManuscriptQueries q, CancellationToken ct) =>
            await q.Version(id, number, ct) is { } v ? Results.Ok(v) : Results.NotFound());
        api.MapGet("/{id}/versions/{number:int}/changes", async (string id, int number, int from, ManuscriptQueries q, CancellationToken ct) =>
            Results.Ok(await q.Changes(id, from, number, ct)));

        // Commands go through the stream. A rule the decider enforces becomes 409 Conflict.
        api.MapPost("/{id}/submit", (string id, IEventStore store, CancellationToken ct) =>
            Run(() => store.Execute<Manuscript>(id, Editorial.Submit, ct)));
        api.MapPost("/{id}/decision", (string id, Decision decision, IEventStore store, CancellationToken ct) =>
            Run(() => store.Execute<Manuscript>(id, m => Editorial.Decide(m, decision), ct)));
    }

    private static async Task<IResult> Run(Func<Task<ExecuteResult<Manuscript>>> command)
    {
        try
        {
            var result = await command();
            return Results.Ok(new { result.Version });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
// end-snippet

public static class PublishingApp
{
    public static void Register(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: publishing-register
        builder.Services.AddDbContextFactory<PublishingDb>(o => o.UseNpgsql(connStr));  // also registers PublishingDb as scoped
        builder.Services.AddSingleton<ManuscriptQueries>();
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .ApplySchemaOnStartup()
            .Keys(keys => keys.StoreInDatabase())  // AuthorAdded holds personal data
            .Stream<Manuscript>("manuscript", s => s
                .Events<ManuscriptStarted, SectionRevised, AuthorAdded, VersionFrozen, ReviewRoundOpened, DecisionMade>()
                .Events<Published, UpdateIssued>())
            .Projection<ManuscriptProjection>("manuscripts", Run.Inline));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));  // "Published", not 5
        // end-snippet
        GraphQLSetup.Register(builder);
    }

    public static void Map(WebApplication app)
    {
        // begin-snippet: publishing-map
        app.MapManuscripts();
        // end-snippet
        GraphQLSetup.Map(app);
    }
}
