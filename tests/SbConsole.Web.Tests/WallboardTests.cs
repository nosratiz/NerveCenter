using System.Text.Json;
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

public class WallboardTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IConnectionProvider _connectionProvider = Substitute.For<IConnectionProvider>();
    private readonly IPluginStoreFactory _pluginStoreFactory = Substitute.For<IPluginStoreFactory>();
    private static readonly byte[] Key = new byte[32];
    private IPlugin[] _plugins = [];

    public WallboardTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(_ => _plugins);
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(Substitute.For<IPluginStore>());
        Services.AddSingleton(_pluginStoreFactory);
        Services.AddSingleton<WallboardSnapshotLoader>();
    }

    [Fact]
    public async Task Tiles_reflect_aggregated_metrics_across_connections()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            db.Connections.Add(new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Dead-lettered", 100) });
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Find(".tile-dead-lettered").TextContent.Contains("200")); // 100 + 100 across two connections

        cut.Find(".tile-dead-lettered").TextContent.Should().Contain("200");
    }

    [Fact]
    public async Task Namespace_backlog_groups_dead_lettered_count_by_connection()
    {
        Guid uk, staging;
        await using (var db = _testDb.CreateDbContext())
        {
            var ukConn = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var stagingConn = new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(ukConn, stagingConn);
            await db.SaveChangesAsync();
            uk = ukConn.Id;
            staging = stagingConn.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([new("Dead-lettered", 214)]));
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("sb-uk-prod"));

        var rows = cut.FindAll(".namespace-backlog-row");
        var ukRow = rows.Single(r => r.TextContent.Contains("sb-uk-prod"));
        ukRow.TextContent.Should().Contain("214");
        cut.Markup.Should().Contain("sb-staging");
    }

    [Fact]
    public async Task Oldest_dead_letter_tile_shows_the_minimum_across_connections()
    {
        Guid uk, staging;
        await using (var db = _testDb.CreateDbContext())
        {
            var ukConn = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var stagingConn = new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(ukConn, stagingConn);
            await db.SaveChangesAsync();
            uk = ukConn.Id;
            staging = stagingConn.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        Services.AddSingleton(clock);
        Services.AddSingleton<TimeProvider>(clock);

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>()).Returns(new List<PluginDashboardMetric>());
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>()).Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(uk, "secret", Arg.Any<CancellationToken>())
            .Returns(new OldestDeadLetterEntry("payments-dlq", clock.GetUtcNow().AddHours(-4).AddMinutes(-12), 214));
        plugin.GetOldestDeadLetterAsync(staging, "secret", Arg.Any<CancellationToken>())
            .Returns(new OldestDeadLetterEntry("test-queue", clock.GetUtcNow().AddMinutes(-5), 1));
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Markup.Should().Contain("payments-dlq");
        cut.Markup.Should().NotContain("test-queue");
    }

    [Fact]
    public async Task Range_toggle_switches_between_the_1h_and_24h_bucket_sets()
    {
        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.FindAll(".range-toggle-1h").Count > 0);

        var pointCount1h = TrendPointCount(cut);

        cut.Find(".range-toggle-24h").Click();
        cut.WaitForAssertion(() => cut.Find(".range-toggle-24h").ClassList.Should().Contain("range-toggle-active"));

        // Not just the CSS class: the 24h tab's trend series must actually carry a different
        // number of data points (288 five-minute buckets) than the 1h tab (60 one-minute
        // buckets) -- proving the toggle really drives the chart, not only a class name.
        var pointCount24h = TrendPointCount(cut);
        pointCount24h.Should().NotBe(pointCount1h);

        cut.Find(".range-toggle-1h").Click();
        cut.WaitForAssertion(() => cut.Find(".range-toggle-1h").ClassList.Should().Contain("range-toggle-active"));

        TrendPointCount(cut).Should().Be(pointCount1h);
    }

    private static int TrendPointCount(IRenderedComponent<global::SbConsole.Web.Components.Pages.Wallboard> cut) =>
        cut.FindComponents<MudChart<double>>()
            .Select(c => c.Instance.ChartSeries.FirstOrDefault(s => s.Name == "Active"))
            .First(s => s is not null)!
            .Data.Count;

    [Fact]
    public async Task Unreachable_connection_shows_a_Fix_link_in_the_namespace_backlog()
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

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _plugins = [];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("sb-eu-prod"));

        cut.Find("a.namespace-fix-link").GetAttribute("href").Should().Be("/connections");
    }

    [Fact]
    public async Task Dead_lettered_delta_tile_shows_the_real_1h_delta_not_a_stale_24h_one()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        Services.AddSingleton(clock);
        Services.AddSingleton<TimeProvider>(clock);

        Guid connectionId;
        await using (var db = _testDb.CreateDbContext())
        {
            var connection = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        // A real 1h-old baseline of 5 and a current total of 20 -- the honest 1h delta is +15.
        // The pre-fix implementation read _buckets24h[0] (~24h old, before any history exists here,
        // so a baseline of 0) and would have shown a wrongly-labeled "+20 / 1h" instead.
        var history = new List<MetricSnapshotPoint>
        {
            new(clock.GetUtcNow().AddMinutes(-60), 0, 5),
            new(clock.GetUtcNow().AddSeconds(-10), 0, 20),
        };
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        store.GetAsync(MetricHistoryKey.For(connectionId, "orders-dlq"), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(history));
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(store);

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Dead-lettered", 20) });
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric> { new("orders-dlq", 0, 20) });
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Find(".tile-dead-lettered").TextContent.Contains("+15"));

        cut.Find(".tile-dead-lettered").TextContent.Should().Contain("+15 / 1h");
        cut.Find(".tile-dead-lettered").TextContent.Should().NotContain("+20 / 1h");
    }

    [Fact]
    public async Task Dead_lettered_delta_tile_shows_a_placeholder_before_the_first_load_completes()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        // Gate GetDashboardMetricsAsync so LoadAsync is still mid-flight when we inspect the first
        // render -- _deadLetterDeltaLastHour is still at its default (null), so the tile must show
        // the honest "no baseline yet" placeholder rather than a fabricated number like "0".
        var gate = new TaskCompletionSource<IReadOnlyList<PluginDashboardMetric>>();
        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>()).Returns(gate.Task);
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();

        cut.Find(".tile-dead-lettered").TextContent.Should().Contain("— / 1h");

        gate.SetResult(new List<PluginDashboardMetric>());
        cut.WaitForState(() => !cut.Find(".tile-dead-lettered").TextContent.Contains("— / 1h"));
    }

    [Fact]
    public async Task A_plugin_that_throws_marks_its_connection_unchecked_and_shows_a_warning()
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
            .Returns(Task.FromException<IReadOnlyList<PluginDashboardMetric>>(new InvalidOperationException("boom")));
        _plugins = [plugin];

        var cut = Render<global::SbConsole.Web.Components.Pages.Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("Couldn't check"));

        cut.Markup.Should().Contain("Couldn't check 1 connection(s)");
    }
}
