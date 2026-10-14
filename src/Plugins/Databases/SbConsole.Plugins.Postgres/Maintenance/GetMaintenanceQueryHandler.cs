using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Maintenance;

public sealed class GetMaintenanceQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetMaintenanceQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgMaintenance>> HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, $"maintenance on {database ?? "(default)"}",
            secret => operations.GetMaintenanceAsync(secret, database, ct), ct);
}
