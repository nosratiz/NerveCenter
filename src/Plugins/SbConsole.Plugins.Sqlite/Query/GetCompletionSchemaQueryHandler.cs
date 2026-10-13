using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>The Query editor's completion schema (design spec §12), from the live file or a backup (§11.7). Not audited.</summary>
public sealed class GetCompletionSchemaQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetCompletionSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<CompletionSchema>> HandleAsync(Guid connectionId, string? backupFile, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.query.completion-schema",
            secret => operations.GetCompletionSchemaAsync(secret, backupFile, ct), ct);
}
