<!-- snippet: publishing-queries -->
```cs
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
```
<!-- endSnippet -->
