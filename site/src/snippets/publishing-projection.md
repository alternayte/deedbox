<!-- snippet: publishing-projection -->
```cs
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
```
<!-- endSnippet -->
