using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Data;

public sealed class SbcDbContext(DbContextOptions<SbcDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<PluginDocument> PluginDocuments => Set<PluginDocument>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppSetting>().HasKey(s => s.Key);
        builder.Entity<Connection>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Name).IsUnique();
            e.Ignore(c => c.Tags);
        });
        builder.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Risk).HasConversion<string>();
            // Store as UTC ticks (not the default DateTimeOffset TEXT mapping) because EF Core 10 +
            // the SQLite provider cannot translate either WHERE comparisons or ORDER BY over a raw
            // DateTimeOffset-typed column to SQL (both throw/fail to translate, e.g. "SQLite does not
            // support expressions of type 'DateTimeOffset' in ORDER BY clauses") -- the long-typed
            // ticks column is required for both filtering and sorting to work at all. All values are
            // written via this app's UTC TimeProvider-based clock, so round-tripping through UTC
            // ticks loses no information.
            e.Property(a => a.At).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            e.HasIndex(a => a.At);
        });
        builder.Entity<PluginDocument>().HasKey(d => new { d.PluginId, d.Key });
    }
}
