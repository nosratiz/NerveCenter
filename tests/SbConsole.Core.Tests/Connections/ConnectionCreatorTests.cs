using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

/// <summary>SQLite plugin design spec §13: the SDK seam a plugin uses to save a connection it just made.</summary>
public sealed class ConnectionCreatorTests : IDisposable
{
    private static readonly byte[] Key = new byte[32];
    private readonly TestDb _db = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public void Dispose() => _db.Dispose();

    private static AuthenticationStateProvider SignedIn(string? userName)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        var identity = userName is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], "test");
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity))));
        return provider;
    }

    private ConnectionCreator Creator(AuthenticationStateProvider? auth = null, params IPlugin[] plugins)
    {
        IPlugin[] registered = plugins.Length == 0 ? [new FakePlugin("sqlite")] : plugins;
        var create = new CreateConnectionCommandHandler(_db, new AesGcmSecretProtector(Key), _audit, new FakeTimeProvider(), registered);
        return new ConnectionCreator(create, registered, auth ?? SignedIn("alice"));
    }

    [Fact]
    public async Task Creates_the_connection_through_the_normal_create_path_as_the_signed_in_user()
    {
        var result = await Creator().CreateAsync("sqlite-new", "sqlite", "Path=/sqlite/new.db;Mode=ReadWrite", ["prod", " team "]);

        var id = result.Should().BeOfType<ConnectionCreateResult.Created>().Subject.Id;
        await using var db = _db.CreateDbContext();
        var row = await db.Connections.SingleAsync();
        row.Id.Should().Be(id);
        row.Name.Should().Be("sqlite-new");
        row.Kind.Should().Be("sqlite");
        row.TagsCsv.Should().Be("prod,team");
        new AesGcmSecretProtector(Key).Unprotect(row.SecretCiphertext).Should().Be("Path=/sqlite/new.db;Mode=ReadWrite");
        row.Summary.Should().ContainKey("Path");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.create" && a.Actor == "alice" && a.Target == "sqlite-new" && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_taken_name_is_a_conflict_and_changes_nothing()
    {
        var creator = Creator();
        await creator.CreateAsync("sqlite-new", "sqlite", "Path=/sqlite/a.db", []);
        _audit.ClearReceivedCalls();

        var result = await creator.CreateAsync("sqlite-new", "sqlite", "Path=/sqlite/b.db", []);

        result.Should().BeOfType<ConnectionCreateResult.Conflict>();
        await using var db = _db.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(1);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact]
    public async Task A_kind_no_registered_plugin_handles_is_invalid()
    {
        var result = await Creator().CreateAsync("kafka-x", "kafka", "bootstrap=localhost:9092", []);

        result.Should().BeOfType<ConnectionCreateResult.Invalid>().Which.Error.Should().Contain("kafka");
        await using var db = _db.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("", "Path=/sqlite/a.db")]
    [InlineData("   ", "Path=/sqlite/a.db")]
    [InlineData("sqlite-a", "")]
    public async Task A_blank_name_or_secret_is_invalid(string name, string secret)
    {
        var result = await Creator().CreateAsync(name, "sqlite", secret, []);

        result.Should().BeOfType<ConnectionCreateResult.Invalid>();
        await using var db = _db.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_name_is_trimmed()
    {
        await Creator().CreateAsync("  sqlite-new  ", "sqlite", "Path=/sqlite/new.db", []);

        await using var db = _db.CreateDbContext();
        (await db.Connections.SingleAsync()).Name.Should().Be("sqlite-new");
    }

    [Fact]
    public async Task Outside_a_signed_in_circuit_the_actor_is_system()
    {
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Throws(new InvalidOperationException("no circuit"));

        await Creator(auth).CreateAsync("sqlite-new", "sqlite", "Path=/sqlite/new.db", []);

        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(a => a.Actor == "system"), Arg.Any<CancellationToken>());
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

        public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret) =>
            new Dictionary<string, string> { ["Path"] = secret };
    }
}
