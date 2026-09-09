using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public sealed class PluginRegistry(IEnumerable<IPlugin> plugins)
{
    public IReadOnlyList<IPlugin> Plugins { get; } = [.. plugins];

    public IPlugin? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}
