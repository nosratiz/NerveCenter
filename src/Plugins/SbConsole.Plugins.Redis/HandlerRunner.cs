using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
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

    public static Task<string?> ResolveSecretAsync(IConnectionProvider connections, Guid connectionId, CancellationToken ct) =>
        connections.GetSecretAsync(connectionId, ct);

    /// <summary>
    /// The shared body of every Keys mutating command: resolve the secret, run the operation
    /// against Redis, log and reduce any exception through FriendlyRedisError, and write one audit
    /// row either way. Mirrors RabbitMQ's HandlerRunner.CommandAsync exactly (docs/design.md §6.8);
    /// handlers with a non-standard audit shape (e.g. delete-by-pattern's dry-run-count detail, or
    /// zset increment's returned score) use ResolveSecretAsync/AuditQuietlyAsync and write their own.
    /// </summary>
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var friendly = FriendlyRedisError.From(ex);
            await AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }

        // Outside the try: Redis already did it, so a failed audit write must not turn a
        // successful delete/rename/etc. into a reported failure (or add a second, "failed" row).
        await AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: true, detail);
        return PluginResult.Ok();
    }

    /// <summary>
    /// Writes one audit row with CancellationToken.None (the Redis call has already happened, so a
    /// cancelled page must not skip the record) and only logs if the write itself fails.
    /// </summary>
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

    /// <summary>Audit target format: "{connectionName}/db{n}/{key}" -- database index takes the role vhost plays for RabbitMQ.</summary>
    public static string Target(string connectionName, int database, string key) => $"{connectionName}/db{database}/{key}";
}
