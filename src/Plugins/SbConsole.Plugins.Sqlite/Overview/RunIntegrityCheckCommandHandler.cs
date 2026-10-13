using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed record RunIntegrityCheckCommand(Guid ConnectionId, string ConnectionName);

/// <summary>
/// PRAGMA integrity_check + foreign_key_check, on demand only (they walk the whole file). Safe but
/// audited -- it can hold a read transaction for a long time on a big file (design spec §5).
/// </summary>
public sealed class RunIntegrityCheckCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<RunIntegrityCheckCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.integrity.check";

    public async Task<PluginResult<IntegrityResult>> HandleAsync(RunIntegrityCheckCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IntegrityResult>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var ranAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        IntegrityCheckOutcome outcome;
        try
        {
            outcome = await operations.RunIntegrityCheckAsync(secret, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<IntegrityResult>.Fail(friendly);
        }

        var result = new IntegrityResult(outcome.IntegrityOk, outcome.Messages, outcome.ForeignKeyViolations, ranAt, clock.GetElapsedTime(started));
        try
        {
            await IntegrityResultStore.WriteAsync(store, cmd.ConnectionId, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The check itself happened; losing the cached copy only means "never run" next visit.
            logger.LogError(ex, "Storing the integrity result for {Target} failed.", cmd.ConnectionName);
        }

        var detail = result.IntegrityOk
            ? $"ok · {result.ForeignKeyViolations} FK violations"
            : $"{result.Messages.Count} problems · {result.ForeignKeyViolations} FK violations";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true, detail);
        return PluginResult<IntegrityResult>.Ok(result);
    }
}
