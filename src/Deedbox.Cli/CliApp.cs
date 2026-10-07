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
        var nativeJson = new Option<bool>("--native-json")
        {
            Description = "SQL Server: convert the JSON columns to the native json type (SQL Server 2025, Azure SQL).",
        };
        var script = new Command("script", "Print the SQL for every migration after --from.") { from, nativeJson };
        script.SetAction(async (result, ct) =>
        {
            RequireSqlServerForNativeJson(result, provider, nativeJson);
            var name = SchemaName.Validate(result.GetValue(schema)!);
            var sql = RequireProvider(result, provider) == Postgres
                ? SchemaScript.Render(PostgresProvider.AllMigrations, name, result.GetValue(from))
                : SchemaScript.Render(SqlServerProvider.AllMigrations, name, result.GetValue(from),
                    result.GetValue(nativeJson) ? SqlServerProvider.NativeJsonScript(name) : null);
            await output.WriteAsync(sql);
            return 0;
        });

        var apply = new Command("apply", "Apply pending migrations under a database lock.") { nativeJson };
        apply.SetAction(async (result, ct) =>
        {
            RequireSqlServerForNativeJson(result, provider, nativeJson);
            await using var db = target.Open(result, result.GetValue(nativeJson));
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
                await output.WriteLineAsync(StatusJson(state).ToJsonString(Indented));
                return 0;
            }

            await output.WriteLineAsync($"Head position: {state.Head}");
            await output.WriteLineAsync();
            await output.WriteLineAsync($"{"CONSUMER",-30} {"MODE",-13} {"STATUS",-11} {"POSITION",10} {"LAG",10}  UPDATED");
            foreach (var c in state.Consumers)
                await output.WriteLineAsync($"{c.Name,-30} {Wire.Text(c.Mode),-13} {Wire.Text(c.Status),-11} {c.Position,10} {c.Lag,10}  {c.UpdatedAt:u}");

            foreach (var c in state.Consumers.Where(c => c.Error is not null))
            {
                var e = JsonDocument.Parse(c.Error!).RootElement;
                await output.WriteLineAsync();
                if (Text(e, "reason") == ConsumerLoop.SlowCatchUpReason)
                {
                    await output.WriteLineAsync($"{c.Name} cannot finish its catch-up: {Text(e, "attempts")} forced cut-overs ran out of time, with {Text(e, "gap")} events left.");
                    await output.WriteLineAsync("  Its handlers are too slow for the append rate. It keeps trying; make the handlers faster, or run it async.");
                    continue;
                }

                await output.WriteLineAsync($"{c.Name} is stalled ({Text(e, "reason")}).");
                if (Text(e, "reason") == "poison")
                {
                    await output.WriteLineAsync($"  event {Text(e, "eventId")} ({Text(e, "eventType")}) in stream '{Text(e, "streamId")}' version {Text(e, "version")}, position {Text(e, "globalPosition")}");
                    await output.WriteLineAsync($"  {Text(e, "exception")}; the message is in the app's log, never in the store.");
                    if (e.TryGetProperty("retryAt", out var retryAt) && retryAt.TryGetDateTimeOffset(out var at))
                        await output.WriteLineAsync($"  {Text(e, "attempts")} attempts; the next retry is at {at:u}.");
                    await output.WriteLineAsync($"  Fix the cause and it runs again at the next retry, or: deedbox skip {c.Name} --event {Text(e, "eventId")}");
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
                    await output.WriteLineAsync($"{j.Id,-36} {Wire.Text(j.Kind),-10} {Wire.Text(j.Status),-8} {j.CreatedAt:u}");
            }

            return 0;
        });

        // ---- jobs the app runs ----
        var projectionName = new Argument<string>("projection") { Description = "The stored projection name." };
        var rebuild = new Command("rebuild", "Queue an in-place rebuild of a projection; the app's runner does it.") { projectionName, wait };
        rebuild.SetAction((result, ct) => Queue(result, target, output, wait, Jobs.Rebuild, Admin.RebuildArgs(result.GetValue(projectionName)!), ct));

        var retiredName = new Argument<string>("projection") { Description = "The stored projection or subscription name." };
        var retire = new Command("retire", "Retire a projection that no live instance registers; nothing applies it until a rebuild.") { retiredName };
        retire.SetAction(async (result, ct) =>
        {
            var name = result.GetValue(retiredName)!;
            await using var db = target.Open(result);
            await Admin.Retire(db, name, Instances.DefaultLiveFor, ct);
            await output.WriteLineAsync($"'{name}' is retired. Rebuild it to use it again.");
            return 0;
        });

        var consumerName = new Argument<string>("consumer") { Description = "The stalled projection or subscription." };
        var eventId = new Option<Guid>("--event") { Description = "The event it stalled on, from deedbox status.", Required = true };
        var skip = new Command("skip", "Queue an audited skip of the event a consumer stalled on.") { consumerName, eventId, wait };
        skip.SetAction((result, ct) => Queue(result, target, output, wait, Jobs.Skip, Admin.SkipArgs(result.GetValue(consumerName)!, result.GetValue(eventId)), ct));

        var subject = new Argument<string?>("subject") { Description = "The data subject, such as person:8421.", Arity = ArgumentArity.ZeroOrOne };
        var tenant = new Option<string>("--tenant") { Description = "The tenant; empty by default.", DefaultValueFactory = _ => "" };
        var identity = new Option<string?>("--identity")
        {
            Description = "Erase an identity, such as github:alice, instead of a subject: its pseudonymous subject in every period with a secret. It is never stored or printed.",
        };
        var masterKey = new Option<string?>("--master-key")
        {
            Description = "With --identity: the master key that wraps the pseudonym secrets: database, env:<VARIABLE> or azure:<key URL>.",
        };
        var erase = new Command("erase", "Delete a data subject's key now, and queue the job that finishes their erasure.") { subject, tenant, identity, masterKey, wait };
        erase.SetAction(async (result, ct) =>
        {
            var (subjectId, identityValue, keySpec) = (result.GetValue(subject), result.GetValue(identity), result.GetValue(masterKey));
            if ((subjectId is null) == (identityValue is null))
                throw new CliException("Pass a subject, such as deedbox erase person:8421, or --identity, such as deedbox erase --identity github:alice --master-key env:DEEDBOX_MASTER_KEY.");
            if (identityValue is null)
            {
                if (keySpec is not null)
                    throw new CliException("--master-key is only for --identity; erasing a subject needs no master key.");
                var subjectTenant = DeedboxContext.ValidTenant(result.GetValue(tenant)!);
                ErasureResult started;
                await using (var db = target.Open(result))
                    started = await Admin.Erase(db, TimeProvider.System, subjectTenant, [subjectId!], ct);
                if (started.KeysDeleted == 0)
                {
                    // Not a success: a wrong subject ID or tenant looks exactly like this.
                    await output.WriteLineAsync($"Tenant '{subjectTenant}' has no key for this subject, so no personal data was erased. Check the subject ID and --tenant.");
                    await Follow(result, target, output, wait, Jobs.Erase, started.JobIds[0], ct);
                    return 1;
                }

                await output.WriteLineAsync("Deleted the subject's key. A running app instance finishes the erasure.");
                return await Follow(result, target, output, wait, Jobs.Erase, started.JobIds[0], ct);
            }

            if (keySpec is null)
                throw new CliException("--identity needs --master-key, the master key that wraps the pseudonym secrets: database, env:<VARIABLE> or azure:<key URL>.");
            var tenantId = DeedboxContext.ValidTenant(result.GetValue(tenant)!);
            IReadOnlyList<Guid> jobs;
            await using (var db = target.Open(result))
                jobs = (await Admin.EraseIdentity(db, new Pseudonymizer(db, MasterKey(keySpec, db)), TimeProvider.System, tenantId, identityValue, ct)).JobIds;
            if (jobs.Count == 0)
            {
                await output.WriteLineAsync($"Tenant '{tenantId}' has no pseudonym period with a secret, so there is nothing to erase.");
                return 0;
            }

            await output.WriteLineAsync($"Deleted the identity's subject keys in {jobs.Count} periods. A running app instance finishes each erasure; follow them with deedbox status.");
            var failed = false;
            foreach (var id in jobs)
                failed |= await Follow(result, target, output, wait, Jobs.Erase, id, ct) != 0;
            return failed ? 1 : 0;
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
        var rewrap = new Command("rewrap", "Re-wrap every tenant key and pseudonym secret with another master key. Events are not touched.") { fromKey, toKey };
        rewrap.SetAction(async (result, ct) =>
        {
            await using var db = target.Open(result);
            var ring = new KeyRing(db, MasterKey(result.GetValue(fromKey)!, db));
            var to = MasterKey(result.GetValue(toKey)!, db);
            var count = await ring.Rewrap(to, ct);
            await output.WriteLineAsync($"Re-wrapped {count} tenant keys and pseudonym secrets with {to.KeyVersion}. Configure that key mode in the app now.");
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

        // ---- pseudonyms ----
        var periodName = new Argument<string>("period") { Description = "The period whose secret to destroy, such as 2026-Q1." };
        var periodTenant = new Option<string>("--tenant") { Description = "The tenant; empty by default.", DefaultValueFactory = _ => "" };
        var destroyYes = new Option<bool>("--yes") { Description = "Confirm: this destroys the period's secret and cannot be undone." };
        var destroy = new Command("destroy", "Destroy a period's pseudonym secret: its subject IDs can never be linked to an identity again.") { periodName, periodTenant, destroyYes };
        destroy.SetAction(async (result, ct) =>
        {
            var (period, tenantId) = (PseudonymPeriod.Validate(result.GetValue(periodName)!), DeedboxContext.ValidTenant(result.GetValue(periodTenant)!));
            if (!result.GetValue(destroyYes))
                throw new CliException($"Destroying the pseudonym secret of period '{period}' in tenant '{tenantId}' cannot be undone. Add --yes to confirm.");
            await using var db = target.Open(result);
            var destroyed = await Admin.DestroyPseudonymPeriod(db, TimeProvider.System, tenantId, period, ct);
            await output.WriteLineAsync(destroyed
                ? $"Destroyed the pseudonym secret of period '{period}' in tenant '{tenantId}'. Its subject IDs can no longer be linked to an identity; the jobs table records it."
                : $"Period '{period}' in tenant '{tenantId}' had no pseudonym secret. It is closed now; the jobs table records it.");
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
            retire,
            skip,
            erase,
            new Command("snapshots", "Rebuild stored state.") { snapshotsRebuild },
            new Command("keys", "Manage the master key.") { rewrap },
            new Command("tenant", "Manage tenants.") { shred },
            new Command("pseudonyms", "Manage pseudonym secrets.") { destroy },
            new Command("lockfile", "Work with event-contract lockfiles.") { diff },
        };
    }

    private static async Task<int> Queue(ParseResult result, Target target, TextWriter output, Option<bool> wait, string kind, System.Text.Json.Nodes.JsonObject args, CancellationToken ct)
    {
        Guid id;
        await using (var db = target.Open(result))
            id = await Jobs.Enqueue(db, TimeProvider.System, kind, args, ct);
        return await Follow(result, target, output, wait, kind, id, ct);
    }

    private static async Task<int> Follow(ParseResult result, Target target, TextWriter output, Option<bool> wait, string kind, Guid id, CancellationToken ct)
    {
        await output.WriteLineAsync($"Queued {kind} job {id}. A running app instance does it; follow it with deedbox status.");
        if (!result.GetValue(wait))
            return 0;

        await using var db = target.Open(result);
        while (true)
        {
            var job = await Admin.Job(db, id, ct);
            if (job?.Status is JobState.Done or JobState.Failed)
            {
                await output.WriteLineAsync($"Job {id} {Wire.Text(job.Status)}: {job.Progress}");
                return job.Status == JobState.Done ? 0 : 1;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The status as JSON with the stored text of each value, so the output does not change with the API's enums.</summary>
    private static System.Text.Json.Nodes.JsonObject StatusJson(StoreStatus state) => new()
    {
        ["head"] = state.Head,
        ["consumers"] = new System.Text.Json.Nodes.JsonArray([.. state.Consumers.Select(c => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = c.Name,
            ["mode"] = Wire.Text(c.Mode),
            ["status"] = Wire.Text(c.Status),
            ["position"] = c.Position,
            ["lag"] = c.Lag,
            ["updatedAt"] = c.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["error"] = c.Error,
        })]),
        ["jobs"] = new System.Text.Json.Nodes.JsonArray([.. state.Jobs.Select(j => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = j.Id.ToString("D"),
            ["kind"] = Wire.Text(j.Kind),
            ["args"] = j.Args,
            ["status"] = Wire.Text(j.Status),
            ["progress"] = j.Progress,
            ["createdAt"] = j.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["startedAt"] = j.StartedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["finishedAt"] = j.FinishedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        })]),
    };

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

    private static void RequireSqlServerForNativeJson(ParseResult result, Option<string?> provider, Option<bool> nativeJson)
    {
        if (result.GetValue(nativeJson) && RequireProvider(result, provider) == Postgres)
            throw new CliException("--native-json is for SQL Server; Postgres always stores jsonb.");
    }

    private sealed class Target(Option<string?> provider, Option<string> schema, Option<string?> connection)
    {
        public DeedboxProvider Open(ParseResult result, bool nativeJson = false)
        {
            var name = SchemaName.Validate(result.GetValue(schema)!);
            var connectionString = result.GetValue(connection) ?? Environment.GetEnvironmentVariable("DEEDBOX_CONNECTION");
            if (string.IsNullOrEmpty(connectionString))
                throw new CliException("Pass --connection or set DEEDBOX_CONNECTION.");
            return RequireProvider(result, provider) == Postgres
                ? new PostgresProvider(NpgsqlDataSource.Create(connectionString), ownsDataSource: true, name)
                : new SqlServerProvider(connectionString, name, nativeJson);
        }
    }
}

internal sealed class CliException(string message) : Exception(message);
