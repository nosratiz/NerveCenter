using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>Actor is the signed-in user's name, for history only; the audit row's actor is the host's own.</summary>
public sealed record RunQueryCommand(Guid ConnectionId, string ConnectionName, string? Database, PgQueryRequest Request, string Actor);

/// <summary>Exactly one of Result / Error is set. Hash is QueryAudit.Hash of the submitted text (links to the audit row).</summary>
public sealed record QueryRun(PgQueryResult? Result, PgErrorInfo? Error, string AuditAction, string Hash);

public sealed class RunQueryCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit,
    [FromKeyedServices(PostgresPlugin.PluginId)] IPluginStore store, TimeProvider clock, ILogger<RunQueryCommandHandler> logger) : IScopedService
{
    public const string ReadAction = "pg.query.read";
    public const string WriteAction = "pg.query.write";

    public async Task<PluginResult<QueryRun>> HandleAsync(RunQueryCommand cmd, CancellationToken ct = default)
    {
        var action = cmd.Request.Write ? WriteAction : ReadAction;
        var risk = cmd.Request.Write ? ActionRisk.Mutating : ActionRisk.Safe;
        var target = $"{cmd.ConnectionName}/{cmd.Database ?? "(default)"}";
        var sql = cmd.Request.Sql;
        var hash = QueryAudit.Hash(sql);
        var head = $"{hash} · {QueryAudit.Preview(sql)}";

        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<QueryRun>.Fail(secretError!);
        }

        var started = clock.GetTimestamp();
        PgQueryResult result;
        try
        {
            result = await operations.RunQueryAsync(secret, cmd.Database, cmd.Request, ct);
        }
        catch (PgSqlRefusedException ex)
        {
            // Nothing ran: no audit row, no history.
            return PluginResult<QueryRun>.Ok(new QueryRun(null, FriendlyPostgresError.Describe(ex), action, hash));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The operations layer rolls a cancelled write back, but the attempt itself must leave a trace.
            var elapsed = clock.GetElapsedTime(started);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, $"{head} · cancelled");
            await AppendHistoryAsync(cmd, hash, null, elapsed, null);
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = clock.GetElapsedTime(started);
            logger.LogError(ex, "{Action} on {Target} failed.", action, target);
            var info = FriendlyPostgresError.Describe(ex, sql);
            var detail = info.SqlState is { } state ? $"{head} · SQLSTATE {state}" : $"{head} · connection failed";
            await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: false, detail);
            await AppendHistoryAsync(cmd, hash, null, elapsed, info.SqlState);
            return PluginResult<QueryRun>.Ok(new QueryRun(null, info, action, hash));
        }

        var rows = result.RowsAffected ?? result.Rows.Count;
        await HandlerRunner.AuditQuietlyAsync(audit, logger, action, target, risk, succeeded: true,
            $"{head} · {rows} rows · {result.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s");
        await AppendHistoryAsync(cmd, hash, (int)Math.Min(rows, int.MaxValue), result.Elapsed, null);
        return PluginResult<QueryRun>.Ok(new QueryRun(result, null, action, hash));
    }

    private async Task AppendHistoryAsync(RunQueryCommand cmd, string hash, int? rows, TimeSpan elapsed, string? sqlState)
    {
        try
        {
            var now = clock.GetUtcNow();
            await QueryHistoryStore.AppendAsync(store, cmd.ConnectionId,
                new QueryHistoryEntry(cmd.Request.Sql, cmd.Request.Write, rows, elapsed.TotalSeconds, sqlState, cmd.Actor, now, hash, cmd.Database),
                now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Appending query history for {Connection} failed.", cmd.ConnectionId);
        }
    }
}
