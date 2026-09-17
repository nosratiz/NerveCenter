using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers a plugin at compile time. IPluginStore is registered keyed by the plugin's own Id
    /// (not as a single unkeyed scoped service) so more than one plugin can be registered without
    /// each plugin's per-connection storage colliding -- see docs/superpowers/plans/
    /// 2026-09-17-kafka-topics-plugin.md Task 3 for the bug this replaced.
    /// </summary>
    public static IServiceCollection AddSbConsolePlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IPlugin, new()
    {
        var plugin = new TPlugin();
        services.AddSingleton<IPlugin>(plugin);
        services.AddKeyedScoped<IPluginStore>(plugin.Id, (sp, _) => sp.GetRequiredService<IPluginStoreFactory>().For(plugin.Id));
        plugin.ConfigureServices(services);
        return services;
    }
}
