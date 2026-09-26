using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Connections;

public sealed class ListVhostsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListVhostsQueryHandler> logger)
{
    public Task<PluginResult<IReadOnlyList<string>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing vhosts",
            secret => operations.ListVhostsAsync(secret, ct), ct);
}
