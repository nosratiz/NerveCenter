using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public sealed record PluginSummary(string Id, string DisplayName, string Version, PluginContribution Contribution, int ConnectionsInUse);

public sealed class ListPluginsQueryHandler(IEnumerable<IPlugin> plugins, IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<IReadOnlyList<PluginSummary>> HandleAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var countsByKind = await db.Connections.AsNoTracking()
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Kind, g => g.Count, ct);

        return plugins
            .Select(p => new PluginSummary(
                p.Id,
                p.DisplayName,
                p.Version,
                p.Contribution,
                countsByKind.GetValueOrDefault(p.ConnectionKind, 0)))
            .ToList();
    }
}
