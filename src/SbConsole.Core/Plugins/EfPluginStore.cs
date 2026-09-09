using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public sealed class EfPluginStoreFactory(IDbContextFactory<SbcDbContext> dbFactory) : IPluginStoreFactory
{
    public IPluginStore For(string pluginId) => new EfPluginStore(dbFactory, pluginId);
}

internal sealed class EfPluginStore(IDbContextFactory<SbcDbContext> dbFactory, string pluginId) : IPluginStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        return doc?.Json;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        if (doc is null)
        {
            db.PluginDocuments.Add(new PluginDocument { PluginId = pluginId, Key = key, Json = value });
        }
        else
        {
            doc.Json = value;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        if (doc is null)
        {
            return false;
        }

        db.PluginDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
