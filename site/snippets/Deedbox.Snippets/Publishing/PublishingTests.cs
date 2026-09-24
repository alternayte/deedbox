using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deedbox.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Publishing;

/// <summary>Runs the manuscript tutorial end to end on Postgres: commands, the projection, REST, GraphQL and erasure.</summary>
public sealed class PublishingTutorialTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manuscript_goes_from_draft_to_a_corrected_version_of_record_and_the_api_serves_every_version()
    {
        await using var app = await Start();
        var http = app.GetTestClient();
        var store = Store(app);
        Task Run(Func<Manuscript, IEnumerable<object>> decide) => store.Execute("m-1", decide, Ct);

        await Run(m => Editorial.Start(m, "Frozen versions in practice"));
        await Run(m => Editorial.AddAuthor(m, "author:ada", "Ada Lovelace", "Analytical Engines Ltd"));
        await Run(m => Editorial.Revise(m, "intro", "Introduction", "h1"));
        await Run(m => Editorial.Revise(m, "methods", "Methods", "h2"));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/manuscripts/m-1/submit", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/manuscripts/m-1/decision?decision=MajorRevision", null, Ct)).StatusCode);
        await Run(m => Editorial.Revise(m, "methods", "Methods", "h3"));
        await http.PostAsync("/manuscripts/m-1/submit", null, Ct);
        await http.PostAsync("/manuscripts/m-1/decision?decision=Accept", null, Ct);
        await Run(m => Editorial.EditorialChange(m, "intro", "Introduction", "h1b", "Typo in the first paragraph"));
        await Run(m => Editorial.Publish(m, "10.5555/frozen"));
        await Run(m => Editorial.Correct(m, "methods", "Methods", "h4", "10.5555/frozen-corr"));

        using var queries = new PublishingQueries();
        var manuscript = await Json(http.GetAsync("/manuscripts/m-1", Ct));
        Assert.Equal(1, queries.Count);  // the detail page is one read of the stored document
        Assert.Equal(("Published", "10.5555/frozen"), (manuscript.GetProperty("status").GetString(), manuscript.GetProperty("doi").GetString()));
        var published = manuscript.GetProperty("published");
        Assert.Equal((6, "CorrectedVersionOfRecord"), (published.GetProperty("number").GetInt32(), published.GetProperty("stage").GetString()));
        Assert.Equal(["/content/h1b", "/content/h4"], published.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("contentUrl").GetString()));
        Assert.Equal("Ada Lovelace", manuscript.GetProperty("authors")[0].GetProperty("name").GetString());
        Assert.Equal(["MajorRevision", "Accept"], manuscript.GetProperty("rounds").EnumerateArray().Select(r => r.GetProperty("decision").GetString()));
        Assert.Equal("Correction", manuscript.GetProperty("updates")[0].GetProperty("type").GetString());

        var history = await Json(http.GetAsync("/manuscripts/m-1/versions", Ct));
        Assert.Equal(["SubmittedUnderReview", "SubmittedUnderReview", "AcceptedManuscript", "AcceptedManuscript", "VersionOfRecord", "CorrectedVersionOfRecord"],
            history.EnumerateArray().Select(v => v.GetProperty("stage").GetString()));
        Assert.Equal("Typo in the first paragraph", history[3].GetProperty("reason").GetString());
        Assert.Equal(["/content/h1", "/content/h3"], (await Json(http.GetAsync("/manuscripts/m-1/versions/2", Ct)))
            .GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("contentUrl").GetString()));

        var revision = await Json(http.GetAsync("/manuscripts/m-1/versions/2/changes?from=1", Ct));
        Assert.Equal(("methods", "changed"), (revision[0].GetProperty("sectionId").GetString(), revision[0].GetProperty("change").GetString()));
        Assert.Equal(1, revision.GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/manuscripts/m-1/submit", null, Ct)).StatusCode);

        // Erasure: the key goes at once; the job appends SubjectErased, and the projection drops the name.
        await app.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("author:ada", Ct);
        for (var i = 0; i < 300 && (await Json(http.GetAsync("/manuscripts/m-1", Ct))).GetProperty("authors")[0].GetProperty("name").ValueKind != JsonValueKind.Null; i++)
            await Task.Delay(100, Ct);
        Assert.Equal(JsonValueKind.Null, (await Json(http.GetAsync("/manuscripts/m-1", Ct))).GetProperty("authors")[0].GetProperty("name").ValueKind);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task The_list_pages_by_newest_change_and_filters_by_title_and_status()
    {
        await using var app = await Start();
        var http = app.GetTestClient();
        var store = Store(app);
        foreach (var (id, title) in new[] { ("m-1", "Frozen versions"), ("m-2", "Peer review at scale"), ("m-3", "More frozen things"), ("m-4", "100% accurate") })
            await store.Execute<Manuscript>(id, m => Editorial.Start(m, title), Ct);
        await store.Execute<Manuscript>("m-1", m => Editorial.Revise(m, "intro", "Introduction", "h1"), Ct);
        await store.Execute<Manuscript>("m-1", Editorial.Submit, Ct);

        var first = await Json(http.GetAsync("/manuscripts?limit=2", Ct));
        Assert.Equal(["m-1", "m-4"], first.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        var second = await Json(http.GetAsync($"/manuscripts?limit=2&after={Uri.EscapeDataString(first.GetProperty("next").GetString()!)}", Ct));
        Assert.Equal(["m-3", "m-2"], second.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);

        var frozen = await Json(http.GetAsync("/manuscripts?search=FROZEN", Ct));
        Assert.Equal(["m-1", "m-3"], frozen.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        var percent = await Json(http.GetAsync($"/manuscripts?search={Uri.EscapeDataString("0%")}", Ct));
        Assert.Equal(["m-4"], percent.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        var underReview = await Json(http.GetAsync("/manuscripts?status=UnderReview", Ct));
        Assert.Equal(["m-1"], underReview.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task GraphQL_loads_a_page_of_manuscripts_and_their_fields_with_one_query_per_dataloader()
    {
        await using var app = await Start();
        var http = app.GetTestClient();
        var store = Store(app);
        foreach (var id in new[] { "m-1", "m-2", "m-3" })
        {
            await store.Execute<Manuscript>(id, m => Editorial.Start(m, $"Title {id}"), Ct);
            await store.Execute<Manuscript>(id, m => Editorial.AddAuthor(m, $"author:{id}", $"Author of {id}", "Lab"), Ct);
            await store.Execute<Manuscript>(id, m => Editorial.Revise(m, "intro", "Introduction", $"h-{id}"), Ct);
            await store.Execute<Manuscript>(id, Editorial.Submit, Ct);
        }

        using var queries = new PublishingQueries();
        var data = (await GraphQL(http, """
            { manuscripts(limit: 10) { items { id title authors { name } latest { number stage } versions { number } } next } }
            """)).GetProperty("manuscripts");

        Assert.Equal(3, queries.Count);  // the page, then one query for the documents and one for the versions
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["m-3", "m-2", "m-1"], items.Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("Author of m-3", items[0].GetProperty("authors")[0].GetProperty("name").GetString());
        Assert.Equal(("SUBMITTED_UNDER_REVIEW", 1), (items[0].GetProperty("latest").GetProperty("stage").GetString(), items[0].GetProperty("versions").GetArrayLength()));

        var one = await GraphQL(http, """{ manuscript(id: "m-2") { status latest { sections { contentUrl } } } }""");
        Assert.Equal(("UNDER_REVIEW", "/content/h-m-2"), (one.GetProperty("manuscript").GetProperty("status").GetString(),
            one.GetProperty("manuscript").GetProperty("latest").GetProperty("sections")[0].GetProperty("contentUrl").GetString()));
    }

    private async Task<WebApplication> Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        PublishingApp.Register(builder, _postgres.GetConnectionString());
        var app = builder.Build();
        PublishingApp.Map(app);
        using (var setup = app.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<PublishingDb>().Database.EnsureCreatedAsync(Ct);
        await app.StartAsync(Ct);
        return app;
    }

    private static IEventStore Store(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

    private static async Task<JsonElement> GraphQL(HttpClient http, string query)
    {
        var body = await Json(http.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"), Ct));
        Assert.False(body.TryGetProperty("errors", out var errors), errors.ToString());
        return body.GetProperty("data");
    }

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> response)
    {
        var message = await response;
        message.EnsureSuccessStatusCode();
        return (await message.Content.ReadFromJsonAsync<JsonElement>(Ct))!;
    }
}

/// <summary>Counts the SQL commands that EF Core runs for PublishingDb while it is alive.</summary>
public sealed class PublishingQueries : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private int _count;

    public PublishingQueries() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

    public int Count => _count;

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
            _subscriptions.Add(listener.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> e)
    {
        if (e.Key == RelationalEventId.CommandExecuted.Name && e.Value is CommandExecutedEventData { Context: PublishingDb })
            Interlocked.Increment(ref _count);
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose() => _subscriptions.ForEach(s => s.Dispose());
}

// begin-snippet: publishing-decider-tests
public class EditorialTests
{
    private static readonly SectionRef Intro = new("intro", "Introduction", "h1");

    [Fact]
    public void Submitting_freezes_the_draft_and_pins_the_round_to_that_version() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"))
            .When(Editorial.Submit)
            .Then(new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1));

    [Fact]
    public void Authors_cannot_change_a_version_that_reviewers_read() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"),
                new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1))
            .When(m => Editorial.Revise(m, "intro", "Introduction", "h2"))
            .ThenThrows<InvalidOperationException>();

    [Fact]
    public void Acceptance_freezes_the_reviewed_content_as_the_accepted_manuscript() =>
        Decider.Given<Manuscript>(new ManuscriptStarted("T"), new SectionRevised("intro", "Introduction", "h1"),
                new VersionFrozen(1, Stage.SubmittedUnderReview, [Intro], null, null), new ReviewRoundOpened(1, 1))
            .When(m => Editorial.Decide(m, Decision.Accept))
            .Then(new DecisionMade(1, Decision.Accept), new VersionFrozen(2, Stage.AcceptedManuscript, [Intro], 1, null));
}
// end-snippet
