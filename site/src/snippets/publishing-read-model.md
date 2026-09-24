<!-- snippet: publishing-read-model -->
```cs
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
```
<!-- endSnippet -->
