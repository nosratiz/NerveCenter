using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SbConsole.Core.Data;

/// <summary>Used only by `dotnet ef` at design time.</summary>
public sealed class SbcDbContextDesignFactory : IDesignTimeDbContextFactory<SbcDbContext>
{
    public SbcDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite("Data Source=design.db").Options);
}
