<!-- snippet: publishing-people-projection -->
```cs
// A second read model from the same events, for questions across manuscripts. It rebuilds on its own.
public sealed class PeopleProjection : Projection<PublishingDb>
{
    public PeopleProjection()
    {
        On<AuthorAdded>(async (e, ctx) =>
        {
            // One profile per person: the newest name and ORCID win.
            if (await ctx.Db.People.FindAsync([e.PersonId], ctx.CancellationToken) is { } person)
                (person.Name, person.Orcid) = (e.Name, e.Orcid);
            else
                ctx.Db.People.Add(new PersonRow { PersonId = e.PersonId, Name = e.Name, Orcid = e.Orcid });

            ctx.Db.ManuscriptAuthors.Add(new ManuscriptAuthorRow
            {
                ManuscriptId = ctx.StreamId, PersonId = e.PersonId, Position = ctx.Version, Affiliation = e.Affiliation, Corresponding = e.Corresponding,
            });
        });

        // Erasure reaches every manuscript of the person; each one clears the same profile.
        On<SubjectErased>(async (e, ctx) =>
        {
            if (await ctx.Db.People.FindAsync([e.SubjectId], ctx.CancellationToken) is { } person)
                (person.Name, person.Orcid) = (null, null);
        });
    }

    protected override async Task ResetAsync(WriteContext<PublishingDb> context)
    {
        await context.Db.ManuscriptAuthors.ExecuteDeleteAsync(context.CancellationToken);
        await context.Db.People.ExecuteDeleteAsync(context.CancellationToken);
    }
}
```
<!-- endSnippet -->
