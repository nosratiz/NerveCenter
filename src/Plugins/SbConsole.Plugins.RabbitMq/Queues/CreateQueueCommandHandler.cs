using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, string Vhost, CreateQueueRequest Request);

public sealed class CreateQueueCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateQueueCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.queue.create", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Request.Name), ActionRisk.Mutating, cmd.Request.Type,
            secret => operations.CreateQueueAsync(secret, cmd.Vhost, cmd.Request, ct), ct);
}
