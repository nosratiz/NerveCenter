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
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
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
        cut.WaitForState(() => cut.Markup.Contains("200")); // 100 + 100 across two connections

        cut.Markup.Should().Contain("200");
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

        cut.Markup.Should().Contain("sb-uk-prod");
        cut.Markup.Should().Contain("214");
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
}
