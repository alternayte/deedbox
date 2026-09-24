using Deedbox;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Deedbox with a service collection.</summary>
public static class DeedboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds Deedbox. Configuration errors, such as a missing provider, throw here so the app fails at start-up.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Chooses the provider and registers stream types.</param>
    public static IServiceCollection AddDeedbox(this IServiceCollection services, Action<DeedboxBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DeedboxBuilder();
        configure(builder);
        var runtime = builder.Build();

        services.AddSingleton(_ => runtime);
        services.AddScoped<IEventStore>(sp => new EventStore(sp.GetRequiredService<DeedboxRuntime>(), OwnedTransactions.Instance));
        services.AddHostedService<DeedboxStartup>();
        return services;
    }
}
