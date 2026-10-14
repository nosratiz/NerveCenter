using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Activity;

public sealed class GetActivityQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetActivityQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgActivity>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "activity", secret => operations.GetActivityAsync(secret, ct), ct);
}
