using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

public sealed record ExportQueryCsvCommand(Guid ConnectionId, string ConnectionName, string? Database, string Sql, int TimeoutS);

/// <summary>Safe (read-only) but audited: data leaves the server.</summary>
public sealed class ExportQueryCsvCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ExportQueryCsvCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.query.export";

    public async Task<PluginResult<CsvExport>> HandleAsync(ExportQueryCsvCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Database ?? "(default)"}";
        var hash = QueryAudit.Hash(cmd.Sql);
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<CsvExport>.Fail(secretError!);
        }

        CsvExport export;
        try
        {
            export = await operations.ExportQueryCsvAsync(secret, cmd.Database, cmd.Sql, cmd.TimeoutS, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, "cancelled");
            throw;
        }
        catch (PgSqlRefusedException ex)
        {
            // Nothing ran: no audit row.
            return PluginResult<CsvExport>.Fail(FriendlyPostgresError.Describe(ex).Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlyPostgresError.Describe(ex, cmd.Sql).Message;
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, $"{hash} · {friendly}");
            return PluginResult<CsvExport>.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: true, $"{hash} · {export.Summary}");
        return PluginResult<CsvExport>.Ok(export);
    }
}
