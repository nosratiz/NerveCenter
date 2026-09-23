using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    TimeProvider clock,
    ILogger<TestConnectionCommandHandler> logger)
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

        // A plugin is free to hand back whatever its SDK produced. LastTestError is a persisted
        // column rendered in the Connections page's Status cell and Detail is an audit column, so
        // both are capped here rather than trusting every plugin to have done it — the untruncated
        // text is logged first so nothing is actually lost.
        if (!testResult.Success && testResult.ErrorMessage is { Length: > FriendlyError.MaxLength })
        {
            logger.LogWarning(
                "Connection test for {ConnectionName} failed; full error: {Error}",
                connection.Name, testResult.ErrorMessage);
        }

        var errorMessage = FriendlyError.Truncate(testResult.ErrorMessage);

        connection.LastTestSucceeded = testResult.Success;
        connection.LastTestedAt = clock.GetUtcNow();
        connection.LastTestError = errorMessage;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.test",
            Target = connection.Name,
            Risk = ActionRisk.Safe,
            Succeeded = testResult.Success,
            Detail = errorMessage,
        }, ct);

        // The caller (ConnectionEditor.razor) renders the same text, so return the capped version too.
        return Result<ConnectionTestResult>.Ok(testResult with { ErrorMessage = errorMessage });
    }
}
