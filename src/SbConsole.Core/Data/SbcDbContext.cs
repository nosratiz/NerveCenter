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
            e.HasIndex(a => a.At);
        });
        builder.Entity<PluginDocument>().HasKey(d => new { d.PluginId, d.Key });
    }
}
