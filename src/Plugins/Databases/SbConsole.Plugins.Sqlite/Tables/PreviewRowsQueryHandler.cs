using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class PreviewRowsQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<PreviewRowsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<RowPage>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.preview",
            secret => operations.PreviewRowsAsync(secret, table, RowPage.PreviewLimit, ct), ct);
}
