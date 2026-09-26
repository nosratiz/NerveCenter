using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

/// <summary>Exchanges plus the vhost's bindings (for counts) and queues (default-exchange count, DLX flags).</summary>
public sealed record ExchangesSnapshot(IReadOnlyList<ExchangeSummary> Exchanges, IReadOnlyList<BindingInfo> Bindings, IReadOnlyList<QueueSummary> Queues);

public sealed class ListExchangesQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListExchangesQueryHandler> logger)
{
    public Task<PluginResult<ExchangesSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing exchanges", async secret =>
        {
            var exchanges = operations.ListExchangesAsync(secret, vhost, ct);
            var bindings = operations.ListBindingsAsync(secret, vhost, ct);
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            await Task.WhenAll(exchanges, bindings, queues);
            return new ExchangesSnapshot(await exchanges, await bindings, await queues);
        }, ct);
}
