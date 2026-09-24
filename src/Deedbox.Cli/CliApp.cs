using System.CommandLine;
using System.Text.Json;
using Azure.Identity;
using Deedbox.Postgres;
using Deedbox.SqlServer;
using Deedbox.Testing.Contracts;
using Npgsql;

namespace Deedbox.Cli;

internal static class CliApp
{
    private const string Postgres = "postgres";
    private const string SqlServer = "sqlserver";

    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            return await Build(output, error).Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });
        }
        catch (Exception ex) when (ex is DeedboxException or CliException or ArgumentException or FormatException)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static RootCommand Build(TextWriter output, TextWriter error)
    {
        var provider = new Option<string?>("--provider") { Description = "The database: postgres or sqlserver.", Recursive = true };
        provider.AcceptOnlyFromAmong(Postgres, SqlServer);
        var schema = new Option<string>("--schema") { Description = "The Deedbox schema name.", DefaultValueFactory = _ => SchemaName.Default, Recursive = true };
        var connection = new Option<string?>("--connection")
        {
            Description = "The connection string. Defaults to the DEEDBOX_CONNECTION environment variable.",
            Recursive = true,
        };
        var wait = new Option<bool>("--wait") { Description = "Wait for the job to finish; exit 1 if it fails." };
        var target = new Target(provider, schema, connection);

        // ---- schema ----
        var from = new Option<int>("--from") { Description = "The schema version the database has now; 0 for a new database.", DefaultValueFactory = _ => 0 };
        var script = new Command("script", "Print the SQL for every migration after --from.") { from };
        script.SetAction(async (result, ct) =>
        {
            var name = SchemaName.Validate(result.GetValue(schema)!);
            var migrations = RequireProvider(result, provider) == Postgres ? PostgresProvider.AllMigrations : SqlServerProvider.AllMigrations;
            await output.WriteAsync(SchemaScript.Render(migrations, name, result.GetValue(from)));
            return 0;
        });

        var apply = new Command("apply", "Apply pending migrations under a database lock.");
        apply.SetAction(async (result, ct) =>
        {
            await using var db = target.Open(result);
            var (before, after) = await SchemaManager.Apply(db, ct);
            await output.WriteLineAsync(before == after
                ? $"Schema '{db.Schema}' is up to date at version {after}."
                : $"Schema '{db.Schema}' migrated from version {before} to {after}.");
            return 0;
        });

        // ---- status ----
        var json = new Option<bool>("--json") { Description = "Print the status as JSON." };
        var status = new Command("status", "Show checkpoints, lag, stalled consumers with their poison event, and recent jobs.") { json };
        status.SetAction(async (result, ct) =>
        {
            await using var db = target.Open(result);
            var state = await Admin.Status(db, ct);
            if (result.GetValue(json))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(state, CliJson.Default.StoreStatus));
                return 0;
            }

            await output.WriteLineAsync($"Head position: {state.Head}");
            await output.WriteLineAsync();
            await output.WriteLineAsync($"{"CONSUMER",-30} {"MODE",-13} {"STATUS",-11} {"POSITION",10} {"LAG",10}  UPDATED");
            foreach (var c in state.Consumers)
                await output.WriteLineAsync($"{c.Name,-30} {c.Mode,-13} {c.Status,-11} {c.Position,10} {c.Lag,10}  {c.UpdatedAt:u}");

            foreach (var c in state.Consumers.Where(c => c.Error is not null))
            {
                var e = JsonDocument.Parse(c.Error!).RootElement;
                await output.WriteLineAsync();
                await output.WriteLineAsync($"{c.Name} is stalled ({Text(e, "reason")}).");
                if (Text(e, "reason") == "poison")
                {
                    await output.WriteLineAsync($"  event {Text(e, "eventId")} ({Text(e, "eventType")}) in stream '{Text(e, "streamId")}' version {Text(e, "version")}, position {Text(e, "globalPosition")}");
                    await output.WriteLineAsync($"  {Text(e, "exception")}: {Text(e, "message")}");
                    await output.WriteLineAsync($"  Fix the handler and restart, or: deedbox skip {c.Name} --event {Text(e, "eventId")}");
                }
                else
                {
                    await output.WriteLineAsync($"  Rebuild it: deedbox rebuild {c.Name}");
                }
            }

            if (state.Jobs.Count > 0)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync($"{"JOB",-36} {"KIND",-10} {"STATUS",-8} CREATED");
                foreach (var j in state.Jobs)
                    await output.WriteLineAsync($"{j.Id,-36} {j.Kind,-10} {j.Status,-8} {j.CreatedAt:u}");
            }

            return 0;
        });

        // ---- jobs the app runs ----
        var projectionName = new Argument<string>("projection") { Description = "The stored projection name." };
        var rebuild = new Command("rebuild", "Queue an in-place rebuild of a projection; the app's runner does it.") { projectionName, wait };
        rebuild.SetAction((result, ct) => Queue(result, target, output, wait, Jobs.Rebuild, Admin.RebuildArgs(result.GetValue(projectionName)!), ct));

        var consumerName = new Argument<string>("consumer") { Description = "The stalled projection or subscription." };
        var eventId = new Option<Guid>("--event") { Description = "The event it stalled on, from deedbox status.", Required = true };
        var skip = new Command("skip", "Queue an audited skip of the event a consumer stalled on.") { consumerName, eventId, wait };
        skip.SetAction((result, ct) => Queue(result, target, output, wait, Jobs.Skip, Admin.SkipArgs(result.GetValue(consumerName)!, result.GetValue(eventId)), ct));

        var subject = new Argument<string>("subject") { Description = "The data subject, such as person:8421." };
        var tenant = new Option<string>("--tenant") { Description = "The tenant; empty by default.", DefaultValueFactory = _ => "" };
        var erase = new Command("erase", "Delete a data subject's key now, and queue the job that finishes their erasure.") { subject, tenant, wait };
        erase.SetAction(async (result, ct) =>
        {
            var args = Admin.EraseArgs(result.GetValue(tenant)!, result.GetValue(subject)!);
            await using (var db = target.Open(result))
                await Admin.DeleteSubjectKey(db, result.GetValue(tenant)!, result.GetValue(subject)!, ct);
            return await Queue(result, target, output, wait, Jobs.Erase, args, ct);
        });

        var streamType = new Argument<string>("streamType") { Description = "The stored stream type name." };
        var snapshotsRebuild = new Command("rebuild", "Queue a rebuild of the stored state of every stream of a type.") { streamType, wait };
        snapshotsRebuild.SetAction((result, ct) => Queue(result, target, output, wait, Jobs.Snapshots, Admin.SnapshotArgs(result.GetValue(streamType)!), ct));

        // ---- keys ----
        var fromKey = new Option<string>("--from")
        {
            Description = "The master key that wrapped the keys now: database, env:<VARIABLE> or azure:<key URL>.",
            Required = true,
        };
        var toKey = new Option<string>("--to") { Description = "The master key to wrap them with: database, env:<VARIABLE> or azure:<key URL>.", Required = true };
        var rewrap = new Command("rewrap", "Re-wrap every tenant key with another master key. Events are not touched.") { fromKey, toKey };
        rewrap.SetAction(async (result, ct) =>
        {
            await using var db = target.Open(result);
            var ring = new KeyRing(db, MasterKey(result.GetValue(fromKey)!, db));
            var to = MasterKey(result.GetValue(toKey)!, db);
            var count = await ring.Rewrap(to, ct);
            await output.WriteLineAsync($"Re-wrapped {count} tenant keys with {to.KeyVersion}. Configure that key mode in the app now.");
            return 0;
        });

        // ---- tenants ----
        var tenantName = new Argument<string>("tenant") { Description = "The tenant to shred." };
        var yes = new Option<bool>("--yes") { Description = "Confirm: this destroys the tenant's keys and cannot be undone." };
        var shred = new Command("shred", "Crypto-shred a tenant: every personal field and sealed state of it reads as erased.") { tenantName, yes };
        shred.SetAction(async (result, ct) =>
        {
            if (!result.GetValue(yes))
                throw new CliException($"Shredding tenant '{result.GetValue(tenantName)}' destroys its keys and cannot be undone. Add --yes to confirm.");
            await using var db = target.Open(result);
            await Admin.ShredTenant(db, DeedboxContext.ValidTenant(result.GetValue(tenantName)!), ct);
            await output.WriteLineAsync($"Tenant '{result.GetValue(tenantName)}' is shredded. Restart app instances to drop its key from memory; reads already treat it as erased.");
            return 0;
        });

        // ---- lockfile ----
        var oldFile = new Argument<FileInfo>("old") { Description = "The earlier lockfile, such as the one on the main branch." };
        var newFile = new Argument<FileInfo>("new") { Description = "The lockfile to compare." };
        var diff = new Command("diff", "Show event-contract changes between two lockfiles; exit 1 when one breaks stored events.") { oldFile, newFile };
        diff.SetAction(async (result, ct) =>
        {
            var old = Lockfile.Parse(await File.ReadAllTextAsync(result.GetValue(oldFile)!.FullName, ct));
            var now = Lockfile.Parse(await File.ReadAllTextAsync(result.GetValue(newFile)!.FullName, ct));
            var changes = Compatibility.Diff(old, now);
            var breaks = Compatibility.Breaks(old, now);
            foreach (var line in changes)
                await output.WriteLineAsync(line);
            if (changes.Count == 0)
                await output.WriteLineAsync("No event-contract changes.");
            foreach (var line in breaks)
                await error.WriteLineAsync("BREAK " + line);
            return breaks.Count == 0 ? 0 : 1;
        });

        return new RootCommand("The Deedbox command-line tool.")
        {
            provider, schema, connection,
            new Command("schema", "Print or apply the Deedbox schema.") { script, apply },
            status,
            rebuild,
            skip,
            erase,
            new Command("snapshots", "Rebuild stored state.") { snapshotsRebuild },
            new Command("keys", "Manage the master key.") { rewrap },
            new Command("tenant", "Manage tenants.") { shred },
            new Command("lockfile", "Work with event-contract lockfiles.") { diff },
        };
    }

    private static async Task<int> Queue(ParseResult result, Target target, TextWriter output, Option<bool> wait, string kind, System.Text.Json.Nodes.JsonObject args, CancellationToken ct)
    {
        await using var db = target.Open(result);
        var id = await Jobs.Enqueue(db, TimeProvider.System, kind, args, ct);
        await output.WriteLineAsync($"Queued {kind} job {id}. A running app instance does it; follow it with deedbox status.");
        if (!result.GetValue(wait))
            return 0;

        while (true)
        {
            var job = await Admin.Job(db, id, ct);
            if (job?.Status is "done" or "failed")
            {
                await output.WriteLineAsync($"Job {id} {job.Status}: {job.Progress}");
                return job.Status == "done" ? 0 : 1;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private static string RequireProvider(ParseResult result, Option<string?> provider) =>
        result.GetValue(provider) ?? throw new CliException("Pass --provider postgres or --provider sqlserver.");

    private static IMasterKeyProvider MasterKey(string spec, DeedboxProvider db)
    {
        if (spec == "database")
            return new DatabaseMasterKey(db);
        if (spec.StartsWith("env:", StringComparison.Ordinal))
            return new KeysBuilder().FromEnvironment(spec[4..]).Factory!(db);
        if (spec.StartsWith("azure:", StringComparison.Ordinal))
            return new KeysBuilder().UseAzureKeyVault(new Uri(spec[6..]), new DefaultAzureCredential()).Factory!(db);
        throw new CliException($"Master key '{spec}' is not valid. Use database, env:<VARIABLE> or azure:<key URL>.");
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value.ToString() : "";

    private sealed class Target(Option<string?> provider, Option<string> schema, Option<string?> connection)
    {
        public DeedboxProvider Open(ParseResult result)
        {
            var name = SchemaName.Validate(result.GetValue(schema)!);
            var connectionString = result.GetValue(connection) ?? Environment.GetEnvironmentVariable("DEEDBOX_CONNECTION");
            if (string.IsNullOrEmpty(connectionString))
                throw new CliException("Pass --connection or set DEEDBOX_CONNECTION.");
            return RequireProvider(result, provider) == Postgres
                ? new PostgresProvider(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, name)
                : new SqlServerProvider(connectionString, name);
        }
    }
}

internal sealed class CliException(string message) : Exception(message);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(StoreStatus))]
internal sealed partial class CliJson : System.Text.Json.Serialization.JsonSerializerContext;
