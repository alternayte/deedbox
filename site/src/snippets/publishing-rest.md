<!-- snippet: publishing-rest -->
```cs
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
```
<!-- endSnippet -->
