using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

public class DeleteConnectionCommandHandlerTests
{
    [Fact]
    public async Task Deletes_and_audits_as_destructive()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), audit, new FakeTimeProvider());
        var created = await create.HandleAsync(new CreateConnectionCommand("gone", "k", "s", [], "admin"));

        var result = await new DeleteConnectionCommandHandler(testDb, audit, new FakeTimeProvider())
            .HandleAsync(new DeleteConnectionCommand(created.Value, "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(0);
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.delete" && a.Risk == ActionRisk.Destructive && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_id_returns_not_found()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();

        var result = await new DeleteConnectionCommandHandler(testDb, audit, new FakeTimeProvider())
            .HandleAsync(new DeleteConnectionCommand(Guid.NewGuid(), "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }
}
