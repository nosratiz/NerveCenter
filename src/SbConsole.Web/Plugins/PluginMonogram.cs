using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

/// <summary>
/// The two-letter mark a plugin is shown by across the host (icon rail, Dashboard fleet cards and
/// triage rows), so every surface agrees on it: IPlugin.Monogram when the plugin supplies one, else
/// the first two letters/digits of its DisplayName.
/// </summary>
public static class PluginMonogram
{
    public static string Of(IPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Monogram))
        {
            return plugin.Monogram;
        }

        var letters = new string(plugin.DisplayName.Where(char.IsLetterOrDigit).Take(2).ToArray());
        return letters.ToUpperInvariant();
    }
}
