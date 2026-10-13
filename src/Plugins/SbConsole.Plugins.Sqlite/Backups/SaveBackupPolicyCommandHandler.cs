using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

public sealed record SaveBackupPolicyCommand(Guid ConnectionId, string ConnectionName, BackupPolicy Policy);

/// <summary>
/// Schedule… (design spec §11.7). Mutating, audited "sqlite.backup.policy". An invalid policy is
/// refused before anything is written or audited. EnabledSince is the handler's, not the caller's:
/// now when the policy goes from disabled/absent to enabled, kept while it stays enabled, cleared
/// when disabled.
/// </summary>
public sealed class SaveBackupPolicyCommandHandler(
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider time,
    ILogger<SaveBackupPolicyCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.policy";

    public async Task<PluginResult> HandleAsync(SaveBackupPolicyCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Policy.Validate() is { } problem)
        {
            return PluginResult.Fail(problem);
        }

        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult.Fail(secretError!);
        }

        try
        {
            var saved = await BackupPolicyStore.ReadAsync(store, cmd.ConnectionId, ct);
            var enabledSince = !cmd.Policy.Enabled ? null
                : saved is { Enabled: true } ? saved.EnabledSince
                : time.GetUtcNow();
            await BackupPolicyStore.WriteAsync(store, cmd.ConnectionId, cmd.Policy with { EnabledSince = enabledSince }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: true, cmd.Policy.Describe());
        return PluginResult.Ok();
    }
}
