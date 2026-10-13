using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

public sealed record RestoreBackupCommand(Guid ConnectionId, string ConnectionName, Guid BackupId);

/// <summary>
/// Restore… (design spec §11.7). Destructive, audited "sqlite.backup.restore"; the page confirms
/// first (typed on prod, IsProd from the server-side ConnectionInfo). Needs Mode=ReadWrite -- the
/// operations re-read it from the decrypted secret. Verify → pre-restore copy → online backup →
/// integrity check of the live file.
/// </summary>
public sealed class RestoreBackupCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<RestoreBackupCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.restore";

    public async Task<PluginResult<RestoreOutcome>> HandleAsync(RestoreBackupCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<RestoreOutcome>.Fail(secretError!);
        }

        var record = await BackupHandlerSupport.FindAsync(store, cmd.ConnectionId, cmd.BackupId, ct);
        if (record is null)
        {
            return PluginResult<RestoreOutcome>.Fail(BackupHandlerSupport.BackupNotFound);
        }

        if (record.FileName is null)
        {
            return PluginResult<RestoreOutcome>.Fail(BackupHandlerSupport.NoFile);
        }

        RestoreOutcome outcome;
        try
        {
            outcome = await operations.RestoreBackupAsync(secret, record.FileName, clock.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} of {File} on {Target} failed.", Action, record.FileName, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Destructive, succeeded: false, $"{record.FileName} · {friendly}");
            return PluginResult<RestoreOutcome>.Fail(friendly);
        }

        var integrity = outcome.IntegrityOk ? "integrity ok" : $"integrity: {outcome.Messages.Count} {(outcome.Messages.Count == 1 ? "problem" : "problems")}";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Destructive, succeeded: true,
            $"{record.FileName} · previous file kept as {outcome.PreRestoreFileName} · {integrity} · {SqliteFormat.Duration(outcome.Duration)}");
        return PluginResult<RestoreOutcome>.Ok(outcome);
    }
}
