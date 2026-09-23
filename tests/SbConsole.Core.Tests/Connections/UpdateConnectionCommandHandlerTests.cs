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

public class UpdateConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string name = "bus", string secret = "Endpoint=sb://original")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), []);
        var result = await create.HandleAsync(new CreateConnectionCommand(name, "azure-servicebus", secret, ["dev"], "admin"));
        return result.Value;
    }

    [Fact]
    public async Task Renames_and_retags_without_touching_the_secret()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var protector = new AesGcmSecretProtector(Key);

        var result = await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus-renamed", ["prod"], NewSecret: null, "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.Name.Should().Be("bus-renamed");
        saved.Tags.Should().Equal("prod");
        protector.Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://original");
    }

    [Fact]
    public async Task Replaces_the_secret_when_provided()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var protector = new AesGcmSecretProtector(Key);

        await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus", ["dev"], NewSecret: "Endpoint=sb://replaced", "admin"));

        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        protector.Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://replaced");
    }

    [Fact]
    public async Task Writes_a_mutating_audit_entry_on_success()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        audit.ClearReceivedCalls();

        await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus-2", ["dev"], null, "admin"));

        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.update" && a.Risk == ActionRisk.Mutating && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_id_returns_not_found_and_does_not_audit()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();

        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(Guid.NewGuid(), "x", [], null, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renaming_to_an_existing_name_returns_conflict()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        await SeedAsync(testDb, audit, name: "taken");
        var id = await SeedAsync(testDb, audit, name: "renaming-this-one");

        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "taken", [], null, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.Conflict);
    }
}
