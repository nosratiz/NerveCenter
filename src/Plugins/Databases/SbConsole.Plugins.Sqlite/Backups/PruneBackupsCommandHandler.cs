using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

public sealed record PruneBackupsCommand(Guid ConnectionId, string ConnectionName);

/// <summary>Deleted = history records removed (with their files); Failed = files that couldn't be deleted (their records stay).</summary>
public sealed record PruneResult(int Deleted, long BytesFreed, int Failed);

/// <summary>
/// Retention / Prune (design spec §11.7). Destructive, audited "sqlite.backup.prune". Deletes what
/// BackupRetention.Plan picks under the saved policy (defaults when none) -- only files named in
/// the history, each re-checked inside BackupDir by the operations. The page confirms first; the
/// scheduler doesn't (the policy is the consent). A cancellation stops between files: what was
/// already deleted is still removed from history and audited, then the cancellation is rethrown.
/// </summary>
public sealed class PruneBackupsCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<PruneBackupsCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.prune";

    public async Task<PluginResult<PruneResult>> HandleAsync(PruneBackupsCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<PruneResult>.Fail(secretError!);
        }

        var history = await BackupHistoryStore.ReadAsync(store, cmd.ConnectionId, ct);
        var policy = await BackupPolicyStore.ReadAsync(store, cmd.ConnectionId, ct) ?? BackupPolicy.Default;
        var plan = BackupRetention.Plan(history, policy, clock.GetUtcNow());

        var removed = new List<Guid>();
        long bytes = 0;
        var failed = 0;
        string? firstError = null;
        foreach (var record in plan)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (record.FileName is not null)
            {
                try
                {
                    await operations.DeleteBackupFileAsync(secret, record.FileName, CancellationToken.None);
                    bytes += record.Bytes;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Deleting backup {File} of {Target} failed.", record.FileName, cmd.ConnectionName);
                    firstError ??= FriendlySqliteError.From(ex);
                    failed++;
                    continue;
                }
            }

            removed.Add(record.Id);
        }

        if (removed.Count > 0)
        {
            await BackupHandlerSupport.StoreQuietlyAsync(() => BackupHistoryStore.RemoveAsync(store, cmd.ConnectionId, removed, CancellationToken.None),
                logger, "the pruned history", cmd.ConnectionName);
        }

        var detail = string.Create(CultureInfo.InvariantCulture, $"{removed.Count} removed · {SqliteFormat.Bytes(bytes)} freed · {policy.Describe()}")
            + (failed == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" · {failed} failed: {firstError}"));
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Destructive, succeeded: failed == 0, detail);
        ct.ThrowIfCancellationRequested();
        return PluginResult<PruneResult>.Ok(new PruneResult(removed.Count, bytes, failed));
    }
}
