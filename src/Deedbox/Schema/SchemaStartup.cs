using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deedbox;

/// <summary>Checks the schema at start-up, or applies it when the app opted in.</summary>
internal sealed partial class SchemaStartup(DeedboxRuntime runtime, ILogger<SchemaStartup> logger) : IHostedService
{
    private readonly ILogger _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (runtime.Options.ApplySchemaOnStartup)
        {
            var (from, to) = await SchemaManager.Apply(runtime.Provider, cancellationToken);
            if (from != to)
                LogApplied(runtime.Provider.Schema, from, to);
            return;
        }

        await SchemaManager.Verify(runtime.Provider, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Deedbox schema '{Schema}' migrated from version {From} to {To}.")]
    private partial void LogApplied(string schema, int from, int to);
}
