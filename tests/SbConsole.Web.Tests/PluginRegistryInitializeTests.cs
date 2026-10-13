using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class PluginRegistryInitializeTests
{
    private sealed class RecordingPlugin(string id) : IPlugin
    {
        public IServiceProvider? InitializedWith { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public string Version => "0.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => id;
        public string ConnectionKindDisplayName => id;
        public PluginContribution Contribution => new(PageCount: 0, ActionCount: 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
        public void Initialize(IServiceProvider services) => InitializedWith = services;
    }

    [Fact]
    public void InitializeAll_hands_every_plugin_the_service_provider()
    {
        var first = new RecordingPlugin("a");
        var second = new RecordingPlugin("b");
        using var services = new ServiceCollection().BuildServiceProvider();

        new PluginRegistry([first, second]).InitializeAll(services);

        first.InitializedWith.Should().BeSameAs(services);
        second.InitializedWith.Should().BeSameAs(services);
    }

    [Fact]
    public void A_plugin_that_does_not_override_Initialize_is_unaffected()
    {
        IPlugin plugin = new SbConsole.Plugins.Redis.RedisPlugin();
        using var services = new ServiceCollection().BuildServiceProvider();

        var act = () => new PluginRegistry([plugin]).InitializeAll(services);

        act.Should().NotThrow();
    }
}
