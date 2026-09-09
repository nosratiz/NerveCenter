using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Settings;

public sealed class DbSettings(IDbContextFactory<SbcDbContext> dbFactory) : ISettings
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }
}
