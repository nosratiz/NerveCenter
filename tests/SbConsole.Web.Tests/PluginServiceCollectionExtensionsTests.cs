using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

/// <summary>
/// Regression guard: AddSbConsolePlugin&lt;TPlugin&gt;() used to register IPluginStore as a single
/// unkeyed scoped service (its own doc comment called this "a single-plugin simplification").
/// Registering a second plugin made every unkeyed IPluginStore resolution in the whole app resolve
/// to whichever plugin was registered LAST, silently corrupting the first plugin's per-connection
/// storage (e.g. Service Bus's metric-history sparklines) the moment a second plugin was added.
/// </summary>
public class PluginServiceCollectionExtensionsTests
{
    private sealed class FakeStore(string pluginId) : IPluginStore
    {
        public string PluginId { get; } = pluginId;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeStoreFactory : IPluginStoreFactory
    {
        public IPluginStore For(string pluginId) => new FakeStore(pluginId);
    }

    private sealed class FakePluginA : IPlugin
    {
        public string Id => "plugin-a";
        public string DisplayName => "Plugin A";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "plugin-a";
        public string ConnectionKindDisplayName => "Plugin A";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    private sealed class FakePluginB : IPlugin
    {
        public string Id => "plugin-b";
        public string DisplayName => "Plugin B";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "plugin-b";
        public string ConnectionKindDisplayName => "Plugin B";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    [Fact]
    public void Two_registered_plugins_each_resolve_their_own_keyed_IPluginStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginStoreFactory, FakeStoreFactory>();
        services.AddSbConsolePlugin<FakePluginA>();
        services.AddSbConsolePlugin<FakePluginB>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var storeA = (FakeStore)scope.ServiceProvider.GetRequiredKeyedService<IPluginStore>("plugin-a");
        var storeB = (FakeStore)scope.ServiceProvider.GetRequiredKeyedService<IPluginStore>("plugin-b");

        storeA.PluginId.Should().Be("plugin-a");
        storeB.PluginId.Should().Be("plugin-b");
    }
}
