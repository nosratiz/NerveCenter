using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueUrl, string QueueName);

public sealed class DeleteQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections)
{
    public async Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteQueueAsync(secret, cmd.QueueUrl, ct);
        }
        catch (Exception ex)
        {
            return PluginResult.Fail(ex);
        }

        return PluginResult.Ok();
    }
}
