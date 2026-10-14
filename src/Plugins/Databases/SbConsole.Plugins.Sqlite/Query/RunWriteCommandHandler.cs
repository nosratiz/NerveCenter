using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

public sealed record RunWriteCommand(Guid ConnectionId, string ConnectionName, string Sql);

/// <summary>
/// Write mode's Run in transaction (design spec §11.4). Destructive: the page requires a dry run of
/// the same text first and, on prod, a typed confirmation. Audit detail:
/// "sha256:&lt;12 hex&gt; · N rows + M cascaded · duration · first 200 chars of SQL".
/// </summary>
public sealed class RunWriteCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RunWriteCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.query.write";

    public async Task<PluginResult<WriteScriptResult>> HandleAsync(RunWriteCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<WriteScriptResult>.Fail(secretError!);
        }

        var hash = QueryAudit.Hash(cmd.Sql);
        WriteScriptResult result;
        try
        {
            result = await operations.RunWriteAsync(secret, cmd.Sql, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed ({Hash}).", Action, cmd.ConnectionName, hash);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Destructive, succeeded: false, $"{hash} · {friendly}");
            return PluginResult<WriteScriptResult>.Fail(friendly);
        }

        var detail = $"{hash} · {SqliteFormat.Count(result.DirectRows)} rows + {SqliteFormat.Count(result.CascadedRows)} cascaded"
            + $" · {SqliteFormat.Duration(result.Duration)} · {QueryAudit.Preview(cmd.Sql)}";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Destructive, succeeded: true, detail);
        return PluginResult<WriteScriptResult>.Ok(result);
    }
}
