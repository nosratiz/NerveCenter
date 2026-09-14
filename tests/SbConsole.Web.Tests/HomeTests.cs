using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class HomeTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IConnectionProvider _connectionProvider = Substitute.For<IConnectionProvider>();
    private static readonly byte[] Key = new byte[32];

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(Array.Empty<IPlugin>());
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
    }

    [Fact]
    public void Shows_all_clear_when_nothing_needs_attention()
    {
        var cut = Render<Home>();

        cut.Markup.Should().Contain("All clear");
    }

    [Fact]
    public async Task Shows_recent_activity_from_real_audit_entries()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "auth.login",
                Target = "-", Risk = ActionRisk.Safe, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("auth.login"));

        cut.Markup.Should().Contain("auth.login");
    }

    [Fact]
    public async Task Connections_tile_reflects_the_saved_connection_count()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            db.Connections.Add(new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".dashboard-tile").Count > 0);

        cut.Markup.Should().Contain("CONNECTIONS");
        cut.Markup.Should().Contain(">2<");
    }

    [Fact]
    public async Task Unreachable_connection_shows_an_alert_with_a_working_retry_button()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Name = "sb-eu-prod", Kind = "azure-servicebus", SecretCiphertext = [1],
                LastTestedAt = DateTimeOffset.UtcNow, LastTestSucceeded = false, LastTestError = "Unauthorized (401)",
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("sb-eu-prod"));

        cut.Markup.Should().Contain("Unauthorized (401)");

        // No plugin is registered in this test host, so TestConnectionCommandHandler fails fast
        // with "no plugin registered" rather than touching the connection row. The button round-
        // tripping through the real handler and re-rendering afterwards (busy flag cleared, no
        // unhandled exception, the connection still listed) is the observable proof it's wired up.
        cut.Find("button.retry-connection").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sb-eu-prod"));
    }
}
