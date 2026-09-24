using Microsoft.EntityFrameworkCore;

namespace Publishing;

// begin-snippet: publishing-read-model
// One row per manuscript: what an API returns first.
public class ManuscriptRow
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public Status Status { get; set; }
    public int Round { get; set; }
    public int? LatestVersion { get; set; }     // the newest frozen version
    public int? PublishedVersion { get; set; }  // the version readers see: the Version of Record, or its correction
    public string? Doi { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// One row per frozen version, and one row per section of it: the version history.
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

public class AuthorRow
{
    public required string ManuscriptId { get; set; }
    public required string AuthorId { get; set; }
    public string? Name { get; set; }   // null once the author is erased
    public required string Affiliation { get; set; }
}

public class RoundRow
{
    public required string ManuscriptId { get; set; }
    public int Round { get; set; }
    public int Version { get; set; }
    public Decision? Decision { get; set; }
}

public class UpdateRow
{
    public required string ManuscriptId { get; set; }
    public required string NoticeDoi { get; set; }
    public UpdateType Type { get; set; }
    public int? Version { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
}

public class PublishingDb(DbContextOptions<PublishingDb> options) : DbContext(options)
{
    public DbSet<ManuscriptRow> Manuscripts => Set<ManuscriptRow>();
    public DbSet<VersionRow> Versions => Set<VersionRow>();
    public DbSet<VersionSectionRow> VersionSections => Set<VersionSectionRow>();
    public DbSet<AuthorRow> Authors => Set<AuthorRow>();
    public DbSet<RoundRow> Rounds => Set<RoundRow>();
    public DbSet<UpdateRow> Updates => Set<UpdateRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder conventions) =>
        conventions.Properties<Enum>().HaveConversion<string>();  // store enums as their names

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("publishing");
        model.Entity<ManuscriptRow>().ToTable("manuscripts").HasKey(m => m.Id);
        model.Entity<VersionRow>().ToTable("versions").HasKey(v => new { v.ManuscriptId, v.Number });
        model.Entity<VersionSectionRow>().ToTable("version_sections").HasKey(s => new { s.ManuscriptId, s.Version, s.Position });
        model.Entity<AuthorRow>().ToTable("authors").HasKey(a => new { a.ManuscriptId, a.AuthorId });
        model.Entity<RoundRow>().ToTable("review_rounds").HasKey(r => new { r.ManuscriptId, r.Round });
        model.Entity<UpdateRow>().ToTable("updates").HasKey(u => new { u.ManuscriptId, u.NoticeDoi });
    }
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
            ctx.Db.Manuscripts.Add(new ManuscriptRow { Id = ctx.StreamId, Title = e.Title, UpdatedAt = ctx.OccurredAt });
            return Task.CompletedTask;
        });

        On<AuthorAdded>((e, ctx) =>
        {
            ctx.Db.Authors.Add(new AuthorRow { ManuscriptId = ctx.StreamId, AuthorId = e.AuthorId, Name = e.Name, Affiliation = e.Affiliation });
            return Task.CompletedTask;
        });

        On<VersionFrozen>(async (e, ctx) =>
        {
            var manuscript = await Row(ctx);
            manuscript.LatestVersion = e.Number;
            ctx.Db.Versions.Add(new VersionRow
            {
                ManuscriptId = ctx.StreamId, Number = e.Number, Stage = e.Stage, BasedOn = e.BasedOn, Reason = e.Reason, FrozenAt = ctx.OccurredAt,
            });
            ctx.Db.VersionSections.AddRange(e.Sections.Select((s, i) => new VersionSectionRow
            {
                ManuscriptId = ctx.StreamId, Version = e.Number, Position = i, SectionId = s.SectionId, Heading = s.Heading, ContentHash = s.ContentHash,
            }));
        });

        On<ReviewRoundOpened>(async (e, ctx) =>
        {
            var manuscript = await Row(ctx);
            (manuscript.Status, manuscript.Round) = (Status.UnderReview, e.Round);
            ctx.Db.Rounds.Add(new RoundRow { ManuscriptId = ctx.StreamId, Round = e.Round, Version = e.Version });
        });

        On<DecisionMade>(async (e, ctx) =>
        {
            var manuscript = await Row(ctx);
            manuscript.Status = e.Decision switch { Decision.Accept => Status.Accepted, Decision.Reject => Status.Rejected, _ => Status.InRevision };
            (await ctx.Db.Rounds.FindAsync([ctx.StreamId, e.Round], ctx.CancellationToken))!.Decision = e.Decision;
        });

        On<Published>(async (e, ctx) =>
        {
            var manuscript = await Row(ctx);
            (manuscript.Status, manuscript.Doi, manuscript.PublishedVersion) = (Status.Published, e.Doi, e.Version);
        });

        On<UpdateIssued>(async (e, ctx) =>
        {
            var manuscript = await Row(ctx);
            if (e.Type is UpdateType.Correction)
                manuscript.PublishedVersion = e.Version;
            if (e.Type is UpdateType.Retraction)
                manuscript.Status = Status.Retracted;
            ctx.Db.Updates.Add(new UpdateRow { ManuscriptId = ctx.StreamId, NoticeDoi = e.NoticeDoi, Type = e.Type, Version = e.Version, IssuedAt = ctx.OccurredAt });
        });

        // Erasure deletes the author's key; the read model drops the name too.
        On<SubjectErased>(async (e, ctx) =>
        {
            if (await ctx.Db.Authors.FindAsync([ctx.StreamId, e.SubjectId], ctx.CancellationToken) is { } author)
                author.Name = null;
        });
    }

    // A rebuild calls this, then replays every event.
    protected override async Task ResetAsync(WriteContext<PublishingDb> context)
    {
        var (db, ct) = (context.Db, context.CancellationToken);
        await db.VersionSections.ExecuteDeleteAsync(ct);
        await db.Versions.ExecuteDeleteAsync(ct);
        await db.Authors.ExecuteDeleteAsync(ct);
        await db.Rounds.ExecuteDeleteAsync(ct);
        await db.Updates.ExecuteDeleteAsync(ct);
        await db.Manuscripts.ExecuteDeleteAsync(ct);
    }

    private static async Task<ManuscriptRow> Row(ProjectionContext<PublishingDb> ctx)
    {
        var manuscript = (await ctx.Db.Manuscripts.FindAsync([ctx.StreamId], ctx.CancellationToken))!;
        manuscript.UpdatedAt = ctx.OccurredAt;
        return manuscript;
    }
}
// end-snippet
