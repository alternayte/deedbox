using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Shop;

public static class Integrations
{
    public static void QueueBox(IServiceCollection services, string connStr)
    {
        // begin-snippet: queuebox
        services.AddDeedbox(es => es
            .UsePostgres(connStr)
            .Keys(keys => keys.FromEnvironment("DEEDBOX_MASTER_KEY"))
            .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
            .Stream<Manuscript>(s => s.Events<ReviewerInvited, CoAuthorAdded>())
            .UseQueueBox(q => q
                .Publish<CheckedOut>("cart.checked_out")
                // Events with [PersonalData] need a payload you shape, so no personal data leaks by default.
                .Publish<ReviewerInvited>("review.invited", (e, info) => new { e.ManuscriptId, e.ReviewerId })
                // Tell downstream systems to erase too.
                .Publish<SubjectErased>("privacy.subject_erased")));
        // end-snippet
    }

    public static void Metadata(WebApplication app)
    {
        // begin-snippet: metadata-middleware
        // Set the tenant and metadata once per request; every store in the request scope uses them.
        app.Use(async (http, next) =>
        {
            var deedbox = http.RequestServices.GetRequiredService<DeedboxContext>();
            deedbox.TenantId = http.Request.Headers["X-Tenant"].ToString();
            deedbox.Metadata = new EventMetadata
            {
                CorrelationId = http.TraceIdentifier,
                Actor = http.User.Identity?.Name is { } user ? $"user:{user}" : null,
            };
            await next(http);
        });
        // end-snippet
    }

    public static void Health(WebApplicationBuilder builder)
    {
        // begin-snippet: health-checks
        builder.Services.AddHealthChecks().AddDeedboxHealthChecks();
        // end-snippet
    }

    public static void Telemetry()
    {
        // begin-snippet: telemetry
        // With OpenTelemetry: subscribe to the "Deedbox" ActivitySource and Meter.
        //   .WithTracing(t => t.AddSource("Deedbox"))
        //   .WithMetrics(m => m.AddMeter("Deedbox"))
        const string SourceAndMeter = "Deedbox";
        // end-snippet
        _ = SourceAndMeter;
    }
}

// begin-snippet: schema-ef-migration
// An EF Core migration that creates the Deedbox tables without adding them to your model.
public partial class AddDeedbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(PostgresSchema.Script(fromVersion: 0));   // SqlServerSchema.Script on SQL Server
}
// end-snippet

public static class AdminSnippets
{
    public static async Task Operate(IEventStoreAdmin admin, Guid stalledEventId)
    {
        // begin-snippet: admin-api
        var status = await admin.GetStatusAsync();
        foreach (var consumer in status.Consumers)
            Console.WriteLine($"{consumer.Name}: {consumer.Status}, {consumer.Lag} behind");

        var rebuild = await admin.RebuildAsync("cart_summary");
        var skip = await admin.SkipAsync("cart_totals", stalledEventId);
        var job = await admin.GetJobAsync(rebuild);
        // end-snippet
        _ = (skip, job);
    }
}
