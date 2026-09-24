<!-- snippet: publishing-read-model -->
```cs
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
```
<!-- endSnippet -->
