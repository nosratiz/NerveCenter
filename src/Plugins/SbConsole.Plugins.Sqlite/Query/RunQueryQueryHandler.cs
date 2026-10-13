using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// The Query page's Run (design spec §11.3). A query, not audited: read-only, the same data the
/// Tables preview shows. "Load 100 more" re-runs with a larger limit.
/// </summary>
public sealed class RunQueryQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<RunQueryQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<QueryResult>> HandleAsync(Guid connectionId, string sql, int limit = QueryResult.DefaultLimit, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.query.run",
            secret => operations.RunQueryAsync(secret, sql, limit, ct), ct);
}
