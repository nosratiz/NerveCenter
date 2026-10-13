using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed record ExportTableCsvCommand(Guid ConnectionId, string ConnectionName, string Table);

/// <summary>Safe (read-only) but audited: data leaves the server (design spec §6).</summary>
public sealed class ExportTableCsvCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ExportTableCsvCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.table.export";

    public async Task<PluginResult<CsvExport>> HandleAsync(ExportTableCsvCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Table}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult<CsvExport>.Fail(secretError!);
        }

        CsvExport export;
        try
        {
            export = await operations.ExportCsvAsync(secret, cmd.Table, CsvExport.MaxRows, CsvExport.MaxBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<CsvExport>.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: true, export.Summary);
        return PluginResult<CsvExport>.Ok(export);
    }
}
