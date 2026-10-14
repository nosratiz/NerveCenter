using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>EXPLAIN QUERY PLAN of the script's first statement (design spec §11.3), on the live file or a backup (§11.7). Not audited.</summary>
public sealed class ExplainQueryQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<ExplainQueryQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<ExplainPlan>> HandleAsync(Guid connectionId, string sql, CancellationToken ct = default) =>
        HandleAsync(connectionId, sql, null, ct);

    public Task<PluginResult<ExplainPlan>> HandleAsync(Guid connectionId, string sql, string? backupFile, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.query.explain",
            secret => operations.ExplainQueryAsync(secret, sql, backupFile, ct), ct);
}
