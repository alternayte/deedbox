using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Shop;

// begin-snippet: personal-data-event
public record ReviewerInvited(
    string ManuscriptId,
    [property: DataSubject] string ReviewerId,     // whose data this is
    [property: PersonalData] string ReviewerName,  // encrypted under ReviewerId's key
    [property: PersonalData] string? ReviewerEmail);
// end-snippet

// begin-snippet: personal-data-several
// Several subjects in one event: name each field's subject property.
public record CoAuthorAdded(
    string ManuscriptId,
    string AuthorId,
    [property: PersonalData(Subject = "AuthorId")] string AuthorName,
    string EditorId,
    [property: PersonalData(Subject = "EditorId")] string? EditorNote);
// end-snippet

public record Manuscript(int Reviewers) : IState<Manuscript>
{
    public static Manuscript Initial { get; } = new(0);

    public static Manuscript Evolve(Manuscript s, object e) => e is ReviewerInvited ? s with { Reviewers = s.Reviewers + 1 } : s;
}

public static class PersonalDataSetup
{
    public static void Database(IServiceCollection services, string connStr)
    {
        // begin-snippet: keys-database
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Keys(keys => keys.StoreInDatabase())
            .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
        // end-snippet
    }

    public static void Environment(IServiceCollection services, string connStr)
    {
        // begin-snippet: keys-environment
        // DEEDBOX_MASTER_KEY holds a key ring: v2:<base64 of 32 random bytes>,v1:<older key>
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Keys(keys => keys
                .FromEnvironment("DEEDBOX_MASTER_KEY")
                .RedactWith("[erased]"))
            .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
        // end-snippet
    }

    public static void Azure(IServiceCollection services, string connStr)
    {
        // begin-snippet: keys-azure
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Keys(keys => keys.UseAzureKeyVault(
                new Uri("https://my-vault.vault.azure.net/keys/deedbox"),
                new DefaultAzureCredential()))
            .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>()));
        // end-snippet
    }

    public static async Task Erase(ISubjectErasure erasure)
    {
        // begin-snippet: erase-subject
        // Their data reads as erased as soon as this returns; the job finishes the rest.
        var jobId = await erasure.EraseSubjectAsync("person:8421");
        // end-snippet
        _ = jobId;
    }

    public static async Task Shred(IEventStoreAdmin admin)
    {
        // begin-snippet: shred-tenant
        await admin.ShredTenantAsync("acme");
        // end-snippet
    }
}
