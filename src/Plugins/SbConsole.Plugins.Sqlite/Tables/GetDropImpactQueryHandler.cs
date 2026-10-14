using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>What the Drop table dialog shows (mockup 1f, spec §11.5). A query: read-only, never audited.</summary>
public sealed class GetDropImpactQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetDropImpactQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<DropImpact>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, $"sqlite.table.drop-impact {table}",
            secret => operations.GetDropImpactAsync(secret, table, ct), ct);
}
