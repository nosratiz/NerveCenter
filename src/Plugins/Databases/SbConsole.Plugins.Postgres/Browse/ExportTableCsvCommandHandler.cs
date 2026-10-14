using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Browse;

/// <summary>QualifiedName comes from the page's loaded detail and is used for the audit target only; the operation works from the oid.</summary>
public sealed record ExportTableCsvCommand(Guid ConnectionId, string ConnectionName, string? Database, string QualifiedName, PgRowsRequest Request);

/// <summary>Safe (read-only) but audited: data leaves the server.</summary>
public sealed class ExportTableCsvCommandHandler(
    IPostgresOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ExportTableCsvCommandHandler> logger) : IScopedService
{
    public const string Action = "pg.table.export";

    public async Task<PluginResult<CsvExport>> HandleAsync(ExportTableCsvCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Database ?? "(default)"}/{cmd.QualifiedName}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<CsvExport>.Fail(secretError!);
        }

        CsvExport export;
        try
        {
            export = await operations.ExportTableCsvAsync(secret, cmd.Database, cmd.Request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, "cancelled");
            throw;
        }
        catch (PgSqlRefusedException ex)
        {
            // Nothing ran: no audit row.
            return PluginResult<CsvExport>.Fail(FriendlyPostgresError.From(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlyPostgresError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<CsvExport>.Fail(friendly);
        }

        var filters = cmd.Request.Filters.Count;
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: true,
            export.Summary + (filters > 0 ? $" · {filters} filters" : ""));
        return PluginResult<CsvExport>.Ok(export);
    }
}
