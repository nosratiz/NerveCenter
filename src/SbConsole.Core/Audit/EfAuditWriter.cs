using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Audit;

public sealed class EfAuditWriter(IDbContextFactory<SbcDbContext> dbFactory) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
