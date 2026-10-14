using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed class DropIndexCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DropIndexCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.index.drop";

    public async Task<PluginResult> HandleAsync(IndexCommand cmd, CancellationToken ct = default)
    {
        var prefix = $"{cmd.ConnectionName}/{cmd.Database ?? "(default)"}";
        var target = $"{prefix}/oid {cmd.IndexOid}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult.Fail(secretError!);
        }

        try
        {
            var qualified = await operations.DropIndexAsync(secret, cmd.Database, cmd.IndexOid, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, $"{prefix}/{qualified}", ActionRisk.Destructive, succeeded: true, null);
            return PluginResult.Ok();
        }
        catch (PgSqlRefusedException ex)
        {
            return PluginResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlyPostgresError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }
    }
}
