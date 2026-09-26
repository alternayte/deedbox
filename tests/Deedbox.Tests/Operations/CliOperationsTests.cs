using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox.Cli;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.PersonalData;
using Deedbox.Tests.Runner;

namespace Deedbox.Tests.Operations;

public sealed class PostgresCliOperationsTests(Databases databases) : CliOperationsTests(databases, Db.Postgres);

public sealed class SqlServerCliOperationsTests(Databases databases) : CliOperationsTests(databases, Db.SqlServer);

public abstract class CliOperationsTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    private string[] Target => ["--provider", Db == Db.Postgres ? "postgres" : "sqlserver", "--schema", Schema, "--connection", ConnectionString];

    [Fact]
    public async Task Retire_refuses_while_an_app_registers_the_projection_and_retires_it_after()
    {
        var host = await StartHost(NewProbe(), b => b.Projection<AsyncApplied>("applied", Run.Async));

        var refused = await Cli(["retire", "applied", .. Target]);
        Assert.Equal(1, refused.Code);
        Assert.StartsWith("DBX035:", refused.Error, StringComparison.Ordinal);

        await StopHost(host);
        var retired = await Cli(["retire", "applied", .. Target]);
        Assert.Equal((0, "'applied' is retired. Rebuild it to use it again."), (retired.Code, retired.Output.Trim()));
        Assert.Contains("retired", (await Cli(["status", .. Target])).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_rebuild_and_skip_work_against_a_running_app()
    {
        var probe = NewProbe();
        probe.PoisonSku = "poison";
        var host = await StartHost(probe, b => b.Projection<AsyncApplied>("applied", Run.Async));
        await StoreOf(host).Append("cart-1", ExpectedVersion.Any, [new ItemAdded("a", 1), new ItemAdded("poison", 1)]);
        await WaitForCheckpoint(host, "applied", r => r.Status == "stalled");

        var (code, text, _) = await Cli(["status", .. Target]);
        Assert.Equal(0, code);
        Assert.Contains("applied", text, StringComparison.Ordinal);
        Assert.Contains("is stalled (poison)", text, StringComparison.Ordinal);
        Assert.Contains("stream 'cart-1' version 2", text, StringComparison.Ordinal);
        Assert.Contains("3 attempts; the next retry is at", text, StringComparison.Ordinal);
        var eventId = JsonDocument.Parse((await Cli(["status", "--json", .. Target])).Output).RootElement
            .GetProperty("consumers")[0].GetProperty("error").GetString()!;
        var poison = JsonNode.Parse(eventId)!["eventId"]!.GetValue<string>();

        Assert.Equal(0, (await Cli(["skip", "applied", "--event", poison, "--wait", .. Target])).Code);
        await WaitForCaughtUp(host, "applied");
        probe.PoisonSku = null;
        var rebuild = await Cli(["rebuild", "applied", "--wait", .. Target]);
        Assert.Equal(0, rebuild.Code);
        Assert.Contains("rebuild job", rebuild.Output, StringComparison.Ordinal);
        await WaitForCaughtUp(host, "applied");
        Assert.Equal(1, probe.Resets);
    }

    [Fact]
    public async Task Erase_deletes_the_key_at_once_and_snapshots_rebuild_queues_for_the_app()
    {
        var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.StoreInDatabase()); });
        await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", null)]);

        var erase = await Cli(["erase", "person:1", .. Target]);
        Assert.Equal(0, erase.Code);
        Assert.Equal(0, await Scalar<int>($"SELECT COUNT(*) FROM {Table("subject_keys")}"));
        Assert.Equal([null], (await StoreOf(host).Load<Manuscript>("m-1")).State.Names);

        Assert.Equal(0, (await Cli(["snapshots", "rebuild", "manuscript", "--wait", .. Target])).Code);
        Assert.Equal(1, (await Cli(["snapshots", "rebuild", "nope", "--wait", .. Target])).Code);
    }

    [Fact]
    public async Task Keys_rewrap_moves_tenant_keys_between_master_keys_and_tenant_shred_needs_confirmation()
    {
        var (fromVariable, toVariable) = ($"DEEDBOX_CLI_FROM_{Guid.NewGuid():N}", $"DEEDBOX_CLI_TO_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(fromVariable, Keys.Ring($"{Schema}-from"));
        Environment.SetEnvironmentVariable(toVariable, Keys.Ring($"{Schema}-to"));
        try
        {
            var host = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromEnvironment(fromVariable)); });
            await StoreOf(host).Append("m-1", ExpectedVersion.NoStream, [new ReviewerInvited("m-1", "person:1", "Ada", null)]);
            await StopHost(host);

            var rewrap = await Cli(["keys", "rewrap", "--from", $"env:{fromVariable}", "--to", $"env:{toVariable}", .. Target]);
            Assert.Equal(0, rewrap.Code);
            Assert.Contains("Re-wrapped 1 tenant keys", rewrap.Output, StringComparison.Ordinal);

            var moved = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromEnvironment(toVariable)); });
            Assert.Equal(["Ada"], (await StoreOf(moved).Load<Manuscript>("m-1")).State.Names);

            var unconfirmed = await Cli(["tenant", "shred", "", .. Target]);
            Assert.Equal(1, unconfirmed.Code);
            Assert.Contains("--yes", unconfirmed.Error, StringComparison.Ordinal);
            Assert.Equal(0, (await Cli(["tenant", "shred", "", "--yes", .. Target])).Code);
            await StopHost(moved);
            var after = await StartHost(NewProbe(), b => { Keys.Streams(b); b.Keys(k => k.FromEnvironment(toVariable)); });
            Assert.Equal([null], (await StoreOf(after).Load<Manuscript>("m-1")).State.Names);
        }
        finally
        {
            Environment.SetEnvironmentVariable(fromVariable, null);
            Environment.SetEnvironmentVariable(toVariable, null);
        }
    }

    [Fact]
    public async Task Commands_that_need_a_database_say_how_to_reach_it()
    {
        var (code, _, error) = await Cli(["status", "--provider", "postgres"]);

        Assert.Equal(1, code);
        Assert.Contains("DEEDBOX_CONNECTION", error, StringComparison.Ordinal);
    }

    protected static async Task<(int Code, string Output, string Error)> Cli(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}

public sealed class CliLockfileTests
{
    [Fact]
    public async Task Lockfile_diff_lists_changes_and_fails_on_a_break()
    {
        var folder = Directory.CreateTempSubdirectory("deedbox-diff-");
        try
        {
            var old = Path.Combine(folder.FullName, "old.lock");
            var added = Path.Combine(folder.FullName, "added.lock");
            var broken = Path.Combine(folder.FullName, "broken.lock");
            await File.WriteAllTextAsync(old, "stream cart (Cart)\n  cart.item_added v1 { qty: int32, sku: string }\n");
            await File.WriteAllTextAsync(added, "stream cart (Cart)\n  cart.item_added v1 { note: string?, qty: int32, sku: string }\n    alias cart.line_added\n  cart.checked_out v1 { at: date-time }\n");
            await File.WriteAllTextAsync(broken, "stream cart (Cart)\n  cart.checked_out v1 { at: date-time }\n");

            var same = await Run(["lockfile", "diff", old, old]);
            var compatible = await Run(["lockfile", "diff", old, added]);
            var breaking = await Run(["lockfile", "diff", old, broken]);

            Assert.Equal((0, "No event-contract changes."), (same.Code, same.Output.Trim()));
            Assert.Equal(0, compatible.Code);
            Assert.Contains("+ cart.checked_out v1 { at: date-time }", compatible.Output, StringComparison.Ordinal);
            Assert.Contains("+ cart.item_added alias cart.line_added", compatible.Output, StringComparison.Ordinal);
            Assert.Contains("~ cart.item_added { qty: int32, sku: string } -> { note: string?, qty: int32, sku: string }", compatible.Output, StringComparison.Ordinal);
            Assert.Equal(1, breaking.Code);
            Assert.Contains("- cart.item_added v1", breaking.Output, StringComparison.Ordinal);
            Assert.Contains("BREAK 'cart.item_added' was removed", breaking.Error, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static async Task<(int Code, string Output, string Error)> Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
