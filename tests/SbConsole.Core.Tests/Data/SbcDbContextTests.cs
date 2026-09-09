using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Data;

public class SbcDbContextTests
{
    [Fact]
    public async Task Can_roundtrip_all_entities()
    {
        using var testDb = new TestDb();
        var connectionId = Guid.NewGuid();

        await using (var db = testDb.CreateDbContext())
        {
            db.AppSettings.Add(new AppSetting { Key = "theme", Value = "dark" });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                Name = "prod-bus",
                Kind = "azure-servicebus",
                TagsCsv = "prod,east",
                SecretCiphertext = [1, 2, 3],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow,
                Actor = "admin",
                Action = "connection.create",
                Target = "prod-bus",
                Risk = ActionRisk.Mutating,
                Succeeded = true,
            });
            db.PluginDocuments.Add(new PluginDocument { PluginId = "servicebus", Key = "prefs", Json = "{}" });
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            (await db.AppSettings.SingleAsync(s => s.Key == "theme")).Value.Should().Be("dark");
            var conn = await db.Connections.SingleAsync(c => c.Id == connectionId);
            conn.Tags.Should().Equal("prod", "east");
            (await db.AuditEntries.SingleAsync()).Risk.Should().Be(ActionRisk.Mutating);
            (await db.PluginDocuments.SingleAsync()).PluginId.Should().Be("servicebus");
        }
    }

    [Fact]
    public async Task Connection_names_are_unique()
    {
        using var testDb = new TestDb();
        await using var db = testDb.CreateDbContext();
        db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "k", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "k", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
