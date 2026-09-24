using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Publishing;

// begin-snippet: publishing-views
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
// end-snippet

// begin-snippet: publishing-read-model
// One row per manuscript. The columns serve the list; the document serves the detail page in one read.
public class ManuscriptRow
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public Status Status { get; set; }
    public int? LatestVersion { get; set; }
    public int? PublishedVersion { get; set; }
    public string? Doi { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string Document { get; set; }  // ManuscriptView as jsonb
}

// One row per frozen version, and one per section of it: the version history.
public class VersionRow
{
    public required string ManuscriptId { get; set; }
    public int Number { get; set; }
    public Stage Stage { get; set; }
    public int? BasedOn { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset FrozenAt { get; set; }
}

public class VersionSectionRow
{
    public required string ManuscriptId { get; set; }
    public int Version { get; set; }
    public int Position { get; set; }
    public required string SectionId { get; set; }
    public required string Heading { get; set; }
    public required string ContentHash { get; set; }
}

public class PublishingDb(DbContextOptions<PublishingDb> options) : DbContext(options)
{
    public DbSet<ManuscriptRow> Manuscripts => Set<ManuscriptRow>();
    public DbSet<VersionRow> Versions => Set<VersionRow>();
    public DbSet<VersionSectionRow> VersionSections => Set<VersionSectionRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder conventions) =>
        conventions.Properties<Enum>().HaveConversion<string>();  // store enums as their names

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("publishing");
        model.HasPostgresExtension("pg_trgm");
        var manuscripts = model.Entity<ManuscriptRow>().ToTable("manuscripts");
        manuscripts.HasKey(m => m.Id);
        manuscripts.Property(m => m.Document).HasColumnType("jsonb");
        manuscripts.HasIndex(m => new { m.UpdatedAt, m.Id });                                 // keyset paging
        manuscripts.HasIndex(m => m.Title).HasMethod("gin").HasOperators("gin_trgm_ops");  // title search
        model.Entity<VersionRow>().ToTable("versions").HasKey(v => new { v.ManuscriptId, v.Number });
        model.Entity<VersionSectionRow>().ToTable("version_sections").HasKey(s => new { s.ManuscriptId, s.Version, s.Position });
    }
}

public static class Documents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static ManuscriptView Read(string json) => JsonSerializer.Deserialize<ManuscriptView>(json, Json)!;

    public static string Write(ManuscriptView view) => JsonSerializer.Serialize(view, Json);
}
// end-snippet

// begin-snippet: publishing-projection
// Inline, so an API call right after a command reads its own write.
public sealed class ManuscriptProjection : Projection<PublishingDb>
{
    public ManuscriptProjection()
    {
        On<ManuscriptStarted>((e, ctx) =>
        {
            var document = new ManuscriptView(ctx.StreamId, e.Title, Status.Draft, null, [], null, null, [], []);
            ctx.Db.Manuscripts.Add(new ManuscriptRow { Id = ctx.StreamId, Title = e.Title, UpdatedAt = ctx.OccurredAt, Document = Documents.Write(document) });
            return Task.CompletedTask;
        });

        On<AuthorAdded>((e, ctx) => Change(ctx, m => m with { Authors = [.. m.Authors, new AuthorView(e.AuthorId, e.Name, e.Affiliation)] }));

        On<VersionFrozen>(async (e, ctx) =>
        {
            var sections = e.Sections.Select(s => new SectionView(s.SectionId, s.Heading, $"/content/{s.ContentHash}")).ToList();
            await Change(ctx, m => m with { Latest = new VersionView(e.Number, e.Stage, e.BasedOn, e.Reason, ctx.OccurredAt, sections) });

            ctx.Db.Versions.Add(new VersionRow
            {
                ManuscriptId = ctx.StreamId, Number = e.Number, Stage = e.Stage, BasedOn = e.BasedOn, Reason = e.Reason, FrozenAt = ctx.OccurredAt,
            });
            ctx.Db.VersionSections.AddRange(e.Sections.Select((s, i) => new VersionSectionRow
            {
                ManuscriptId = ctx.StreamId, Version = e.Number, Position = i, SectionId = s.SectionId, Heading = s.Heading, ContentHash = s.ContentHash,
            }));
        });

        On<ReviewRoundOpened>((e, ctx) => Change(ctx, m => m with
        {
            Status = Status.UnderReview,
            Rounds = [.. m.Rounds, new RoundView(e.Round, e.Version, null)],
        }));

        On<DecisionMade>((e, ctx) => Change(ctx, m => m with
        {
            Status = e.Decision switch { Decision.Accept => Status.Accepted, Decision.Reject => Status.Rejected, _ => Status.InRevision },
            Rounds = [.. m.Rounds.Select(r => r.Round == e.Round ? r with { Decision = e.Decision } : r)],
        }));

        On<Published>((e, ctx) => Change(ctx, m => m with { Status = Status.Published, Doi = e.Doi, Published = m.Latest }));

        On<UpdateIssued>((e, ctx) => Change(ctx, m => m with
        {
            Status = e.Type is UpdateType.Retraction ? Status.Retracted : m.Status,
            Published = e.Type is UpdateType.Correction ? m.Latest : m.Published,  // the corrected version is the newest one
            Updates = [.. m.Updates, new UpdateView(e.Type, e.NoticeDoi, e.Version, ctx.OccurredAt)],
        }));

        // Erasure deletes the author's key; the read model drops the name too.
        On<SubjectErased>((e, ctx) => Change(ctx, m => m with
        {
            Authors = [.. m.Authors.Select(a => a.AuthorId == e.SubjectId ? a with { Name = null } : a)],
        }));
    }

    // A rebuild calls this, then replays every event.
    protected override async Task ResetAsync(WriteContext<PublishingDb> context)
    {
        var (db, ct) = (context.Db, context.CancellationToken);
        await db.VersionSections.ExecuteDeleteAsync(ct);
        await db.Versions.ExecuteDeleteAsync(ct);
        await db.Manuscripts.ExecuteDeleteAsync(ct);
    }

    // Changes the document, then copies the fields that the list filters and sorts on into their columns.
    private static async Task Change(ProjectionContext<PublishingDb> ctx, Func<ManuscriptView, ManuscriptView> change)
    {
        var row = (await ctx.Db.Manuscripts.FindAsync([ctx.StreamId], ctx.CancellationToken))!;
        var document = change(Documents.Read(row.Document));
        row.Document = Documents.Write(document);
        (row.Status, row.Doi, row.LatestVersion, row.PublishedVersion, row.UpdatedAt) =
            (document.Status, document.Doi, document.Latest?.Number, document.Published?.Number, ctx.OccurredAt);
    }
}
// end-snippet
