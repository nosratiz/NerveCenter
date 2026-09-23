using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;
using System.Text.Json;

namespace SbConsole.Core.Connections;

public sealed record CreateConnectionCommand(
    string Name, string Kind, string Secret, IReadOnlyList<string> Tags, string Actor);

public sealed class CreateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock,
    IEnumerable<IPlugin> plugins)
{
    public async Task<Result<Guid>> HandleAsync(CreateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result<Guid>.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        // No plugin matching cmd.Kind is not an error here (unlike TestConnectionCommandHandler,
        // which genuinely needs a plugin to test against) -- a summary is a nice-to-have display
        // aid, not a requirement to save a connection at all.
        var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == cmd.Kind);
        var summary = plugin?.GetConnectionSummary(cmd.Secret) ?? new Dictionary<string, string>();

        var connection = new Connection
        {
            Name = cmd.Name,
            Kind = cmd.Kind,
            SecretCiphertext = protector.Protect(cmd.Secret),
            SummaryJson = JsonSerializer.Serialize(summary),
        };
        connection.Id = Guid.NewGuid();
        connection.TagsCsv = string.Join(',', cmd.Tags);
        connection.CreatedAt = clock.GetUtcNow();
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
