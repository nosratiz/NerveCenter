using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

/// <summary>
/// The bindings whose source is one exchange -- what the Publish dialog's routing preview and the
/// Bindings dialog read. The default exchange has no listable bindings (every queue is implicitly
/// bound by its own name), so they're synthesized from the queue list.
/// </summary>
public sealed class ListExchangeBindingsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListExchangeBindingsQueryHandler> logger)
{
    public Task<PluginResult<IReadOnlyList<BindingInfo>>> HandleAsync(Guid connectionId, string vhost, string exchange, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing exchange bindings", async secret =>
        {
            if (exchange.Length == 0)
            {
                var queues = await operations.ListQueuesAsync(secret, vhost, ct);
                return RoutingMatcher.SynthesizeDefaultExchangeBindings(queues.Select(q => q.Name));
            }

            var bindings = await operations.ListBindingsAsync(secret, vhost, ct);
            return (IReadOnlyList<BindingInfo>)bindings.Where(b => b.Source == exchange).ToList();
        }, ct);
}
