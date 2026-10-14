using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetOverviewQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetOverviewQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<SqliteOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.overview.get",
            secret => operations.GetOverviewAsync(secret, ct), ct);
}
