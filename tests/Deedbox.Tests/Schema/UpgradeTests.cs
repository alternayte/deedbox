using System.Collections.Concurrent;
using System.Diagnostics;
using Deedbox.Tests.Infrastructure;
using Deedbox.Tests.Projections;
using Deedbox.Tests.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Schema;

public sealed class PostgresUpgradeTests(Databases databases) : UpgradeTests(databases, Db.Postgres);

public sealed class SqlServerUpgradeTests(Databases databases) : UpgradeTests(databases, Db.SqlServer);

// The same stored names as tests/Deedbox.OldVersion writes: person, person.person_named.
public record PersonNamed([property: DataSubject] string PersonId, [property: PersonalData] string? Name);

public record Person(string? Name) : IState<Person>
{
    public static Person Initial { get; } = new((string?)null);

    public static Person Evolve(Person s, object e) => e is PersonNamed named ? new Person(named.Name) : s;
}

/// <summary>The same projection as the old release's: one row per applied event, keyed by event ID.</summary>
public sealed class Seen : Projection
{
    public Seen(Probe probe) => On<ItemAdded>((_, ctx) => TestTables.Insert(ctx.Connection, ctx.Transaction,
        $"INSERT INTO {probe.Table("seen")} (event_id) VALUES (@id)", ("id", ctx.EventId.ToString())));
}

/// <summary>
/// "The storage schema never breaks": a store that a released package wrote, with its own schema version, is migrated
/// by this build and still reads, appends, projects and erases. Then the released package runs against the migrated
/// schema, as an old pod does during a rolling deploy.
/// </summary>
public abstract class UpgradeTests(Databases databases, Db db) : RunnerTest(databases, db)
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.2.1")]
    [InlineData("0.3.1")]
    [InlineData("0.4.1")]
    public async Task A_store_that_a_release_wrote_upgrades_and_the_release_still_runs_on_it(string version)
    {
        await OldVersion.Run(version, "write", Db, ConnectionString, Schema);

        var probe = NewProbe();
        var host = await StartHost(probe, b => b
            .Keys(k => k.StoreInDatabase())
            .Stream<Person>(s => s.Events<PersonNamed>())
            .Projection<Seen>("seen", Run.Async));
        var store = StoreOf(host);

        Assert.Equal(RuntimeOf(host).Provider.LatestSchemaVersion, await Scalar<int>($"SELECT MAX(version) FROM {Table("schema_version")}"));
        var cart = await store.Load<Cart>("cart-1");
        Assert.Equal((3L, true, 2, 1), (cart.Version, cart.State.IsCheckedOut, cart.State.Items["apple"], cart.State.Items["pear"]));
        Assert.Equal("Ada Lovelace", (await store.Load<Person>("person-1")).State.Name);

        Assert.Equal(4, (await store.Append("cart-1", ExpectedVersion.Exact(3), [new ItemAdded("kiwi", 1)])).Version);
        await WaitForCaughtUp(host, "seen");
        Assert.Equal(4, await Scalar<int>($"SELECT COUNT(*) FROM {Table("seen")}"));

        // The old release appends, reads personal data and runs the shared projection on the migrated schema.
        await OldVersion.Run(version, "append", Db, ConnectionString, Schema);
        Assert.Equal(6, (await store.Load<Cart>("cart-2")).State.Items.Values.Sum());
        await WaitForCaughtUp(host, "seen");
        Assert.Equal(5, await Scalar<int>($"SELECT COUNT(*) FROM {Table("seen")}"));

        var erasure = await host.Services.CreateScope().ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync("person:1");
        Assert.Equal("done", (await WaitForJob(host, erasure)).Status);
        Assert.Null((await store.Load<Person>("person-1")).State.Name);
        Assert.Equal("Grace Hopper", (await store.Load<Person>("person-2")).State.Name);
    }
}

/// <summary>Builds tests/Deedbox.OldVersion against one released Deedbox from nuget.org, once per test run, and runs it.</summary>
internal static class OldVersion
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Builds = new();

    public static async Task Run(string version, string phase, Db db, string connectionString, string schema)
    {
        var app = await Builds.GetOrAdd(version, v => new Lazy<Task<string>>(() => Build(v))).Value;
        var (exit, output) = await Dotnet([app, phase, db == Db.Postgres ? "postgres" : "sqlserver", schema], connectionString);
        Assert.True(exit == 0, $"Deedbox {version} failed in phase '{phase}':{Environment.NewLine}{output}");
    }

    private static async Task<string> Build(string version)
    {
        var root = Path.GetFullPath(Path.Combine(Testing.EventContracts.CallerDirectory(SourceFile()), "..", "..", ".."));

        // One output folder per release and per test process, so the two target frameworks' runs never share a build.
        var artifacts = Path.Combine(root, "artifacts", "old-version", $"{version}-net{Environment.Version.Major}");
        var (exit, output) = await Dotnet(
            ["build", Path.Combine(root, "tests", "Deedbox.OldVersion"), "-c", "Release", "--nologo", "-v", "quiet", $"-p:DeedboxVersion={version}", $"-p:ArtifactsPath={artifacts}"], null);
        Assert.True(exit == 0, $"Deedbox.OldVersion did not build against Deedbox {version}:{Environment.NewLine}{output}");
        return Path.Combine(artifacts, "bin", "Deedbox.OldVersion", "release_net8.0", "Deedbox.OldVersion.dll");
    }

    private static async Task<(int Exit, string Output)> Dotnet(string[] arguments, string? connectionString)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (connectionString is not null)
            start.Environment["DEEDBOX_OLD_CONNECTION"] = connectionString;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
}
