using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis;

internal static class HandlerRunner
{
    public const string ConnectionNotFound = "Connection not found.";

    public static async Task<PluginResult<T>> QueryAsync<T>(
        IConnectionProvider connections,
        ILogger logger,
        Guid connectionId,
        string description,
        Func<string, Task<T>> run,
        CancellationToken ct)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<T>.Fail(ConnectionNotFound);
        }

        try
        {
            var value = await run(secret);
            return PluginResult<T>.Ok(value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Redis query failed: {Description}", description);
            return PluginResult<T>.Fail(ex);
        }
    }
}
