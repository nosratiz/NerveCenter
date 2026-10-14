using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Overview;

public sealed class ListDatabasesQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<ListDatabasesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<string>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.databases.list",
            secret => operations.ListDatabasesAsync(secret, ct), ct);
}
