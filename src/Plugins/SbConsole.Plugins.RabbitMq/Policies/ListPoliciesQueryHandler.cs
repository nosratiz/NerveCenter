using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Policies;

/// <summary>Policies plus the queues and exchanges their patterns are matched against (the Matches column).</summary>
public sealed record PoliciesSnapshot(IReadOnlyList<PolicyInfo> Policies, IReadOnlyList<QueueSummary> Queues, IReadOnlyList<ExchangeSummary> Exchanges);

public sealed class ListPoliciesQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListPoliciesQueryHandler> logger)
{
    public Task<PluginResult<PoliciesSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing policies", async secret =>
        {
            var policies = operations.ListPoliciesAsync(secret, vhost, ct);
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            var exchanges = operations.ListExchangesAsync(secret, vhost, ct);
            await Task.WhenAll(policies, queues, exchanges);
            return new PoliciesSnapshot(await policies, await queues, await exchanges);
        }, ct);
}
