using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes;

internal static class CommandRunner
{
    /// <summary>
    /// Resolves the secret (not audited on failure: nothing ran), runs <paramref name="work"/>, audits success with the
    /// detail it returns, audits failure with the friendly message, and never throws except for caller cancellation.
    /// </summary>
    public static async Task<PluginResult> RunAsync(
        IConnectionProvider connections, IAuditScope audit, ILogger logger, Guid connectionId,
        string action, string target, ActionRisk risk, Func<string, Task<string?>> work, CancellationToken ct)
    {
        var r = await CoreAsync<bool>(connections, audit, logger, connectionId, action, target, risk,
            async s => (true, await work(s)), ct);
        return r.IsSuccess ? PluginResult.Ok() : PluginResult.Fail(r.Error!);
    }

    public static Task<PluginResult<T>> RunAsync<T>(
        IConnectionProvider connections, IAuditScope audit, ILogger logger, Guid connectionId,
        string action, string target, ActionRisk risk, Func<string, Task<(T Value, string? Detail)>> work, CancellationToken ct) =>
        CoreAsync(connections, audit, logger, connectionId, action, target, risk, work, ct);

    private static async Task<PluginResult<T>> CoreAsync<T>(
        IConnectionProvider connections, IAuditScope audit, ILogger logger, Guid connectionId,
        string action, string target, ActionRisk risk, Func<string, Task<(T Value, string? Detail)>> work, CancellationToken ct)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, connectionId, $"{action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<T>.Fail(secretError!);
        }

        try
        {
            var (value, detail) = await work(secret);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: true, detail);
            return PluginResult<T>.Ok(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var friendly = FriendlyKubernetesError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, friendly);
            return PluginResult<T>.Fail(friendly);
        }
    }
}
