using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

public sealed record DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName);

public sealed class DeleteTopicCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
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
            await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
