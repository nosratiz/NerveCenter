using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public interface IPluginStoreFactory
{
    IPluginStore For(string pluginId);
}
