using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

internal static class HandlerRunner
{
    public const string ConnectionNotFound = "Connection not found.";

    /// <summary>
    /// Shared body of every read-only query: resolve the secret, run, reduce any failure through
    /// FriendlySqliteError (Redis/RabbitMQ HandlerRunner pattern). A cancellation the caller asked
    /// for is rethrown so pages can tell "you cancelled" from "it failed".
    /// </summary>
    public static async Task<PluginResult<T>> QueryAsync<T>(
        IConnectionProvider connections, ILogger logger, Guid connectionId, string description,
        Func<string, Task<T>> run, CancellationToken ct)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<T>.Fail(ConnectionNotFound);
            }

            return PluginResult<T>.Ok(await run(secret));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "SQLite query failed: {Description}", description);
            return PluginResult<T>.Fail(ex);
        }
    }

    /// <summary>Writes one audit row with CancellationToken.None and only logs if the write itself fails.</summary>
    public static async Task AuditQuietlyAsync(
        IAuditScope audit, ILogger logger, string action, string target, ActionRisk risk, bool succeeded, string? detail)
    {
        try
        {
            await audit.RecordAsync(action, target, risk, succeeded, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing the {Action} audit row for {Target} (succeeded: {Succeeded}) failed.", action, target, succeeded);
        }
    }
}
