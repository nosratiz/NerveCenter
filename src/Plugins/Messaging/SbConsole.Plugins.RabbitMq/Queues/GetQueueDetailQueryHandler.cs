using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Queues;

/// <summary>One queue's detail plus the vhost topology its dead-letter "route out" is resolved against.</summary>
public sealed record QueueDetailSnapshot(QueueDetails Detail, IReadOnlyList<QueueSummary> Queues, IReadOnlyList<BindingInfo> Bindings, IReadOnlyList<ExchangeSummary> Exchanges);

/// <summary>
/// Succeeds with a null snapshot when the queue itself doesn't exist (a 404 from the queue read
/// specifically); a 404 from any of the topology reads is an ordinary failure.
/// </summary>
public sealed class GetQueueDetailQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<GetQueueDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<QueueDetailSnapshot?>> HandleAsync(Guid connectionId, string vhost, string queue, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync<QueueDetailSnapshot?>(connections, logger, connectionId, "Loading queue detail", async secret =>
        {
            var detail = operations.GetQueueAsync(secret, vhost, queue, ct);
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            var bindings = operations.ListBindingsAsync(secret, vhost, ct);
            var exchanges = operations.ListExchangesAsync(secret, vhost, ct);
            try
            {
                await Task.WhenAll(detail, queues, bindings, exchanges);
            }
            catch when (detail.Exception?.InnerException is ManagementApiException { StatusCode: 404 })
            {
                return null;
            }

            return new QueueDetailSnapshot(await detail, await queues, await bindings, await exchanges);
        }, ct);
}
