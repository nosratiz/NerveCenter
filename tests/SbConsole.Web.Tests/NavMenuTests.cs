using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
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

    private IPlugin AddPlugin(string id, string displayName, string? monogram, params PluginNavItem[] items)
    {
        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns(id);
        plugin.DisplayName.Returns(displayName);
        plugin.Version.Returns("0.1.3");
        plugin.Monogram.Returns(monogram);
        plugin.ConnectionKind.Returns(id);
        plugin.NavItems.Returns(items.ToList());
        return plugin;
    }

    private IConnectionProvider RegisterTwoPlugins(out IPlugin alpha, out IPlugin beta)
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        alpha = AddPlugin("alpha", "Alpha Bus", "AB", new PluginNavItem("Queues", "/p/alpha/queues"));
        beta = AddPlugin("beta", "Beta", null, new PluginNavItem("Keys", "/p/beta/keys"));
        Services.AddSingleton(new PluginRegistry([alpha, beta]));
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());
        Services.AddSingleton(connections);
        return connections;
    }

    [Fact]
    public void The_rail_shows_one_monogram_icon_per_plugin_falling_back_to_the_display_name()
    {
        RegisterTwoPlugins(out _, out _);

        var cut = Render<NavMenu>();

        var rail = cut.FindAll(".nav-rail-plugin");
        rail.Select(e => e.TextContent.Trim()).Should().Equal("AB", "BE");
        rail[0].GetAttribute("title").Should().Be("Alpha Bus");
    }

    [Fact]
    public void The_drawer_shows_only_the_plugin_owning_the_current_url()
    {
        RegisterTwoPlugins(out _, out _);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/p/beta/keys");

        var cut = Render<NavMenu>();

        cut.Find(".nav-drawer-title").TextContent.Should().Be("Beta");
        cut.Find(".nav-drawer").InnerHtml.Should().Contain("/p/beta/keys").And.NotContain("/p/alpha/queues");
        cut.Find(".nav-rail-plugin.active").GetAttribute("title").Should().Be("Beta");
    }

    [Fact]
    public void Navigating_to_another_plugins_page_switches_the_drawer()
    {
        RegisterTwoPlugins(out _, out _);
        var cut = Render<NavMenu>();
        cut.Find(".nav-drawer-title").TextContent.Should().Be("Alpha Bus");

        cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/p/beta/keys"));

        cut.WaitForAssertion(() => cut.Find(".nav-drawer-title").TextContent.Should().Be("Beta"));
    }

    [Fact]
    public void A_host_page_keeps_the_last_selected_plugin_in_the_drawer()
    {
        RegisterTwoPlugins(out _, out _);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/p/beta/keys");
        var cut = Render<NavMenu>();

        cut.InvokeAsync(() => nav.NavigateTo("/audit"));

        cut.WaitForAssertion(() => cut.Find(".nav-drawer-title").TextContent.Should().Be("Beta"));
        cut.FindAll(".nav-rail-plugin.active").Should().BeEmpty();
    }

    [Fact]
    public async Task A_badge_on_a_plugin_not_shown_in_the_drawer_still_surfaces_as_a_rail_dot()
    {
        var connections = RegisterTwoPlugins(out _, out var beta);
        var connectionId = Guid.NewGuid();
        connections.ListAsync("beta", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "beta-conn", "beta", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        beta.GetNavBadgeAsync("/p/beta/keys", "secret", Arg.Any<CancellationToken>()).Returns(3);

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        cut.Find(".nav-drawer-title").TextContent.Should().Be("Alpha Bus");
        cut.Find(".nav-rail-plugin[title='Beta'] .nav-rail-dot").Should().NotBeNull();
        cut.FindAll(".nav-rail-plugin[title='Alpha Bus'] .nav-rail-dot").Should().BeEmpty();
    }

    [Fact]
    public async Task The_drawer_lists_the_active_plugins_connections_with_their_test_status()
    {
        var connections = RegisterTwoPlugins(out _, out _);
        connections.ListAsync("alpha", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>
        {
            new(Guid.NewGuid(), "alpha-prod", "alpha", [], LastTestSucceeded: true),
            new(Guid.NewGuid(), "alpha-local", "alpha", []),
        });

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        var rows = cut.FindAll(".nav-connection");
        rows.Select(r => r.TextContent.Trim()).Should().Equal("alpha-prod", "alpha-local");
        rows[0].QuerySelector(".nav-status-dot")!.ClassList.Should().Contain("ok");
        rows[1].QuerySelector(".nav-status-dot")!.ClassList.Should().Contain("unknown");
    }

    [Fact]
    public void The_drawer_collapses_to_leave_just_the_rail()
    {
        RegisterTwoPlugins(out _, out _);
        var cut = Render<NavMenu>();

        cut.Find(".nav-drawer-toggle").Click();

        cut.FindAll(".nav-drawer").Should().BeEmpty();
        cut.FindAll(".nav-rail-plugin").Should().HaveCount(2);
    }

    [Fact]
    public void Connections_are_listed_without_waiting_on_slow_badge_calls()
    {
        var connections = RegisterTwoPlugins(out var alpha, out _);
        var connectionId = Guid.NewGuid();
        var listed = new TaskCompletionSource<IReadOnlyList<ConnectionInfo>>();
        connections.ListAsync("alpha", Arg.Any<CancellationToken>()).Returns(listed.Task);
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        alpha.GetNavBadgeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<int?>().Task); // an unreachable broker: never answers

        var cut = Render<NavMenu>();
        listed.SetResult([new(connectionId, "alpha-prod", "alpha", [])]); // the DB read finishes after first render

        cut.WaitForAssertion(() => cut.FindAll(".nav-connection").Select(r => r.TextContent.Trim()).Should().Equal("alpha-prod"));
    }
}
