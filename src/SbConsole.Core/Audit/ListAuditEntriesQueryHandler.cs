using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Audit;

public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Actor = null,
    ActionRisk? Risk = null,
    string? TargetContains = null,
    int Page = 1,
    int PageSize = 25);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int TotalCount);

public sealed class ListAuditEntriesQueryHandler(IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<AuditPage> HandleAsync(AuditQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queryable = db.AuditEntries.AsNoTracking();

        // Apply server-side filters (those that SQLite can handle)
        if (query.Actor is { } actor) queryable = queryable.Where(e => e.Actor == actor);
        if (query.Risk is { } risk) queryable = queryable.Where(e => e.Risk == risk);
        if (query.TargetContains is { } target) queryable = queryable.Where(e => e.Target.Contains(target));

        // Switch to client-side for DateTimeOffset filtering and ordering
        var filtered = queryable.AsEnumerable()
            .Where(e =>
            {
                if (query.From is { } from && e.At < from) return false;
                if (query.To is { } to && e.At > to) return false;
                return true;
            })
            .OrderByDescending(e => e.At)
            .AsQueryable();

        var totalCount = filtered.Count();
        var entries = filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new AuditPage(entries, totalCount);
    }
}
