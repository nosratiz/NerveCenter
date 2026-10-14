using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed record ReindexCommand(Guid ConnectionId, string ConnectionName);

/// <summary>
/// REINDEX on a failed integrity check (design spec §11.6). Mutating, audited "sqlite.reindex":
/// REINDEX, then a fresh integrity check that replaces the stored result (same store as Run now).
/// </summary>
public sealed class ReindexCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<ReindexCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.reindex";

    public async Task<PluginResult<IntegrityResult>> HandleAsync(ReindexCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<IntegrityResult>.Fail(secretError!);
        }

        var ranAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        IntegrityCheckOutcome outcome;
        try
        {
            outcome = await operations.ReindexAsync(secret, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<IntegrityResult>.Fail(friendly);
        }

        var result = new IntegrityResult(outcome.IntegrityOk, outcome.Messages, outcome.ForeignKeyViolations, ranAt, clock.GetElapsedTime(started));
        try
        {
            await IntegrityResultStore.WriteAsync(store, cmd.ConnectionId, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Storing the integrity result after REINDEX for {Target} failed.", cmd.ConnectionName);
        }

        var detail = result.IntegrityOk
            ? $"integrity ok after REINDEX · {result.ForeignKeyViolations} FK violations"
            : $"{result.Messages.Count} problems remain after REINDEX · {result.ForeignKeyViolations} FK violations";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: true, detail);
        return PluginResult<IntegrityResult>.Ok(result);
    }
}
