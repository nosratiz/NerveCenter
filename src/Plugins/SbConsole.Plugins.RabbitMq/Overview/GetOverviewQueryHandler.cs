using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Overview;

/// <summary>Everything the Overview page draws: cluster totals, nodes, and the vhost's queues (for "Deepest queues").</summary>
public sealed record OverviewSnapshot(BrokerOverview Overview, IReadOnlyList<NodeSummary> Nodes, IReadOnlyList<QueueSummary> Queues);

public sealed class GetOverviewQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<GetOverviewQueryHandler> logger)
{
    public Task<PluginResult<OverviewSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Loading the broker overview", async secret =>
        {
            var overview = operations.GetOverviewAsync(secret, ct);
            var nodes = operations.ListNodesAsync(secret, ct);
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            await Task.WhenAll(overview, nodes, queues);
            return new OverviewSnapshot(await overview, await nodes, await queues);
        }, ct);
}
