using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

public sealed class SeedDemoConnectionsCommandHandlerTests : IDisposable
{
    private static readonly byte[] Key = new byte[32];
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sbc-demo-{Guid.NewGuid()}.json");
    private readonly TestDb _db = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_file);
    }

    private SeedDemoConnectionsCommandHandler Handler(string? file, params IPlugin[] plugins)
    {
        var options = new BootstrapOptions("/data/sbc.db", Key, "pw", "api", "http://localhost:5080")
        {
            DemoConnectionsFile = file,
        };
        var create = new CreateConnectionCommandHandler(_db, new AesGcmSecretProtector(Key), _audit, new FakeTimeProvider(), plugins);
        return new SeedDemoConnectionsCommandHandler(options, create, plugins, NullLogger<SeedDemoConnectionsCommandHandler>.Instance);
    }

    private void WriteFile(string json) => File.WriteAllText(_file, json);

    [Fact]
    public async Task Does_nothing_when_the_feature_is_off()
    {
        var result = await Handler(null, new FakePlugin("sqlite")).HandleAsync();

        result.Should().Be(new DemoSeedResult(0, 0));
        await using var db = _db.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Creates_missing_connections_encrypted_with_summary_tags_and_a_system_audit_row()
    {
        WriteFile("""
            [
              { "name": "sqlite-reports", "kind": "sqlite", "secret": "Path=/sqlite/reports.db;Mode=ReadWrite", "tags": ["prod"] },
              { "name": "sqlite-new", "kind": "sqlite", "secret": "Path=/sqlite/new.db;Mode=ReadWrite" }
            ]
            """);

        var result = await Handler(_file, new FakePlugin("sqlite")).HandleAsync();

        result.Should().Be(new DemoSeedResult(Created: 2, Skipped: 0));
        await using var db = _db.CreateDbContext();
        var reports = await db.Connections.SingleAsync(c => c.Name == "sqlite-reports");
        reports.Kind.Should().Be("sqlite");
        reports.TagsCsv.Should().Be("prod");
        new AesGcmSecretProtector(Key).Unprotect(reports.SecretCiphertext).Should().Be("Path=/sqlite/reports.db;Mode=ReadWrite");
        reports.Summary.Should().ContainSingle(kv => kv.Key == "Path" && kv.Value == "/sqlite/reports.db;Mode=ReadWrite");
        (await db.Connections.SingleAsync(c => c.Name == "sqlite-new")).TagsCsv.Should().BeEmpty();
        await _audit.Received(2).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.create" && a.Actor == "system" && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Leaves_an_existing_connection_with_the_same_name_untouched()
    {
        var plugin = new FakePlugin("sqlite");
        await new CreateConnectionCommandHandler(_db, new AesGcmSecretProtector(Key), _audit, new FakeTimeProvider(), [plugin])
            .HandleAsync(new CreateConnectionCommand("sqlite-shop", "sqlite", "Path=/elsewhere.db", ["mine"], "admin"));
        _audit.ClearReceivedCalls();
        WriteFile("""[{ "name": "sqlite-shop", "kind": "sqlite", "secret": "Path=/sqlite/shop.db", "tags": ["staging"] }]""");

        var result = await Handler(_file, plugin).HandleAsync();

        result.Should().Be(new DemoSeedResult(Created: 0, Skipped: 1));
        await using var db = _db.CreateDbContext();
        var saved = await db.Connections.SingleAsync();
        new AesGcmSecretProtector(Key).Unprotect(saved.SecretCiphertext).Should().Be("Path=/elsewhere.db");
        saved.TagsCsv.Should().Be("mine");
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Running_twice_is_idempotent()
    {
        WriteFile("""[{ "name": "a", "kind": "sqlite", "secret": "Path=/a.db" }]""");
        var handler = Handler(_file, new FakePlugin("sqlite"));

        await handler.HandleAsync();
        var second = await handler.HandleAsync();

        second.Should().Be(new DemoSeedResult(Created: 0, Skipped: 1));
        await using var db = _db.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Skips_an_entry_whose_kind_has_no_plugin()
    {
        WriteFile("""
            [
              { "name": "mystery", "kind": "no-such-plugin", "secret": "x" },
              { "name": "ok", "kind": "sqlite", "secret": "Path=/ok.db" }
            ]
            """);

        var result = await Handler(_file, new FakePlugin("sqlite")).HandleAsync();

        result.Should().Be(new DemoSeedResult(Created: 1, Skipped: 1));
        await using var db = _db.CreateDbContext();
        (await db.Connections.Select(c => c.Name).ToListAsync()).Should().Equal("ok");
    }

    [Fact]
    public async Task Skips_an_entry_missing_its_name_kind_or_secret()
    {
        WriteFile("""
            [
              { "kind": "sqlite", "secret": "Path=/a.db" },
              { "name": "no-secret", "kind": "sqlite" },
              { "name": "  ", "kind": "sqlite", "secret": "Path=/b.db" },
              null
            ]
            """);

        var result = await Handler(_file, new FakePlugin("sqlite")).HandleAsync();

        result.Should().Be(new DemoSeedResult(Created: 0, Skipped: 4));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "name": "an object, not an array" }""")]
    [InlineData("")]
    public async Task An_invalid_file_does_not_throw_and_creates_nothing(string content)
    {
        WriteFile(content);

        var act = () => Handler(_file, new FakePlugin("sqlite")).HandleAsync();

        (await act.Should().NotThrowAsync()).Subject.Should().Be(new DemoSeedResult(0, 0));
    }

    [Fact]
    public async Task A_missing_file_does_not_throw()
    {
        var act = () => Handler(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.json"), new FakePlugin("sqlite")).HandleAsync();

        (await act.Should().NotThrowAsync()).Subject.Should().Be(new DemoSeedResult(0, 0));
    }

    private sealed class FakePlugin(string kind) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => kind;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));

        // Mimics a real plugin's summary so the test proves the summary goes through the normal create path.
        public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret) =>
            new Dictionary<string, string> { ["Path"] = secret["Path=".Length..] };
    }
}
