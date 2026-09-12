using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed class ListConnectionsQueryHandler(IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<IReadOnlyList<ConnectionInfo>> HandleAsync(string? kind = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Connections.AsNoTracking();
        if (kind is not null)
        {
            query = query.Where(c => c.Kind == kind);
        }

        var rows = await query.OrderBy(c => c.Name).ToListAsync(ct);
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags, c.LastTestSucceeded, c.LastTestedAt, c.LastTestError)).ToList();
    }
}
