using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

public sealed record IndexCommand(Guid ConnectionId, string ConnectionName, string? Database, uint IndexOid);

public sealed class ReindexIndexCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ReindexIndexCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.index.reindex";

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
            var qualified = await operations.ReindexIndexAsync(secret, cmd.Database, cmd.IndexOid, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, $"{prefix}/{qualified}", ActionRisk.Mutating, succeeded: true, null);
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
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }
    }
}
