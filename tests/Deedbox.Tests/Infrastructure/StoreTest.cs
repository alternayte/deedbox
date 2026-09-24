using Microsoft.Extensions.DependencyInjection;

namespace Deedbox.Tests.Infrastructure;

/// <summary>A database test with Deedbox registered and its schema applied.</summary>
public abstract class StoreTest(Databases databases, Db db) : DatabaseTest(databases, db), IAsyncDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    protected static void DefaultStreams(DeedboxBuilder builder) => builder
        .Stream<Cart>(s => s.Events<ItemAdded, CheckedOut>())
        .Stream<Order>(s => s.Events<OrderPlaced>())
        .Stream<Counter>(s => s.Events<Incremented>());

    /// <summary>A service provider for the test schema, with the schema applied.</summary>
    protected async Task<IServiceProvider> Services(Action<DeedboxBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeedbox(b => (configure ?? DefaultStreams)(UseDatabase(b)));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        await SchemaManager.Apply(provider.GetRequiredService<DeedboxRuntime>().Provider, Ct);
        return provider;
    }

    protected async Task<IEventStore> Store(Action<DeedboxBuilder>? configure = null) => StoreFrom(await Services(configure));

    protected static IEventStore StoreFrom(IServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

    protected static string NewStreamId() => StreamId.From(Guid.NewGuid());

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
