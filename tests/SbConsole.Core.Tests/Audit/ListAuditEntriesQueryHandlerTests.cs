using FluentAssertions;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class ListAuditEntriesQueryHandlerTests
{
    private static AuditEntry Entry(string actor, string action, string target, ActionRisk risk, bool succeeded, DateTimeOffset at) => new()
    {
        At = at,
        Actor = actor,
        Action = action,
        Target = target,
        Risk = risk,
        Succeeded = succeeded,
    };

    private static async Task SeedAsync(TestDb testDb, params AuditEntry[] entries)
    {
        await using var db = testDb.CreateDbContext();
        db.AuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Returns_newest_first_with_total_count()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "queue.purge", "a", ActionRisk.Destructive, true, now.AddMinutes(-1)),
            Entry("admin", "auth.login", "-", ActionRisk.Safe, true, now));

        var page = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery());

        page.TotalCount.Should().Be(2);
        page.Entries.Select(e => e.Action).Should().Equal("auth.login", "queue.purge");
    }

    [Fact]
    public async Task Filters_by_risk_and_actor_and_target_substring()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "queue.purge", "payments-dlq", ActionRisk.Destructive, true, now),
            Entry("api", "message.peek", "orders-inbound", ActionRisk.Safe, true, now));

        var result = await new ListAuditEntriesQueryHandler(testDb)
            .HandleAsync(new AuditQuery(Actor: "admin", Risk: ActionRisk.Destructive, TargetContains: "payments"));

        result.Entries.Should().ContainSingle(e => e.Target == "payments-dlq");
    }

    [Fact]
    public async Task Filters_by_exact_action()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "connection.test", "sb-eu-prod", ActionRisk.Safe, false, now),
            Entry("admin", "queue.purge", "payments-dlq", ActionRisk.Destructive, true, now));

        var result = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(Action: "connection.test"));

        result.Entries.Should().ContainSingle(e => e.Target == "sb-eu-prod");
    }

    [Fact]
    public async Task Filters_by_date_range()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "a", "t", ActionRisk.Safe, true, now.AddDays(-10)),
            Entry("admin", "b", "t", ActionRisk.Safe, true, now));

        var result = await new ListAuditEntriesQueryHandler(testDb)
            .HandleAsync(new AuditQuery(From: now.AddDays(-1), To: now.AddDays(1)));

        result.Entries.Should().ContainSingle(e => e.Action == "b");
    }

    [Fact]
    public async Task Pages_results()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb, Enumerable.Range(0, 5)
            .Select(i => Entry("admin", $"action-{i}", "t", ActionRisk.Safe, true, now.AddMinutes(i)))
            .ToArray());

        var page1 = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(Page: 1, PageSize: 2));
        var page2 = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(Page: 2, PageSize: 2));

        page1.TotalCount.Should().Be(5);
        page1.Entries.Should().HaveCount(2);
        page2.Entries.Should().HaveCount(2);
        page1.Entries.Select(e => e.Action).Should().NotIntersectWith(page2.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task Combines_date_filter_with_another_filter_and_pagination()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "queue.purge", "a", ActionRisk.Destructive, true, now.AddDays(-10)), // out of date range
            Entry("admin", "queue.purge", "b", ActionRisk.Destructive, true, now.AddMinutes(-3)),
            Entry("admin", "queue.purge", "c", ActionRisk.Destructive, true, now.AddMinutes(-2)),
            Entry("admin", "message.peek", "d", ActionRisk.Safe, true, now.AddMinutes(-1))); // wrong risk

        var page = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(
            From: now.AddDays(-1), Risk: ActionRisk.Destructive, Page: 1, PageSize: 1));

        page.TotalCount.Should().Be(2); // b and c match date+risk; a is too old, d is wrong risk
        page.Entries.Should().ContainSingle(e => e.Target == "c"); // newest-first, page size 1
    }
}
