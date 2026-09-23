using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Messages;

public sealed record ProduceMessageCommand(Guid ConnectionId, string ConnectionName, string TopicName, string? Key, string Value, int? Partition);

public sealed class ProduceMessageCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ProduceMessageCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(ProduceMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.ProduceMessageAsync(secret, cmd.TopicName, cmd.Key, cmd.Value, cmd.Partition, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Producing a message to {Target} failed.", target);
            await audit.RecordAsync("kafka.message.produce", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("kafka.message.produce", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
