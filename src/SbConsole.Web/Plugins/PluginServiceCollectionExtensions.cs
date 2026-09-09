using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers a plugin at compile time. The scoped IPluginStore registration is a
    /// single-plugin simplification; move to keyed services when a second plugin arrives.
    /// </summary>
    public static IServiceCollection AddSbConsolePlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IPlugin, new()
    {
        var plugin = new TPlugin();
        services.AddSingleton<IPlugin>(plugin);
        services.AddScoped<IPluginStore>(sp => sp.GetRequiredService<IPluginStoreFactory>().For(plugin.Id));
        plugin.ConfigureServices(services);
        return services;
    }
}
