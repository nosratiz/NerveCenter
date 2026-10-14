using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class ListSchemaQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<ListSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<SchemaListing>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.schema.list",
            secret => operations.ListSchemaAsync(secret, ct), ct);
}
