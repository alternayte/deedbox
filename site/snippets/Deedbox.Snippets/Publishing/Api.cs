using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Publishing;

// begin-snippet: publishing-views
public record ManuscriptView(
    string Id, string Title, Status Status, string? Doi,
    IReadOnlyList<AuthorView> Authors,
    VersionView? Latest,      // the newest version, for authors and editors
    VersionView? Published,   // the version readers see, for the public site
    IReadOnlyList<RoundView> Rounds,
    IReadOnlyList<UpdateView> Updates);

public record VersionView(int Number, Stage Stage, int? BasedOn, string? Reason, DateTimeOffset FrozenAt, IReadOnlyList<SectionView> Sections);

public record VersionSummary(int Number, Stage Stage, int? BasedOn, string? Reason, DateTimeOffset FrozenAt);

public record SectionView(string SectionId, string Heading, string ContentUrl);

public record SectionChange(string SectionId, string Heading, string Change);  // added, changed or removed

public record AuthorView(string AuthorId, string? Name, string Affiliation);

public record RoundView(int Round, int Version, Decision? Decision);

public record UpdateView(UpdateType Type, string NoticeDoi, int? Version, DateTimeOffset IssuedAt);
// end-snippet

// begin-snippet: publishing-queries
// Every read goes through here, so REST and GraphQL return the same shapes.
public sealed class ManuscriptQueries(PublishingDb db)
{
    public async Task<ManuscriptView?> Manuscript(string id, CancellationToken ct = default)
    {
        if (await db.Manuscripts.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct) is not { } m)
            return null;

        var authors = await db.Authors.AsNoTracking().Where(a => a.ManuscriptId == id).OrderBy(a => a.AuthorId)
            .Select(a => new AuthorView(a.AuthorId, a.Name, a.Affiliation)).ToListAsync(ct);
        var rounds = await db.Rounds.AsNoTracking().Where(r => r.ManuscriptId == id).OrderBy(r => r.Round)
            .Select(r => new RoundView(r.Round, r.Version, r.Decision)).ToListAsync(ct);
        var updates = await db.Updates.AsNoTracking().Where(u => u.ManuscriptId == id).OrderBy(u => u.IssuedAt)
            .Select(u => new UpdateView(u.Type, u.NoticeDoi, u.Version, u.IssuedAt)).ToListAsync(ct);
        var latest = m.LatestVersion is { } l ? await Version(id, l, ct) : null;
        var published = m.PublishedVersion is { } p ? (p == m.LatestVersion ? latest : await Version(id, p, ct)) : null;
        return new ManuscriptView(m.Id, m.Title, m.Status, m.Doi, authors, latest, published, rounds, updates);
    }

    public Task<List<VersionSummary>> Versions(string id, CancellationToken ct = default) =>
        db.Versions.AsNoTracking().Where(v => v.ManuscriptId == id).OrderBy(v => v.Number)
            .Select(v => new VersionSummary(v.Number, v.Stage, v.BasedOn, v.Reason, v.FrozenAt)).ToListAsync(ct);

    public async Task<VersionView?> Version(string id, int number, CancellationToken ct = default)
    {
        if (await db.Versions.AsNoTracking().SingleOrDefaultAsync(v => v.ManuscriptId == id && v.Number == number, ct) is not { } v)
            return null;
        var sections = await Sections(id, number, ct);
        return new VersionView(v.Number, v.Stage, v.BasedOn, v.Reason, v.FrozenAt,
            [.. sections.Select(s => new SectionView(s.SectionId, s.Heading, $"/content/{s.ContentHash}"))]);
    }

    // What changed between two versions, section by section, from the content hashes.
    public async Task<List<SectionChange>> Changes(string id, int from, int to, CancellationToken ct = default)
    {
        var before = (await Sections(id, from, ct)).ToDictionary(s => s.SectionId);
        var after = await Sections(id, to, ct);
        var changes = new List<SectionChange>();
        foreach (var s in after)
        {
            if (!before.Remove(s.SectionId, out var old))
                changes.Add(new(s.SectionId, s.Heading, "added"));
            else if (old.ContentHash != s.ContentHash || old.Heading != s.Heading)
                changes.Add(new(s.SectionId, s.Heading, "changed"));
        }

        changes.AddRange(before.Values.Select(s => new SectionChange(s.SectionId, s.Heading, "removed")));
        return changes;
    }

    private Task<List<VersionSectionRow>> Sections(string id, int version, CancellationToken ct) =>
        db.VersionSections.AsNoTracking().Where(s => s.ManuscriptId == id && s.Version == version).OrderBy(s => s.Position).ToListAsync(ct);
}
// end-snippet

// begin-snippet: publishing-rest
public static class ManuscriptEndpoints
{
    public static void MapManuscripts(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/manuscripts");

        // Reads come from the projection.
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

// begin-snippet: publishing-graphql
// Hot Chocolate: the same queries, as GraphQL fields.
public sealed class ManuscriptQuery
{
    public Task<ManuscriptView?> Manuscript(string id, ManuscriptQueries q, CancellationToken ct) => q.Manuscript(id, ct);

    public Task<List<VersionSummary>> Versions(string id, ManuscriptQueries q, CancellationToken ct) => q.Versions(id, ct);

    public Task<VersionView?> Version(string id, int number, ManuscriptQueries q, CancellationToken ct) => q.Version(id, number, ct);

    public Task<List<SectionChange>> Changes(string id, int from, int to, ManuscriptQueries q, CancellationToken ct) => q.Changes(id, from, to, ct);
}
// end-snippet

public static class PublishingApp
{
    public static void Register(WebApplicationBuilder builder, string connStr)
    {
        // begin-snippet: publishing-register
        builder.Services.AddDbContext<PublishingDb>(o => o.UseNpgsql(connStr));
        builder.Services.AddScoped<ManuscriptQueries>();
        builder.Services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .ApplySchemaOnStartup()
            .Keys(keys => keys.StoreInDatabase())  // AuthorAdded holds personal data
            .Stream<Manuscript>("manuscript", s => s
                .Events<ManuscriptStarted, SectionRevised, AuthorAdded, VersionFrozen, ReviewRoundOpened, DecisionMade>()
                .Events<Published, UpdateIssued>())
            .Projection<ManuscriptProjection>("manuscripts", Run.Inline));
        builder.Services.AddGraphQLServer().AddQueryType<ManuscriptQuery>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));  // "Published", not 5
        // end-snippet
    }

    public static void Map(WebApplication app)
    {
        // begin-snippet: publishing-map
        app.MapManuscripts();
        app.MapGraphQL();   // POST /graphql
        // end-snippet
    }
}
