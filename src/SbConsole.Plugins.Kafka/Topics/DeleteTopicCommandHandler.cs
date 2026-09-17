using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Topics;

public sealed record DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName);

public sealed class DeleteTopicCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.DeleteTopicAsync(secret, cmd.TopicName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting topic {Target} failed.", target);
            await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
