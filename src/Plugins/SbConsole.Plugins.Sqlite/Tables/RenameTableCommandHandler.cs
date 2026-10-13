using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed record RenameTableCommand(Guid ConnectionId, string ConnectionName, string OldName, string NewName);

/// <summary>
/// Rename (design spec §11.5). Mutating, audited "sqlite.table.rename" on "&lt;connection&gt;/&lt;old&gt;"
/// with detail "→ &lt;new&gt;". The page confirms on prod; the operation validates both names.
/// </summary>
public sealed class RenameTableCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RenameTableCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.table.rename";

    public async Task<PluginResult> HandleAsync(RenameTableCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.OldName}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult.Fail(secretError!);
        }

        var newName = cmd.NewName.Trim();
        try
        {
            await operations.RenameTableAsync(secret, cmd.OldName, newName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Mutating, succeeded: false, $"→ {newName} · {friendly}");
            return PluginResult.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Mutating, succeeded: true, $"→ {newName}");
        return PluginResult.Ok();
    }
}
