using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Maintenance;

public sealed record MaintenanceCommand(Guid ConnectionId, string ConnectionName, string? Database, uint TableOid, PgMaintenanceKind Kind);

public sealed class RunMaintenanceCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RunMaintenanceCommandHandler> logger) : IScopedService
{
    public async Task<PluginResult<string>> HandleAsync(MaintenanceCommand cmd, CancellationToken ct = default)
    {
        var (action, risk) = cmd.Kind switch
        {
            PgMaintenanceKind.Vacuum => ("pg.maintenance.vacuum", ActionRisk.Mutating),
            PgMaintenanceKind.Analyze => ("pg.maintenance.analyze", ActionRisk.Mutating),
            PgMaintenanceKind.VacuumFull => ("pg.maintenance.vacuum_full", ActionRisk.Destructive),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd)),
        };
        var prefix = $"{cmd.ConnectionName}/{cmd.Database ?? "(default)"}";
        var target = $"{prefix}/oid {cmd.TableOid}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail(secretError!);
        }

        try
        {
            var qualified = await operations.RunMaintenanceAsync(secret, cmd.Database, cmd.TableOid, cmd.Kind, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, $"{prefix}/{qualified}", risk, succeeded: true, null);
            return PluginResult<string>.Ok(qualified);
        }
        catch (PgSqlRefusedException ex)
        {
            return PluginResult<string>.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var friendly = FriendlyPostgresError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, friendly);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
