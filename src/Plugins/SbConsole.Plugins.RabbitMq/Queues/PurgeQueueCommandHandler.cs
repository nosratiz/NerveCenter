using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Queues;

public sealed record PurgeQueueCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Queue, long? ReadyCount = null);

public sealed class PurgeQueueCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PurgeQueueCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(PurgeQueueCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.queue.purge", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Queue), ActionRisk.Destructive, cmd.ReadyCount is { } ready ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ready:N0} ready") : null,
            secret => operations.PurgeQueueAsync(secret, cmd.Vhost, cmd.Queue, ct), ct);
}
