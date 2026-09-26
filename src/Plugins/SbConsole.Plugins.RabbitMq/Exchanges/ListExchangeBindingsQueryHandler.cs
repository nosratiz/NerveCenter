using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

/// <summary>
/// One exchange's bindings in both directions, plus every binding in the vhost (what
/// <see cref="RoutingMatcher.ResolveDeep"/> needs to follow exchange hops). Incoming lists only
/// exchange sources -- a queue can't be a binding source.
/// </summary>
public sealed record ExchangeBindings(IReadOnlyList<BindingInfo> Outgoing, IReadOnlyList<BindingInfo> Incoming, IReadOnlyList<BindingInfo> Vhost);

/// <summary>
/// The default exchange has no listable bindings of its own (every queue is implicitly bound by its
/// own name) and can't take part in exchange-to-exchange bindings, so its bindings are synthesized
/// from the queue list and it has nothing incoming.
/// </summary>
public sealed class ListExchangeBindingsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListExchangeBindingsQueryHandler> logger)
{
    public Task<PluginResult<ExchangeBindings>> HandleAsync(Guid connectionId, string vhost, string exchange, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing exchange bindings", async secret =>
        {
            if (exchange.Length == 0)
            {
                var queues = await operations.ListQueuesAsync(secret, vhost, ct);
                var synthesized = RoutingMatcher.SynthesizeDefaultExchangeBindings(queues.Select(q => q.Name));
                return new ExchangeBindings(synthesized, [], synthesized);
            }

            var bindings = await operations.ListBindingsAsync(secret, vhost, ct);
            return new ExchangeBindings(
                bindings.Where(b => b.Source == exchange).ToList(),
                bindings.Where(b => !b.IsToQueue && b.Destination == exchange).ToList(),
                bindings);
        }, ct);
}
