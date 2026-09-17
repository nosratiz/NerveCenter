using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed record ResetConsumerGroupOffsetCommand(
    Guid ConnectionId, string ConnectionName, string GroupId, string TopicName, int Partition,
    OffsetResetMode Mode, long? Offset, DateTimeOffset? Timestamp);

public sealed class ResetConsumerGroupOffsetCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ResetConsumerGroupOffsetCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(ResetConsumerGroupOffsetCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.GroupId}/{cmd.TopicName}-{cmd.Partition}";

        if (cmd.Mode == OffsetResetMode.Offset && cmd.Offset is null)
        {
            return PluginResult.Fail("An offset value is required for this reset mode.");
        }

        if (cmd.Mode == OffsetResetMode.Timestamp && cmd.Timestamp is null)
        {
            return PluginResult.Fail("A timestamp is required for this reset mode.");
        }

        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.ResetConsumerGroupOffsetAsync(secret, cmd.GroupId, cmd.TopicName, cmd.Partition, cmd.Mode, cmd.Offset, cmd.Timestamp, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resetting offset for {Target} failed.", target);
            await audit.RecordAsync("kafka.consumergroup.resetoffset", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("kafka.consumergroup.resetoffset", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
