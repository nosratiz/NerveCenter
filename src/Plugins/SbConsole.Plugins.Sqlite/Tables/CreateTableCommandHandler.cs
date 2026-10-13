using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed record CreateTableCommand(Guid ConnectionId, string ConnectionName, string Ddl);

/// <summary>
/// Create table… (design spec §11.5). Mutating, audited "sqlite.table.create": exactly one CREATE
/// TABLE statement, run in a transaction on an attach-limited writable connection. Returns the new
/// table's name. Audit detail: "sha256:&lt;12 hex&gt; · first 200 chars of the DDL".
/// </summary>
public sealed class CreateTableCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTableCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.table.create";

    public async Task<PluginResult<string>> HandleAsync(CreateTableCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail(secretError!);
        }

        var hash = QueryAudit.Hash(cmd.Ddl);
        string name;
        try
        {
            name = await operations.CreateTableAsync(secret, cmd.Ddl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed ({Hash}).", Action, cmd.ConnectionName, hash);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: false, $"{hash} · {friendly}");
            return PluginResult<string>.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, $"{cmd.ConnectionName}/{name}", ActionRisk.Mutating, succeeded: true,
            $"{hash} · {QueryAudit.Preview(cmd.Ddl)}");
        return PluginResult<string>.Ok(name);
    }
}
