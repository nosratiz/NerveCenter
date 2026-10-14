using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed class ListSchemaQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<ListSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgSchemaTree>> HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.schema.list",
            secret => operations.ListSchemaAsync(secret, database, ct), ct);
}
