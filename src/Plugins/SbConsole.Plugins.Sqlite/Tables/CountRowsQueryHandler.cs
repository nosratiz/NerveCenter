using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>Exact COUNT(*) -- a click, never a default, because it walks the whole table (mockup 1b footnote).</summary>
public sealed class CountRowsQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<CountRowsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<long>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.count",
            secret => operations.CountRowsAsync(secret, table, ct), ct);
}
