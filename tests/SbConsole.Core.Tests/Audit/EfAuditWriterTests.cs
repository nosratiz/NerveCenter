using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class EfAuditWriterTests
{
    [Fact]
    public async Task Persists_the_entry()
    {
        using var testDb = new TestDb();
        var writer = new EfAuditWriter(testDb);

        await writer.WriteAsync(new AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            Actor = "admin",
            Action = "queue.purge",
            Target = "orders/$deadletter",
            Risk = ActionRisk.Destructive,
            Succeeded = false,
            Detail = "confirmation mismatch",
        });

        await using var db = testDb.CreateDbContext();
        var saved = await db.AuditEntries.SingleAsync();
        saved.Action.Should().Be("queue.purge");
        saved.Risk.Should().Be(ActionRisk.Destructive);
        saved.Succeeded.Should().BeFalse();
    }
}
