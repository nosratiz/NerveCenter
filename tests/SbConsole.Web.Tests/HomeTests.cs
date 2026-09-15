using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
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
using SbConsole.Web.Wallboard;

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

    private IPlugin[] _plugins = [];
    private readonly IPluginStoreFactory _pluginStoreFactory = Substitute.For<IPluginStoreFactory>();

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(_ => _plugins);
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(Substitute.For<IPluginStore>());
        Services.AddSingleton(_pluginStoreFactory);
        Services.AddSingleton<WallboardSnapshotLoader>();
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
    public async Task Dashboard_tiles_are_flat_with_no_shadow()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".dashboard-tile").Count > 0);

        var tile = cut.Find(".dashboard-tile");
        tile.ClassList.Should().Contain("mud-elevation-0");
        tile.ClassList.Should().NotContain("mud-elevation-1");
        tile.GetAttribute("style").Should().Contain("--mud-palette-lines-default");
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

    [Fact]
    public async Task Plugin_reported_problems_render_in_Needs_attention()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Warning", "payments-dlq", "214 dead-lettered", "/p/azure-servicebus/dead-letter"),
            });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Markup.Should().Contain("payments-dlq");
        cut.Markup.Should().Contain("214 dead-lettered");
        cut.Markup.Should().Contain("sb-uk-prod");
    }

    [Fact]
    public async Task All_clear_requires_both_zero_unreachable_connections_and_zero_plugin_problems()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".dashboard-tile").Count > 0);

        cut.Markup.Should().Contain("All clear");
    }

    [Fact]
    public async Task Header_summary_reflects_connection_count_and_plugin_metrics()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric>
            {
                new("Queues", 148), new("Topics", 23), new("Subscriptions", 91), new("Dead-lettered", 312),
            });
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("148 q"));

        cut.Markup.Should().Contain("1 conn · 148 q · 23 t / 91 sub · 312 dlq");
    }

    [Fact]
    public async Task Namespace_chip_turns_red_when_its_connection_has_an_open_problem()
    {
        Guid problemConnectionId;
        Guid cleanConnectionId;
        await using (var db = _testDb.CreateDbContext())
        {
            var problematic = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var clean = new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(problematic, clean);
            await db.SaveChangesAsync();
            problemConnectionId = problematic.Id;
            cleanConnectionId = clean.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(problemConnectionId, "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem> { new("Warning", "payments-dlq", "5 dead-lettered", null) });
        plugin.GetDashboardProblemsAsync(cleanConnectionId, "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        var chips = cut.FindComponents<MudChip<string>>();
        var problemChip = chips.Single(c => c.Markup.Contains("sb-uk-prod"));
        var cleanChip = chips.Single(c => c.Markup.Contains("sb-dev"));
        problemChip.Instance.Color.Should().Be(Color.Error);
        cleanChip.Instance.Color.Should().Be(Color.Success);
    }

    [Fact]
    public async Task A_plugin_that_throws_marks_its_connection_unchecked_and_shows_a_warning_instead_of_All_clear()
    {
        Guid connectionId;
        await using (var db = _testDb.CreateDbContext())
        {
            var connection = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PluginDashboardProblem>>(new InvalidOperationException("boom")));
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Couldn't check"));

        cut.Markup.Should().NotContain("All clear");
        cut.Markup.Should().Contain("Couldn't check 1 connection(s)");

        var chips = cut.FindComponents<MudChip<string>>();
        var chip = chips.Single(c => c.Markup.Contains("sb-uk-prod"));
        chip.Instance.Color.Should().Be(Color.Warning);
    }

    [Fact]
    public async Task Needs_attention_sorts_unreachable_connections_before_plugin_errors_before_plugin_warnings()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Name = "sb-eu-prod", Kind = "azure-servicebus", SecretCiphertext = [1],
                LastTestedAt = DateTimeOffset.UtcNow, LastTestSucceeded = false, LastTestError = "Unauthorized (401)",
            });
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Error", "namespace-outage", "connectivity lost", null),
                new("Warning", "payments-dlq", "5 dead-lettered", null),
            });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        var unreachableIndex = cut.Markup.IndexOf("sb-eu-prod", StringComparison.Ordinal);
        var pluginErrorIndex = cut.Markup.IndexOf("namespace-outage", StringComparison.Ordinal);
        var pluginWarningIndex = cut.Markup.IndexOf("payments-dlq", StringComparison.Ordinal);

        unreachableIndex.Should().BeGreaterThan(-1);
        pluginErrorIndex.Should().BeGreaterThan(unreachableIndex);
        pluginWarningIndex.Should().BeGreaterThan(pluginErrorIndex);
    }

    [Fact]
    public async Task Activity_panel_still_shows_real_audit_entries_and_links_to_the_audit_log()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "queue.purge",
                Target = "sb-uk-prod / payments-dlq", Risk = ActionRisk.Destructive, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("queue.purge"));

        cut.Markup.Should().Contain("queue.purge");
        cut.Markup.Should().Contain("sb-uk-prod / payments-dlq");
        cut.Find("a[href='/audit']").Should().NotBeNull();
    }

    [Fact]
    public void Header_links_to_the_wallboard()
    {
        var cut = Render<Home>();

        cut.Find("a.open-wallboard").GetAttribute("href").Should().Be("/wallboard");
    }

    [Fact]
    public async Task Dashboard_embeds_a_wallboard_widget_with_a_link_to_the_full_page()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Dead-lettered", 42) });
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".wallboard-widget .tile-dead-lettered").Count > 0);

        cut.Find(".wallboard-widget .tile-dead-lettered").TextContent.Should().Contain("42");
        cut.Find("a.open-full-wallboard").GetAttribute("href").Should().Be("/wallboard");
    }
}
