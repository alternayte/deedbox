using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Deedbox;

/// <summary>Configures Deedbox inside <c>AddDeedbox</c>.</summary>
public sealed class DeedboxBuilder
{
    private readonly List<StreamRegistration> _streams = [];
    private readonly List<IJsonTypeInfoResolver> _jsonContexts = [];
    private readonly List<ProjectionRegistration> _projections = [];
    private readonly List<SubscriptionRegistration> _subscriptions = [];
    private readonly RunnerOptions _runner = new();
    private readonly KeysBuilder _keys = new();
    private readonly List<Action<IServiceCollection>> _services = [];
    private Action<JsonSerializerOptions>? _configureJson;
    private Func<string, IServiceProvider, DeedboxProvider>? _provider;
    private string _schema = SchemaName.Default;
    private bool _applySchemaOnStartup;
    private int _executeRetries = 3;

    internal DeedboxBuilder()
    {
    }

    /// <summary>
    /// Puts the Deedbox tables in <paramref name="name"/> instead of <c>deedbox</c>.
    /// Use lower-case letters, digits and underscores.
    /// </summary>
    public DeedboxBuilder Schema(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _schema = SchemaName.Validate(name);
        return this;
    }

    /// <summary>
    /// Applies pending schema migrations when the host starts, under a database lock so concurrent
    /// instances apply them once. Without this, start-up fails when the schema is behind.
    /// </summary>
    public DeedboxBuilder ApplySchemaOnStartup()
    {
        _applySchemaOnStartup = true;
        return this;
    }

    /// <summary>
    /// Registers a stream type named after <typeparamref name="TState"/> in snake case, so <c>Cart</c> becomes <c>cart</c>.
    /// </summary>
    /// <param name="configure">Registers the stream's events and settings.</param>
    /// <typeparam name="TState">The stream's state type.</typeparam>
    public DeedboxBuilder Stream<TState>(Action<StreamBuilder<TState>> configure) where TState : IState<TState> =>
        Stream(Naming.StreamType(typeof(TState)), configure);

    /// <summary>Registers a stream type with an explicit stored name.</summary>
    /// <param name="name">The stored stream type name.</param>
    /// <param name="configure">Registers the stream's events and settings.</param>
    /// <typeparam name="TState">The stream's state type.</typeparam>
    public DeedboxBuilder Stream<TState>(string name, Action<StreamBuilder<TState>> configure) where TState : IState<TState>
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var stream = new StreamRegistration(name, typeof(TState), static () => TState.Initial, static (s, e) => TState.Evolve((TState)s, e));
        configure(new StreamBuilder<TState>(stream));
        _streams.Add(stream);
        return this;
    }

    /// <summary>
    /// Changes Deedbox's JSON options, which start from the web defaults (camelCase). The app's global
    /// JSON options never apply to stored events.
    /// </summary>
    /// <param name="configure">Changes the options before Deedbox freezes them.</param>
    public DeedboxBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureJson += configure;
        return this;
    }

    /// <summary>
    /// Uses source-generated JSON contracts, for trimmed and native AOT apps. The context must include
    /// every event and state type. Call it again to add more contexts.
    /// </summary>
    /// <param name="context">A <see cref="JsonSerializerContext"/>, such as <c>MyEventsJsonContext.Default</c>.</param>
    public DeedboxBuilder UseJsonContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _jsonContexts.Add(context);
        return this;
    }

    /// <summary>
    /// How many times <see cref="IEventStore.Execute"/> retries after a concurrency conflict; 3 by default.
    /// Retries are safe because the decision is pure.
    /// </summary>
    /// <param name="retries">The retry count, 0 or more.</param>
    public DeedboxBuilder ExecuteRetries(int retries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);
        _executeRetries = retries;
        return this;
    }

    /// <summary>
    /// Registers a projection under a stored name. The name keys its checkpoint, so renaming the class keeps its
    /// progress. Each projection has one run mode; registering a class twice fails.
    /// </summary>
    /// <param name="name">The stored projection name, such as <c>cart_summary</c>.</param>
    /// <param name="run">Inline, in the append's transaction; or Async, in the background runner.</param>
    /// <typeparam name="TProjection">A <see cref="Projection"/> or EF Core <c>Projection&lt;TDbContext&gt;</c>.</typeparam>
    public DeedboxBuilder Projection<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProjection>(string name, Run run)
        where TProjection : ProjectionBase
    {
        ArgumentNullException.ThrowIfNull(name);
        _projections.Add(ProjectionSet.Registration<TProjection>(name, run));
        return this;
    }

    /// <summary>
    /// Registers a subscription under a stored name, which keys its checkpoint. It runs in the background runner
    /// after events commit, and delivers each event at least once.
    /// </summary>
    /// <param name="name">The stored subscription name, such as <c>receipt_email</c>.</param>
    /// <typeparam name="TSubscription">The subscription.</typeparam>
    public DeedboxBuilder Subscription<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSubscription>(string name)
        where TSubscription : Subscription
    {
        ArgumentNullException.ThrowIfNull(name);
        _subscriptions.Add(new SubscriptionRegistration(name, typeof(TSubscription), services => ActivatorUtilities.CreateInstance<TSubscription>(services)));
        return this;
    }

    /// <summary>
    /// Chooses where the master key for personal data lives, such as <c>keys =&gt; keys.StoreInDatabase()</c>. Required
    /// when any registered event has [PersonalData]; start-up fails without it.
    /// </summary>
    /// <param name="configure">Chooses the key mode.</param>
    public DeedboxBuilder Keys(Action<KeysBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_keys);
        return this;
    }

    /// <summary>Changes the background runner's settings.</summary>
    /// <param name="configure">Changes the settings.</param>
    public DeedboxBuilder Runner(Action<RunnerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_runner);
        _runner.Validate();
        return this;
    }

    /// <summary>
    /// Runs <typeparamref name="THook"/> inside every append's transaction, such as to write outbox rows.
    /// It is resolved from the append's scope.
    /// </summary>
    /// <typeparam name="THook">The hook.</typeparam>
    public DeedboxBuilder OnAppending<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>()
        where THook : class, IAppendingHook
    {
        _services.Add(s => s.TryAddEnumerable(ServiceDescriptor.Scoped<IAppendingHook, THook>()));
        return this;
    }

    private readonly List<Action<EventRegistry>> _checks = [];

    /// <summary>Lets an extension package register services with AddDeedbox.</summary>
    internal void AddServices(Action<IServiceCollection> register) => _services.Add(register);

    /// <summary>Lets an extension package check its configuration against the built runtime, so mistakes fail at start-up.</summary>
    internal void AddCheck(Action<EventRegistry> check) => _checks.Add(check);

    internal void RegisterServices(IServiceCollection services)
    {
        foreach (var register in _services)
            register(services);
    }

    internal void UseProvider(Func<string, IServiceProvider, DeedboxProvider> factory)
    {
        if (_provider is not null)
            throw new DeedboxException(Errors.ProviderAlreadySet, "A database provider is already configured. Call UsePostgres or UseSqlServer once.");
        _provider = factory;
    }

    private void ValidateProjections()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<Type>();
        foreach (var subscription in _subscriptions)
        {
            EventRegistry.ValidateName(subscription.Name, "Subscription");
            if (!names.Add(subscription.Name))
                throw new DeedboxException(Errors.DuplicateProjection, $"Subscription name '{subscription.Name}' is registered twice. Each subscription needs its own name.");
            if (!types.Add(subscription.Type))
                throw new DeedboxException(Errors.DuplicateProjection, $"{subscription.Type.Name} is registered twice. Register a subscription once, under one name.");
        }

        foreach (var projection in _projections)
        {
            EventRegistry.ValidateName(projection.Name, "Projection");
            if (!names.Add(projection.Name))
                throw new DeedboxException(Errors.DuplicateProjection, $"Name '{projection.Name}' is registered twice. Each projection and subscription needs its own name.");
            if (!types.Add(projection.Type))
            {
                throw new DeedboxException(Errors.DuplicateProjection,
                    $"{projection.Type.Name} is registered twice. A projection has one name and one run mode; running it both inline and async applies events twice.");
            }
        }
    }

    /// <summary>The registry and JSON options alone, without a provider, for tools such as the lockfile.</summary>
    internal (EventRegistry Registry, DeedboxJson Json) BuildRegistry()
    {
        var json = new DeedboxJson(_jsonContexts, _configureJson);
        return (new EventRegistry(_streams, json, keysConfigured: _keys.Factory is not null), json);
    }

    /// <summary>
    /// Validates the configuration now, so a mistake fails AddDeedbox. Returns the runtime factory; the provider is
    /// created when the runtime is first resolved, so it can read configuration that is final only then.
    /// </summary>
    internal Func<IServiceProvider, DeedboxRuntime> Build()
    {
        if (_provider is null)
            throw new DeedboxException(Errors.NoProvider, "No database provider is configured. Call UsePostgres(...) or UseSqlServer(...) in AddDeedbox.");

        var (registry, json) = BuildRegistry();
        ValidateProjections();
        foreach (var check in _checks)
            check(registry);

        var options = new DeedboxOptions(_schema, _applySchemaOnStartup, _executeRetries, _projections, _subscriptions, _runner, _keys.Placeholder);
        var (createProvider, createKeys, schema) = (_provider, _keys.Factory, _schema);
        return services =>
        {
            var provider = createProvider(schema, services);
            var keys = createKeys is { } factory ? new KeyRing(provider, factory(provider)) : null;
            return new DeedboxRuntime(options, provider, registry, json) { Keys = keys };
        };
    }
}

internal sealed record DeedboxOptions(
    string Schema,
    bool ApplySchemaOnStartup,
    int ExecuteRetries,
    IReadOnlyList<ProjectionRegistration> Projections,
    IReadOnlyList<SubscriptionRegistration> Subscriptions,
    RunnerOptions Runner,
    string? RedactedPlaceholder);

/// <summary>Everything a store needs that lives for the life of the app.</summary>
internal sealed class DeedboxRuntime(DeedboxOptions options, DeedboxProvider provider, EventRegistry registry, DeedboxJson json) : IAsyncDisposable
{
    public DeedboxOptions Options { get; } = options;
    public DeedboxProvider Provider { get; } = provider;
    public EventRegistry Registry { get; } = registry;
    public DeedboxJson Json { get; } = json;
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>This process's ID in the heartbeat table.</summary>
    public Guid InstanceId { get; } = Uuid7.New();

    /// <summary>The key hierarchy, or null when no key mode is configured.</summary>
    public KeyRing? Keys { get; init; }

    public KeyRing RequireKeys() => Keys ?? throw new DeedboxException(Errors.NoKeyMode,
        "Stored events hold encrypted personal data, but no key mode is configured. Add .Keys(keys => ...) with the mode that encrypted them.");

    /// <summary>Event types known to be committed in event_types, so appends skip recording them.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<EventTypeRow, bool> KnownEventTypes { get; } = new();

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
