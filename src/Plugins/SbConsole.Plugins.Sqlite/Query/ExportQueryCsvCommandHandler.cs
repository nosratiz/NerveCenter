using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>BackupFile: export from that file in the connection's BackupDir instead of the live file (§11.7).</summary>
public sealed record ExportQueryCsvCommand(Guid ConnectionId, string ConnectionName, string Sql, string? BackupFile = null);

/// <summary>
/// The Query page's Export CSV (design spec §11.3): re-runs the script read-only with the table
/// export's caps. Safe but audited -- data leaves the server; the detail names the script by hash.
/// </summary>
public sealed class ExportQueryCsvCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ExportQueryCsvCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.query.export";

    public async Task<PluginResult<CsvExport>> HandleAsync(ExportQueryCsvCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<CsvExport>.Fail(secretError!);
        }

        var hash = QueryAudit.Hash(cmd.Sql);
        CsvExport export;
        try
        {
            export = await operations.ExportQueryCsvAsync(secret, cmd.Sql, CsvExport.MaxRows, CsvExport.MaxBytes, cmd.BackupFile, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed ({Hash}).", Action, cmd.ConnectionName, hash);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, $"{hash} · {friendly}");
            return PluginResult<CsvExport>.Fail(friendly);
        }

        var source = cmd.BackupFile is null ? "" : $"backup {cmd.BackupFile} · ";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true,
            $"{source}{export.Summary} · {hash} · {QueryAudit.Preview(cmd.Sql)}");
        return PluginResult<CsvExport>.Ok(export);
    }
}
