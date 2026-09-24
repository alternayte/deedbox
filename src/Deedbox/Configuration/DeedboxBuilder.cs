namespace Deedbox;

/// <summary>Configures Deedbox inside <c>AddDeedbox</c>.</summary>
public sealed class DeedboxBuilder
{
    private Func<string, DeedboxProvider>? _provider;
    private string _schema = SchemaName.Default;
    private bool _applySchemaOnStartup;

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

        var options = new DeedboxOptions(_schema, _applySchemaOnStartup);
        return new DeedboxRuntime(options, _provider(_schema));
    }
}

internal sealed record DeedboxOptions(string Schema, bool ApplySchemaOnStartup);

/// <summary>Everything a store needs that lives for the life of the app.</summary>
internal sealed class DeedboxRuntime(DeedboxOptions options, DeedboxProvider provider) : IAsyncDisposable
{
    public DeedboxOptions Options { get; } = options;
    public DeedboxProvider Provider { get; } = provider;

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
