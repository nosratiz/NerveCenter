using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Activity;

/// <summary>Shared body of the cancel and terminate handlers; acts only on client backends that are not this console's own.</summary>
internal static class BackendCommandRunner
{
    public const string OwnApplication = "sbconsole";

    public static async Task<PluginResult> RunAsync(
        IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger logger,
        BackendCommand cmd, string action, ActionRisk risk, Func<string, int, CancellationToken, Task<bool>> signal, CancellationToken ct)
    {
        var target = $"{cmd.ConnectionName}/pid {cmd.Pid}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult.Fail(secretError!);
        }

        var ended = $"pid {cmd.Pid} had already ended.";
        try
        {
            var backend = await operations.GetBackendAsync(secret, cmd.Pid, ct);
            if (backend is null)
            {
                await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, ended);
                return PluginResult.Fail(ended);
            }

            if (backend.Application == OwnApplication)
            {
                return PluginResult.Fail("That's one of this console's own connections.");
            }

            if (!await signal(secret, cmd.Pid, ct))
            {
                await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, ended);
                return PluginResult.Fail(ended);
            }

            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: true,
                $"pid {backend.Pid} · {backend.User} · {backend.Application} · {QueryAudit.Preview(backend.Query)}");
            return PluginResult.Ok();
        }
        catch (PgSqlRefusedException ex)
        {
            return PluginResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var friendly = FriendlyPostgresError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }
    }
}
