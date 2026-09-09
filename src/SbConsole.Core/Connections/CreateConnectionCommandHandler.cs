using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record CreateConnectionCommand(
    string Name, string Kind, string Secret, IReadOnlyList<string> Tags, string Actor);

public sealed class CreateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<Guid>> HandleAsync(CreateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result<Guid>.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        var connection = new Data.Entities.Connection
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name,
            Kind = cmd.Kind,
            TagsCsv = string.Join(',', cmd.Tags),
            SecretCiphertext = protector.Protect(cmd.Secret),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.create",
            Target = cmd.Name,
            Risk = ActionRisk.Mutating,
            Succeeded = true,
        }, ct);

        return Result<Guid>.Ok(connection.Id);
    }
}
