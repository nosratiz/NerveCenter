using Microsoft.Extensions.DependencyInjection;

namespace SbConsole.Sdk;

public interface IPlugin
{
    /// <summary>Stable, URL-safe identifier, e.g. "servicebus". Used in routes (/p/{Id}/...) and storage scoping.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    IReadOnlyList<PluginNavItem> NavItems { get; }

    /// <summary>Root Blazor component rendered at /p/{Id}. Must derive from ComponentBase.</summary>
    Type RootComponent { get; }

    /// <summary>Connection kind this plugin's connections use, e.g. "azure-servicebus". Matches Connection.Kind.</summary>
    string ConnectionKind { get; }

    /// <summary>Shown in the host's Add/Edit Connection "Kind" dropdown.</summary>
    string ConnectionKindDisplayName { get; }

    /// <summary>Static summary shown on the host's Plugins page.</summary>
    PluginContribution Contribution { get; }

    void ConfigureServices(IServiceCollection services);
}
