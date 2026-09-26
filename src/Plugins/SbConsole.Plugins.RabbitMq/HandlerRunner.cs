using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq;

/// <summary>
/// The shared body of every RabbitMQ handler: resolve the secret just-in-time, run the operation,
/// log the full exception and reduce it through FriendlyRabbitError. Commands additionally write one
/// audit row on success and on failure (failure detail = the friendly message). The AWS plugin
/// repeats this block in every handler; here it is written once so the ~20 handlers stay one-liners
/// and can't drift apart. Handlers with a non-standard audit shape (publish, republish, consume)
/// use <see cref="ResolveSecretAsync"/> and write their own rows.
/// </summary>
internal static class HandlerRunner
{
    public const string ConnectionNotFound = "Connection not found.";

    public static Task<string?> ResolveSecretAsync(IConnectionProvider connections, Guid connectionId, CancellationToken ct) =>
        connections.GetSecretAsync(connectionId, ct);

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
        catch (Exception ex)
        {
            logger.LogError(ex, "{Description} for connection {ConnectionId} failed.", description, connectionId);
            return PluginResult<T>.Fail(ex);
        }
    }

    public static async Task<PluginResult> CommandAsync(
        IConnectionProvider connections, IAuditScope audit, ILogger logger, Guid connectionId,
        string action, string target, ActionRisk risk, string? detail,
        Func<string, Task> run, CancellationToken ct)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail(ConnectionNotFound);
        }

        try
        {
            await run(secret);
            await audit.RecordAsync(action, target, risk, succeeded: true, detail: detail, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var friendly = FriendlyRabbitError.From(ex);
            await audit.RecordAsync(action, target, risk, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }

    public static string Target(string connectionName, string vhost, string name) => $"{connectionName}/{vhost}/{name}";
}
