using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public sealed class PluginRegistry(IEnumerable<IPlugin> plugins)
{
    public IReadOnlyList<IPlugin> Plugins { get; } = [.. plugins];

    public IPlugin? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Calls IPlugin.Initialize on every registered plugin. Program.cs calls this once, right after Build().</summary>
    public void InitializeAll(IServiceProvider services)
    {
        foreach (var plugin in Plugins)
        {
            plugin.Initialize(services);
        }
    }
}
