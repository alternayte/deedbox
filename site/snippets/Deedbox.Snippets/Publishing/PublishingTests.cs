using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deedbox.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Publishing;

/// <summary>Runs the manuscript tutorial end to end: commands, the projection, REST, GraphQL and erasure, on Postgres.</summary>
public sealed class PublishingTutorialTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task A_manuscript_goes_from_draft_to_a_corrected_version_of_record_and_the_api_serves_every_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        PublishingApp.Register(builder, _postgres.GetConnectionString());
        await using var app = builder.Build();
        PublishingApp.Map(app);
        using (var setup = app.Services.CreateScope())
            await setup.ServiceProvider.GetRequiredService<PublishingDb>().Database.EnsureCreatedAsync(ct);
        await app.StartAsync(ct);
        var http = app.GetTestClient();
        var store = app.Services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();
        Task Run(Func<Manuscript, IEnumerable<object>> decide) => store.Execute("m-1", decide, ct);

        await Run(m => Editorial.Start(m, "Frozen versions in practice"));
        await Run(m => Editorial.AddAuthor(m, "author:ada", "Ada Lovelace", "Analytical Engines Ltd"));
        await Run(m => Editorial.Revise(m, "intro", "Introduction", "h1"));
        await Run(m => Editorial.Revise(m, "methods", "Methods", "h2"));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/manuscripts/m-1/submit", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/manuscripts/m-1/decision?decision=MajorRevision", null, ct)).StatusCode);
        await Run(m => Editorial.Revise(m, "methods", "Methods", "h3"));
        await http.PostAsync("/manuscripts/m-1/submit", null, ct);
        await http.PostAsync("/manuscripts/m-1/decision?decision=Accept", null, ct);
        await Run(m => Editorial.EditorialChange(m, "intro", "Introduction", "h1b", "Typo in the first paragraph"));
        await Run(m => Editorial.Publish(m, "10.5555/frozen"));
        await Run(m => Editorial.Correct(m, "methods", "Methods", "h4", "10.5555/frozen-corr"));

        var manuscript = await Json(http.GetAsync("/manuscripts/m-1", ct));
        Assert.Equal(("Published", "10.5555/frozen"), (manuscript.GetProperty("status").GetString(), manuscript.GetProperty("doi").GetString()));
        var published = manuscript.GetProperty("published");
        Assert.Equal((6, "CorrectedVersionOfRecord"), (published.GetProperty("number").GetInt32(), published.GetProperty("stage").GetString()));
        Assert.Equal(["/content/h1b", "/content/h4"], published.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("contentUrl").GetString()));
        Assert.Equal("Ada Lovelace", manuscript.GetProperty("authors")[0].GetProperty("name").GetString());
        Assert.Equal(["MajorRevision", "Accept"], manuscript.GetProperty("rounds").EnumerateArray().Select(r => r.GetProperty("decision").GetString()));
        Assert.Equal("Correction", manuscript.GetProperty("updates")[0].GetProperty("type").GetString());

        var history = await Json(http.GetAsync("/manuscripts/m-1/versions", ct));
        Assert.Equal(["SubmittedUnderReview", "SubmittedUnderReview", "AcceptedManuscript", "AcceptedManuscript", "VersionOfRecord", "CorrectedVersionOfRecord"],
            history.EnumerateArray().Select(v => v.GetProperty("stage").GetString()));
        Assert.Equal("Typo in the first paragraph", history[3].GetProperty("reason").GetString());

        var revision = await Json(http.GetAsync("/manuscripts/m-1/versions/2/changes?from=1", ct));
        Assert.Equal(("methods", "changed"), (revision[0].GetProperty("sectionId").GetString(), revision[0].GetProperty("change").GetString()));
        Assert.Equal(1, revision.GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/manuscripts/m-1/submit", null, ct)).StatusCode);

        var graphql = await Json(http.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new
        {
            query = """{ manuscript(id: "m-1") { status published { number stage } } versions(id: "m-1") { number } changes(id: "m-1", from: 3, to: 4) { sectionId change } }""",
        }), Encoding.UTF8, "application/json"), ct));
        var data = graphql.GetProperty("data");
        Assert.Equal(("PUBLISHED", "CORRECTED_VERSION_OF_RECORD"),
            (data.GetProperty("manuscript").GetProperty("status").GetString(), data.GetProperty("manuscript").GetProperty("published").GetProperty("stage").GetString()));
        Assert.Equal(6, data.GetProperty("versions").GetArrayLength());
        Assert.Equal("intro", data.GetProperty("changes")[0].GetProperty("sectionId").GetString());

        // Erasure: the key goes at once; the job appends SubjectErased, and the projection drops the name.
        await app.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("author:ada", ct);
        for (var i = 0; i < 300 && (await Json(http.GetAsync("/manuscripts/m-1", ct))).GetProperty("authors")[0].GetProperty("name").ValueKind != JsonValueKind.Null; i++)
            await Task.Delay(100, ct);
        Assert.Equal(JsonValueKind.Null, (await Json(http.GetAsync("/manuscripts/m-1", ct))).GetProperty("authors")[0].GetProperty("name").ValueKind);
        await app.StopAsync(ct);
    }

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> response)
    {
        var message = await response;
        message.EnsureSuccessStatusCode();
        return (await message.Content.ReadFromJsonAsync<JsonElement>())!;
    }
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
