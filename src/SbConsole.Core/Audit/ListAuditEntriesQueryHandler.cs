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
        var filtered = db.AuditEntries.AsNoTracking().AsQueryable();

        if (query.From is { } from) filtered = filtered.Where(e => e.At >= from);
        if (query.To is { } to) filtered = filtered.Where(e => e.At <= to);
        if (query.Actor is { } actor) filtered = filtered.Where(e => e.Actor == actor);
        if (query.Risk is { } risk) filtered = filtered.Where(e => e.Risk == risk);
        if (query.TargetContains is { } target) filtered = filtered.Where(e => e.Target.Contains(target));

        var totalCount = await filtered.CountAsync(ct);
        var entries = await filtered
            .OrderByDescending(e => e.At)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new AuditPage(entries, totalCount);
    }
}
