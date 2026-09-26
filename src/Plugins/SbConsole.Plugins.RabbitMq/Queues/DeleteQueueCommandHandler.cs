using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Queue);

public sealed class DeleteQueueCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteQueueCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.queue.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Queue), ActionRisk.Destructive, null,
            secret => operations.DeleteQueueAsync(secret, cmd.Vhost, cmd.Queue, ct), ct);
}
