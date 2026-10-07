using System.Collections.Immutable;
using System.Data.Common;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage: <write|append> <postgres|sqlserver> <schema>; the connection string is in DEEDBOX_OLD_CONNECTION.
// Only the API that every release since 0.1.0 has is used here.
var (phase, provider, schema) = (args[0], args[1], args[2]);
var connectionString = Environment.GetEnvironmentVariable("DEEDBOX_OLD_CONNECTION")
    ?? throw new InvalidOperationException("Set DEEDBOX_OLD_CONNECTION.");

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(new Target(provider, schema));
builder.Services.AddDeedbox(b =>
{
    (provider == "postgres" ? b.UsePostgres(connectionString) : b.UseSqlServer(connectionString)).Schema(schema);

    // "write" is the old release on its own schema. "append" is the old release on a schema this build migrated.
    if (phase == "write")
        b.ApplySchemaOnStartup();
    b.Keys(k => k.StoreInDatabase())
        .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
        .Stream<Person>(s => s.Events<PersonNamed>())
        .Projection<Seen>("seen", Run.Async)
        .Runner(r => r.MinPollDelay = TimeSpan.FromMilliseconds(20));
});

using var host = builder.Build();
await host.StartAsync();
var store = host.Services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

if (phase == "write")
{
    await store.Append("cart-1", ExpectedVersion.NoStream, [new ItemAdded("apple", 2), new ItemAdded("pear", 1)]);
    await store.Append("cart-1", ExpectedVersion.Exact(2), [new CheckedOut(DateTimeOffset.UnixEpoch)]);
    await store.Append("cart-2", ExpectedVersion.NoStream, [new ItemAdded("plum", 5)]);
    await store.Append("person-1", ExpectedVersion.NoStream, [new PersonNamed("person:1", "Ada Lovelace")]);
    await store.Append("person-2", ExpectedVersion.NoStream, [new PersonNamed("person:2", "Grace Hopper")]);
}
else
{
    await store.Append("cart-2", ExpectedVersion.Any, [new ItemAdded("fig", 1)]);
    var person = await store.Load<Person>("person-2");
    if (person.State.Name != "Grace Hopper")
        throw new InvalidOperationException($"The old release read '{person.State.Name}' for person-2.");
}

// In "write", the old release's projection must apply its events before it stops, so its checkpoint is part of what is
// upgraded. In "append", a newer instance shares the projection and may apply the event first; the test counts the rows.
var deadline = DateTime.UtcNow.AddSeconds(60);
while (phase == "write" && Seen.Cart2 < 5)
{
    if (DateTime.UtcNow > deadline)
        throw new TimeoutException("The old release's projection did not apply this run's events.");
    await Task.Delay(50);
}

await host.StopAsync();
return 0;

public sealed record Target(string Provider, string Schema);

public record ItemAdded(string Sku, int Qty);

public record CheckedOut(DateTimeOffset At);

public record Cart(ImmutableDictionary<string, int> Items, bool IsCheckedOut, int Changes) : IState<Cart>
{
    public static Cart Initial { get; } = new(ImmutableDictionary<string, int>.Empty, false, 0);

    public static Cart Evolve(Cart s, object e) => e switch
    {
        ItemAdded x => s with { Items = s.Items.SetItem(x.Sku, s.Items.GetValueOrDefault(x.Sku) + x.Qty), Changes = s.Changes + 1 },
        CheckedOut => s with { IsCheckedOut = true, Changes = s.Changes + 1 },
        _ => s,
    };
}

public record PersonNamed([property: DataSubject] string PersonId, [property: PersonalData] string? Name);

public record Person(string? Name) : IState<Person>
{
    public static Person Initial { get; } = new((string?)null);

    public static Person Evolve(Person s, object e) => e is PersonNamed named ? new Person(named.Name) : s;
}

/// <summary>Counts cart-2's quantity in memory and writes one row per applied event, as the tests' own projection does.</summary>
public sealed class Seen : Projection
{
    public static int Cart2;

    public Seen(Target target) => On<ItemAdded>(async (e, ctx) =>
    {
        await using var command = ctx.Connection.CreateCommand();
        command.Transaction = ctx.Transaction;
        command.CommandText = target.Provider == "postgres"
            ? $"CREATE TABLE IF NOT EXISTS {target.Schema}.seen (event_id text PRIMARY KEY); INSERT INTO {target.Schema}.seen VALUES (@id)"
            : $"IF OBJECT_ID('[{target.Schema}].[seen]') IS NULL CREATE TABLE [{target.Schema}].[seen] (event_id nvarchar(40) PRIMARY KEY); INSERT INTO [{target.Schema}].[seen] VALUES (@id)";
        var id = command.CreateParameter();
        id.ParameterName = "id";
        id.Value = ctx.EventId.ToString();
        command.Parameters.Add(id);
        await command.ExecuteNonQueryAsync();
        if (ctx.StreamId == "cart-2")
            Interlocked.Add(ref Cart2, e.Qty);
    });
}
