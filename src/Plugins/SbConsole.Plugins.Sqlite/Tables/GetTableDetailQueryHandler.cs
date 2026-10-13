using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class GetTableDetailQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetTableDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<TableDetail>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.detail",
            secret => operations.GetTableDetailAsync(secret, table, ct), ct);
}
