using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed record RecoverCommand(Guid ConnectionId, string ConnectionName);

/// <summary>
/// Recover into new file (design spec §11.6). Mutating, audited "sqlite.recover": a best-effort
/// salvage into a new file in the backup directory; the live file is only read. Audit detail:
/// "&lt;file&gt; · N tables · R rows (S skipped) · F failed".
/// </summary>
public sealed class RecoverCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, TimeProvider clock, ILogger<RecoverCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.recover";

    public async Task<PluginResult<SalvageOutcome>> HandleAsync(RecoverCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<SalvageOutcome>.Fail(secretError!);
        }

        SalvageOutcome outcome;
        try
        {
            outcome = await operations.SalvageAsync(secret, clock.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<SalvageOutcome>.Fail(friendly);
        }

        var detail = $"{outcome.FileName} · {outcome.TablesCopied} tables · {SqliteFormat.Count(outcome.RowsCopied)} rows"
            + (outcome.RowsSkipped > 0 ? $" ({SqliteFormat.Count(outcome.RowsSkipped)} skipped)" : "")
            + $" · {outcome.Failures.Count} failed";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: true, detail);
        return PluginResult<SalvageOutcome>.Ok(outcome);
    }
}
