namespace SbConsole.Sdk;

/// <summary>
/// Hands a plugin its own IPluginStore, scoped by the plugin's Id. A plugin binds this in its own
/// ConfigureServices (e.g. services.AddScoped&lt;IPluginStore&gt;(sp =>
/// sp.GetRequiredService&lt;IPluginStoreFactory&gt;().For(Id))) so its own UI code can simply
/// @inject IPluginStore instead of naming the factory or its own Id at every call site.
/// </summary>
public interface IPluginStoreFactory
{
    IPluginStore For(string pluginId);
}
