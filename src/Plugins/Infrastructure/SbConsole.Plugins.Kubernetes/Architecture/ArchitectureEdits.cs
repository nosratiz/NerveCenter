using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>Who is editing which connection's map. <see cref="Author"/> is the signed-in user, recorded on the change.</summary>
public sealed record ArchEditContext(Guid ConnectionId, string ConnectionName, string Author);

internal static class ArchitectureEdits
{
    /// <summary>Every architecture edit is console data, audited under one action.</summary>
    public const string Action = "k8s.arch.edit";
    public const string NothingChanged = "Nothing changed.";

    /// <summary>
    /// Checks the connection exists (not audited when it doesn't: nothing ran), runs the store change, audits success with
    /// its detail and a store failure with a generic message. An <see cref="ArchEditException"/> is a refusal: returned, not audited.
    /// </summary>
    public static async Task<PluginResult<T>> RunAsync<T>(
        IConnectionProvider connections, IAuditScope audit, ILogger logger, ArchEditContext context, string target,
        Func<Task<(T Value, string Detail)>> work, CancellationToken ct)
    {
        var (secret, error) = await HandlerRunner.TryResolveSecretAsync(connections, logger, context.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<T>.Fail(error!);
        }

        var auditTarget = $"{context.ConnectionName}/{target}";
        try
        {
            var (value, detail) = await work();
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, auditTarget, ActionRisk.Mutating, succeeded: true, detail);
            return PluginResult<T>.Ok(value);
        }
        catch (ArchEditException refused)
        {
            return PluginResult<T>.Fail(refused.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, auditTarget);
            const string failed = "Saving to the console failed — try again.";
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, auditTarget, ActionRisk.Mutating, succeeded: false, failed);
            return PluginResult<T>.Fail(failed);
        }
    }

    /// <summary>"checkout/payment-gateway" for a workload, the host for an external node; the id itself otherwise.</summary>
    public static string NodeLabel(string nodeId)
    {
        if (nodeId.StartsWith("x:", StringComparison.Ordinal))
        {
            return nodeId[2..];
        }

        var body = nodeId.Length > 2 && nodeId[1] == ':' ? nodeId[2..] : nodeId;
        var parts = body.Split('/');
        return parts.Length == 3 ? $"{parts[0]}/{parts[2]}" : body;
    }
}
