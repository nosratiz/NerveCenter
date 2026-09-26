using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Queues;

/// <summary>Queues plus the vhost's bindings and exchanges, which DeadLetterTopology needs to flag DLQs.</summary>
public sealed record QueuesSnapshot(IReadOnlyList<QueueSummary> Queues, IReadOnlyList<BindingInfo> Bindings, IReadOnlyList<ExchangeSummary> Exchanges);

public sealed class ListQueuesQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListQueuesQueryHandler> logger)
{
    public Task<PluginResult<QueuesSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing queues", async secret =>
        {
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            var bindings = operations.ListBindingsAsync(secret, vhost, ct);
            var exchanges = operations.ListExchangesAsync(secret, vhost, ct);
            await Task.WhenAll(queues, bindings, exchanges);
            return new QueuesSnapshot(await queues, await bindings, await exchanges);
        }, ct);
}
