using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed class GetObjectDetailQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetObjectDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgObjectDetail?>> HandleAsync(Guid connectionId, string? database, uint oid, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.object.detail",
            secret => operations.GetObjectDetailAsync(secret, database, oid, ct), ct);
}
