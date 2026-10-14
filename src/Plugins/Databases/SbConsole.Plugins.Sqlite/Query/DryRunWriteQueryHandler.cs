using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// Write mode's Dry run (design spec §11.4). A query despite the writable connection: it always
/// rolls back, so nothing durable changes and no audit row is written.
/// </summary>
public sealed class DryRunWriteQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<DryRunWriteQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<WriteScriptResult>> HandleAsync(Guid connectionId, string sql, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.query.dryrun",
            secret => operations.DryRunWriteAsync(secret, sql, ct), ct);
}
