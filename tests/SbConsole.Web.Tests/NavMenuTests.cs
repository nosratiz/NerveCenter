using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Sdk;
using SbConsole.Web.Components.Layout;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class NavMenuTests : BunitContext
{
    private sealed class FakePlugin : IPlugin
    {
        public string Id => "fake";
        public string DisplayName => "Fake Plugin";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/fake/queues")];
        public string ConnectionKind => "fake";
        public string ConnectionKindDisplayName => "Fake Connection Kind";
        public PluginContribution Contribution => new(PageCount: 1, ActionCount: 1);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(Success: true));
    }

    [Fact]
    public void Renders_host_sections_and_plugin_nav_items()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new PluginRegistry([new FakePlugin()]));

        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();

        cut.Markup.Should().Contain("Connections");
        cut.Markup.Should().Contain("Audit");
        cut.Markup.Should().Contain("/plugins");
        cut.Markup.Should().Contain("Fake Plugin");
        cut.Markup.Should().Contain("/p/fake/queues");
    }

    [Fact]
    public async Task Shows_a_badge_for_a_nav_item_the_plugin_reports_a_count_for()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Dead-letter", "/p/fake/dead-letter") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "fake-conn", "fake", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        plugin.GetNavBadgeAsync("/p/fake/dead-letter", "secret", Arg.Any<CancellationToken>()).Returns(12);
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        cut.Markup.Should().Contain("12");
    }

    [Fact]
    public async Task Shows_no_badge_when_the_plugin_reports_null()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Queues", "/p/fake/queues") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        cut.FindAll(".nav-badge").Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_whose_secret_is_missing_is_skipped_not_fatal()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Dead-letter", "/p/fake/dead-letter") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "fake-conn", "fake", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        var act = async () => await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());

        await act.Should().NotThrowAsync();
        cut.Render();
        cut.FindAll(".nav-badge").Should().BeEmpty();
    }
}
