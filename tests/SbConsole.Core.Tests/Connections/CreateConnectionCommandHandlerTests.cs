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

public class CreateConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private static CreateConnectionCommandHandler Handler(TestDb db, IAuditWriter audit) =>
        new(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider());

    [Fact]
    public async Task Creates_connection_with_encrypted_secret_and_audits()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();

        var result = await Handler(testDb, audit).HandleAsync(
            new CreateConnectionCommand("dev-bus", "azure-servicebus", "Endpoint=sb://x", ["dev"], "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync();
        saved.Name.Should().Be("dev-bus");
        saved.SecretCiphertext.Should().NotBeEquivalentTo(System.Text.Encoding.UTF8.GetBytes("Endpoint=sb://x"));
        new AesGcmSecretProtector(Key).Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://x");
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.create" && a.Target == "dev-bus"
                && a.Actor == "admin" && a.Risk == ActionRisk.Mutating && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_name_returns_conflict_and_does_not_audit_success()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var handler = Handler(testDb, audit);
        await handler.HandleAsync(new CreateConnectionCommand("dup", "k", "s", [], "admin"));
        audit.ClearReceivedCalls();

        var result = await handler.HandleAsync(new CreateConnectionCommand("dup", "k", "s2", [], "admin"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Category.Should().Be(ErrorCategory.Conflict);
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        await using var db = testDb.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(1);
    }
}
