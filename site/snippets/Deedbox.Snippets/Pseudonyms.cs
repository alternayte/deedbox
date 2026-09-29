using Microsoft.Extensions.DependencyInjection;

namespace Shop;

// begin-snippet: pseudonym-event
public record CorrectionRecorded(
    [property: DataSubject] string Author,   // a pseudonymous subject ID, never the login
    [property: PersonalData] string? Text);  // free text can still name a person
// end-snippet

public record Session(int Corrections) : IState<Session>
{
    public static Session Initial { get; } = new(0);

    public static Session Evolve(Session s, object e) => e is CorrectionRecorded ? s with { Corrections = s.Corrections + 1 } : s;
}

public static class PseudonymSetup
{
    public static void Register(IServiceCollection services, string connStr)
    {
        // begin-snippet: pseudonyms-register
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Keys(keys => keys.FromEnvironment("DEEDBOX_MASTER_KEY"))
            .PseudonymPrefix("person:")  // the default; set it once
            .Stream<Session>(s => s.Events<CorrectionRecorded>()));
        // end-snippet
    }

    public static async Task Record(IPseudonyms pseudonyms, IEventStore store, TimeProvider clock, string sessionId, string login, string text)
    {
        // begin-snippet: pseudonyms-write
        // One secret per quarter: the same person gets a new subject ID each quarter.
        var author = await pseudonyms.SubjectForAsync($"github:{login}", PseudonymPeriod.Quarter(clock.GetUtcNow()));
        await store.Append(sessionId, ExpectedVersion.Any, [new CorrectionRecorded(author, text)]);
        // end-snippet
    }

    public static async Task Erase(IPseudonyms pseudonyms)
    {
        // begin-snippet: pseudonyms-erase
        // Erases the person's subject in every period whose secret still exists.
        var jobIds = await pseudonyms.EraseIdentityAsync("github:alice");
        // end-snippet
        _ = jobIds;
    }

    public static async Task Destroy(IEventStoreAdmin admin)
    {
        // begin-snippet: pseudonyms-destroy
        // The app no longer writes in 2026-Q1. Nobody can link its subject IDs to a person again.
        var destroyed = await admin.DestroyPseudonymPeriodAsync("2026-Q1", tenantId: "acme");
        // end-snippet
        _ = destroyed;
    }
}
