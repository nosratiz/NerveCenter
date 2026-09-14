using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

public class TestConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private sealed class FakePlugin(string kind, ConnectionTestResult result, string? expectedSecret = null) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => kind;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }

        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
        {
            if (expectedSecret is not null)
            {
                secret.Should().Be(expectedSecret);
            }

            return Task.FromResult(result);
        }
    }

    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string kind = "azure-servicebus", string secret = "Endpoint=sb://x")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider());
        var result = await create.HandleAsync(new CreateConnectionCommand("bus", kind, secret, [], "admin"));
        return result.Value;
    }

    [Fact]
    public async Task Successful_test_persists_success_and_audits_as_safe()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit, secret: "Endpoint=sb://real");
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(true), expectedSecret: "Endpoint=sb://real");
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.LastTestSucceeded.Should().BeTrue();
        saved.LastTestedAt.Should().NotBeNull();
        saved.LastTestError.Should().BeNull();
        await audit.Received(1).WriteAsync(
            Arg.Is<global::SbConsole.Core.Data.Entities.AuditEntry>(a => a.Action == "connection.test" && a.Risk == ActionRisk.Safe && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_test_persists_the_error_and_audits_as_failed()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(false, "Unauthorized (401)"));
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        result.IsSuccess.Should().BeTrue(); // the COMMAND succeeded (it ran); the TEST itself failed
        result.Value!.Success.Should().BeFalse();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.LastTestSucceeded.Should().BeFalse();
        saved.LastTestError.Should().Be("Unauthorized (401)");
        await audit.Received(1).WriteAsync(
            Arg.Is<global::SbConsole.Core.Data.Entities.AuditEntry>(a => a.Action == "connection.test" && a.Risk == ActionRisk.Safe && !a.Succeeded && a.Detail == "Unauthorized (401)"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_not_found()
    {
        using var testDb = new TestDb();

        var result = await new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [], Substitute.For<IAuditWriter>(), new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance)
            .HandleAsync(new TestConnectionCommand(Guid.NewGuid(), "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
    }

    [Fact]
    public async Task No_plugin_registered_for_the_connections_kind_returns_not_found()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit, kind: "kafka");

        var result = await new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [], audit, new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance)
            .HandleAsync(new TestConnectionCommand(id, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
    }

    [Fact]
    public async Task A_plugins_oversized_error_is_capped_before_it_is_persisted_or_audited()
    {
        // LastTestError is a persisted column rendered straight into the Connections page's Status
        // cell, and Detail is an audit column. A plugin is free to hand back whatever its SDK
        // produced -- live testing produced a 2,608-character message that wrapped five lines and
        // squeezed every other column -- so this handler caps it rather than trusting each plugin.
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var oversized = new string('x', 3_000);
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(false, oversized));
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        var capped = new string('x', FriendlyError.MaxLength) + FriendlyError.TruncationMarker;
        result.Value!.ErrorMessage.Should().Be(capped);
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.LastTestError.Should().Be(capped);
        await audit.Received(1).WriteAsync(
            Arg.Is<global::SbConsole.Core.Data.Entities.AuditEntry>(a => a.Action == "connection.test" && a.Detail == capped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_short_error_is_persisted_verbatim()
    {
        // The cap must not disturb the distinct, fixed messages docs/design.md §6 asks the plugin
        // for ("Unauthorized (401)", "Namespace unreachable", "Invalid connection string format").
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(false, "Namespace unreachable"));
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider(), NullLogger<TestConnectionCommandHandler>.Instance);

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        result.Value!.ErrorMessage.Should().Be("Namespace unreachable");
        await using var db = testDb.CreateDbContext();
        (await db.Connections.SingleAsync(c => c.Id == id)).LastTestError.Should().Be("Namespace unreachable");
    }
}
