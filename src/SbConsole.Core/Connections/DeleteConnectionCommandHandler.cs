using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record DeleteConnectionCommand(Guid Id, string Actor);

public sealed class DeleteConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result> HandleAsync(DeleteConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (connection is null)
        {
            await audit.WriteAsync(new AuditEntry
            {
                At = clock.GetUtcNow(),
                Actor = cmd.Actor,
                Action = "connection.delete",
                Target = cmd.Id.ToString(),
                Risk = ActionRisk.Destructive,
                Succeeded = false,
                Detail = "Connection not found.",
            }, ct);

            return Result.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        db.Connections.Remove(connection);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.delete",
            Target = connection.Name,
            Risk = ActionRisk.Destructive,
            Succeeded = true,
        }, ct);

        return Result.Ok();
    }
}
