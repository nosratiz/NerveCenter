using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Topics;

public sealed record CreateTopicCommand(Guid ConnectionId, string ConnectionName, string TopicName, int PartitionCount, int ReplicationFactor);

public sealed class CreateTopicCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateTopicAsync(secret, new CreateTopicRequest(cmd.TopicName, cmd.PartitionCount, cmd.ReplicationFactor), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating topic {Target} failed.", target);
            await audit.RecordAsync("kafka.topic.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("kafka.topic.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
