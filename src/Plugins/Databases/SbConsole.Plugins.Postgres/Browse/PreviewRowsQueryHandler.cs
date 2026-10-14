using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed class PreviewRowsQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<PreviewRowsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgRowPage>> HandleAsync(Guid connectionId, string? database, PgRowsRequest request, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.rows.preview",
            secret => operations.PreviewRowsAsync(secret, database, request, ct), ct);
}
