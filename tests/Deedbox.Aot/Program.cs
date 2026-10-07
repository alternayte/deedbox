using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Appends, loads and erases through a native AOT binary. The connection string is in DEEDBOX_AOT_CONNECTION.
var connectionString = Environment.GetEnvironmentVariable("DEEDBOX_AOT_CONNECTION")
    ?? throw new InvalidOperationException("Set DEEDBOX_AOT_CONNECTION.");

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddDeedbox(b => b
    .UsePostgres(connectionString)
    .Schema("aot")
    .ApplySchemaOnStartup()
    .UseJsonContext(AotJson.Default)
    .Keys(k => k.FromKeyRing($"v1:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}"))
    .Stream<Person>(s => s.Events<PersonNamed, PersonMoved>())
    .Projection<Names>("names", Run.Inline)
    .Runner(r => r.MinPollDelay = TimeSpan.FromMilliseconds(20)));

using var host = builder.Build();
await host.StartAsync();
using var scope = host.Services.CreateScope();
var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
var id = StreamId.From(Guid.NewGuid());
var subject = $"person:{Guid.NewGuid():N}";

await store.Append(id, ExpectedVersion.NoStream, [new PersonNamed(subject, "Ada Lovelace")]);
var moved = await store.Execute<Person>(id, person => person.Name is null ? [] : [new PersonMoved("London")]);
Require(moved.Version == 2 && moved.State == new Person("Ada Lovelace", "London"), $"Execute returned {moved.State} at version {moved.Version}.");
Require((await store.Load<Person>(id)).State.Name == "Ada Lovelace", "The load did not decrypt the name.");
Require(Names.Seen.Contains("Ada Lovelace"), "The inline projection did not see the event.");

var job = await scope.ServiceProvider.GetRequiredService<ISubjectErasure>().EraseSubjectAsync(subject);
Require((await store.Load<Person>(id)).State.Name is null, "The name is still readable after the erasure.");

var admin = host.Services.GetRequiredService<IEventStoreAdmin>();
var deadline = DateTime.UtcNow.AddSeconds(60);
while ((await admin.GetJobAsync(job))?.FinishedAt is null)
{
    Require(DateTime.UtcNow < deadline, "The erasure job did not finish.");
    await Task.Delay(50);
}

var erased = await store.Load<Person>(id);
Require(erased.Version == 3 && erased.State == new Person(null, "London"), $"After the erasure job the stream is {erased.State} at version {erased.Version}.");

await host.StopAsync();
Console.WriteLine("Native AOT: append, execute, load, inline projection and erasure work.");
return 0;

static void Require(bool condition, string failure)
{
    if (!condition)
        throw new InvalidOperationException(failure);
}

public record PersonNamed([property: DataSubject] string PersonId, [property: PersonalData] string? Name);

public record PersonMoved(string City);

public record Person(string? Name, string? City) : IState<Person>
{
    public static Person Initial { get; } = new(null, null);

    public static Person Evolve(Person s, object e) => e switch
    {
        PersonNamed named => s with { Name = named.Name },
        PersonMoved moved => s with { City = moved.City },
        _ => s,
    };
}

public sealed class Names : Projection
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Seen = [];

    public Names() => On<PersonNamed>((e, _) =>
    {
        if (e.Name is not null)
            Seen.Add(e.Name);
        return Task.CompletedTask;
    });
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PersonNamed))]
[JsonSerializable(typeof(PersonMoved))]
[JsonSerializable(typeof(Person))]
internal sealed partial class AotJson : JsonSerializerContext;
