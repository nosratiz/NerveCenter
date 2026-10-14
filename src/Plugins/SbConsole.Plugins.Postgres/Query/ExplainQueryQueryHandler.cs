using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>Plain EXPLAIN (never ANALYZE): reads nothing, so it is not audited.</summary>
public sealed class ExplainQueryQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<ExplainQueryQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<string>> HandleAsync(Guid connectionId, string? database, string sql, int timeoutS, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.query.explain",
            secret => operations.ExplainAsync(secret, database, sql, timeoutS, ct), ct);
}
