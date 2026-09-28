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
        try
        {
            // Resolved inside the try, matching RabbitMQ's HandlerRunner.QueryAsync: a
            // secret-decryption failure (e.g. a rotated SBC_DATA_KEY) must be caught and reduced
            // to a friendly failed result here, not propagate raw out of a page's
            // OnInitializedAsync, which has no guard of its own.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<T>.Fail(ConnectionNotFound);
            }

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
