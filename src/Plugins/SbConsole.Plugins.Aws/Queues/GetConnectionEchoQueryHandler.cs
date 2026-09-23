using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed class GetConnectionEchoQueryHandler(IConnectionProvider connections, ILogger<GetConnectionEchoQueryHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<string>.Fail("Connection not found.");
            }

            return PluginResult<string>.Ok(AwsConfigParser.SafeEcho(secret));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving connection echo for {ConnectionId} failed.", connectionId);
            return PluginResult<string>.Fail(ex);
        }
    }
}
