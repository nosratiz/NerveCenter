using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
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

        var cut = Render<NavMenu>();

        cut.Markup.Should().Contain("Connections");
        cut.Markup.Should().Contain("Audit");
        cut.Markup.Should().Contain("Fake Plugin");
        cut.Markup.Should().Contain("/p/fake/queues");
    }
}
