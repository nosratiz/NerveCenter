using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Shovels;

/// <summary>
/// Dynamic shovels plus the vhost's queues (a terminated shovel's explanation row says how many
/// messages are holding in its source). ShovelPluginMissing: the shovel status endpoint answered
/// 404, i.e. rabbitmq_shovel_management isn't enabled -- an informational state, not an error.
/// </summary>
public sealed record ShovelsSnapshot(IReadOnlyList<ShovelInfo> Shovels, bool ShovelPluginMissing, IReadOnlyList<QueueSummary> Queues);

public sealed class ListShovelsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListShovelsQueryHandler> logger)
{
    public Task<PluginResult<ShovelsSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing shovels", async secret =>
        {
            var queues = operations.ListQueuesAsync(secret, vhost, ct);
            IReadOnlyList<ShovelInfo> shovels;
            var missing = false;
            try
            {
                shovels = await operations.ListShovelsAsync(secret, vhost, ct);
            }
            catch (ManagementApiException ex) when (ex.StatusCode == 404)
            {
                shovels = [];
                missing = true;
            }

            return new ShovelsSnapshot(shovels, missing, await queues);
        }, ct);
}
