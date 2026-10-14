using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed class GetDdlQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetDdlQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<string?>> HandleAsync(Guid connectionId, string? database, uint oid, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.object.ddl",
            secret => operations.GetDdlAsync(secret, database, oid, ct), ct);
}
