using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Deedbox;

/// <summary>Configures Deedbox inside <c>AddDeedbox</c>.</summary>
public sealed class DeedboxBuilder
{
    private readonly List<StreamRegistration> _streams = [];
    private readonly List<IJsonTypeInfoResolver> _jsonContexts = [];
    private Action<JsonSerializerOptions>? _configureJson;
    private Func<string, DeedboxProvider>? _provider;
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

    internal void UseProvider(Func<string, DeedboxProvider> factory)
    {
        if (_provider is not null)
            throw new DeedboxException(Errors.ProviderAlreadySet, "A database provider is already configured. Call UsePostgres or UseSqlServer once.");
        _provider = factory;
    }

    internal DeedboxRuntime Build()
    {
        if (_provider is null)
            throw new DeedboxException(Errors.NoProvider, "No database provider is configured. Call UsePostgres(...) or UseSqlServer(...) in AddDeedbox.");

        var json = new DeedboxJson(_jsonContexts, _configureJson);
        var registry = new EventRegistry(_streams, json);
        var options = new DeedboxOptions(_schema, _applySchemaOnStartup, _executeRetries);
        return new DeedboxRuntime(options, _provider(_schema), registry, json);
    }
}

internal sealed record DeedboxOptions(string Schema, bool ApplySchemaOnStartup, int ExecuteRetries);

/// <summary>Everything a store needs that lives for the life of the app.</summary>
internal sealed class DeedboxRuntime(DeedboxOptions options, DeedboxProvider provider, EventRegistry registry, DeedboxJson json) : IAsyncDisposable
{
    public DeedboxOptions Options { get; } = options;
    public DeedboxProvider Provider { get; } = provider;
    public EventRegistry Registry { get; } = registry;
    public DeedboxJson Json { get; } = json;
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
