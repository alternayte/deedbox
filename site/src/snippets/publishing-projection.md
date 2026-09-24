<!-- snippet: publishing-projection -->
```cs
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
```
<!-- endSnippet -->
