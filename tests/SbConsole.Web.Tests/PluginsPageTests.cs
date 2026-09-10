using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using CorePlugins = SbConsole.Core.Plugins;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class PluginsPageTests : BunitContext, IAsyncLifetime
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

    private sealed class FakePlugin : IPlugin
    {
        public string Id => "azure-servicebus";
        public string DisplayName => "Azure Service Bus";
        public string Version => "0.4.1";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public Type RootComponent => typeof(object);
        public string ConnectionKind => "azure-servicebus";
        public string ConnectionKindDisplayName => "Azure Service Bus";
        public PluginContribution Contribution => new(PageCount: 3, ActionCount: 8);
        public void ConfigureServices(IServiceCollection services) { }
    }

    private readonly TestDb _testDb = new();

    public PluginsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin()]);
        Services.AddSingleton<CorePlugins.ListPluginsQueryHandler>();
    }

    [Fact]
    public void Renders_installed_plugins_with_version_and_contribution()
    {
        var cut = Render<SbConsole.Web.Components.Pages.Plugins>();

        cut.Markup.Should().Contain("Azure Service Bus");
        cut.Markup.Should().Contain("0.4.1");
        cut.Markup.Should().Contain("3 page");
        cut.Markup.Should().Contain("8 action");
    }
}
