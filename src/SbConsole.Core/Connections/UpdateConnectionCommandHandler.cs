using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;
using System.Text.Json;

namespace SbConsole.Core.Connections;

public sealed record UpdateConnectionCommand(
    Guid Id, string Name, IReadOnlyList<string> Tags, string? NewSecret, string Actor);

public sealed class UpdateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock,
    IEnumerable<IPlugin> plugins)
{
    public async Task<Result> HandleAsync(UpdateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (connection is null)
        {
            return Result.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        if (connection.Name != cmd.Name && await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        connection.Name = cmd.Name;
        connection.TagsCsv = string.Join(',', cmd.Tags);
        if (cmd.NewSecret is not null)
        {
            connection.SecretCiphertext = protector.Protect(cmd.NewSecret);

            // Only recompute the summary when a new secret was actually provided -- there is no
            // plaintext to recompute it from otherwise (the old secret stays encrypted at rest and
            // is never decrypted just to refresh a display chip), so the existing SummaryJson is
            // left untouched.
            var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == connection.Kind);
            var summary = plugin?.GetConnectionSummary(cmd.NewSecret) ?? new Dictionary<string, string>();
            connection.SummaryJson = JsonSerializer.Serialize(summary);
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.update",
            Target = connection.Name,
            Risk = ActionRisk.Mutating,
            Succeeded = true,
        }, ct);

        return Result.Ok();
    }
}
