using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record TestConnectionCommand(Guid ConnectionId, string Actor);

public sealed class TestConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IEnumerable<IPlugin> plugins,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<ConnectionTestResult>> HandleAsync(TestConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);
        if (connection is null)
        {
            return Result<ConnectionTestResult>.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == connection.Kind);
        if (plugin is null)
        {
            return Result<ConnectionTestResult>.Fail(ErrorCategory.NotFound, $"No plugin registered for connection kind '{connection.Kind}'.");
        }

        var secret = protector.Unprotect(connection.SecretCiphertext);
        var testResult = await plugin.TestConnectionAsync(secret, ct);

        connection.LastTestSucceeded = testResult.Success;
        connection.LastTestedAt = clock.GetUtcNow();
        connection.LastTestError = testResult.ErrorMessage;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.test",
            Target = connection.Name,
            Risk = ActionRisk.Safe,
            Succeeded = testResult.Success,
            Detail = testResult.ErrorMessage,
        }, ct);

        return Result<ConnectionTestResult>.Ok(testResult);
    }
}
