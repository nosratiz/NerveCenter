using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetTableSizesQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetTableSizesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<TableSizes>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.overview.sizes",
            secret => operations.GetTableSizesAsync(secret, ct), ct);
}
