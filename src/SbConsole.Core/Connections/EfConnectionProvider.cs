using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed class EfConnectionProvider(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector) : IConnectionProvider
{
    public async Task<IReadOnlyList<ConnectionInfo>> ListAsync(string kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Connections.AsNoTracking()
            .Where(c => c.Kind == kind)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags)).ToList();
    }

    public async Task<string?> GetSecretAsync(Guid connectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct);
        return row is null ? null : protector.Unprotect(row.SecretCiphertext);
    }
}
