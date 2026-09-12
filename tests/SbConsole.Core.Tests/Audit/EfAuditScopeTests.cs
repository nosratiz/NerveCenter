using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class EfAuditScopeTests
{
    private static AuthenticationStateProvider AuthProviderFor(string? userName)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        var identity = userName is null
            ? new System.Security.Claims.ClaimsIdentity()
            : new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName)],
                "test");
        var state = new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity));
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    [Fact]
    public async Task Records_an_audit_entry_with_the_current_users_name()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor("alice"));

        await scope.RecordAsync("queue.create", "orders-inbound", ActionRisk.Mutating, succeeded: true);

        await using var db = testDb.CreateDbContext();
        var entry = await db.AuditEntries.SingleAsync();
        entry.Actor.Should().Be("alice");
        entry.Action.Should().Be("queue.create");
        entry.Target.Should().Be("orders-inbound");
        entry.Risk.Should().Be(ActionRisk.Mutating);
        entry.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Falls_back_to_admin_when_the_identity_has_no_name()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor(null));

        await scope.RecordAsync("queue.peek", "orders-inbound", ActionRisk.Safe, succeeded: true);

        await using var db = testDb.CreateDbContext();
        (await db.AuditEntries.SingleAsync()).Actor.Should().Be("admin");
    }

    [Fact]
    public async Task Records_the_detail_and_failure_outcome()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor("alice"));

        await scope.RecordAsync("queue.purge", "orders-inbound/$deadletter", ActionRisk.Destructive, succeeded: false, detail: "confirmation mismatch");

        await using var db = testDb.CreateDbContext();
        var entry = await db.AuditEntries.SingleAsync();
        entry.Succeeded.Should().BeFalse();
        entry.Detail.Should().Be("confirmation mismatch");
    }
}
