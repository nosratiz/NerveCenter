using Microsoft.Extensions.DependencyInjection;

namespace SbConsole.Sdk;

public interface IPlugin
{
    /// <summary>Stable, URL-safe identifier, e.g. "servicebus". Used in routes (/p/{Id}/...) and storage scoping.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    IReadOnlyList<PluginNavItem> NavItems { get; }

    /// <summary>Connection kind this plugin's connections use, e.g. "azure-servicebus". Matches Connection.Kind.</summary>
    string ConnectionKind { get; }

    /// <summary>Shown in the host's Add/Edit Connection "Kind" dropdown.</summary>
    string ConnectionKindDisplayName { get; }

    /// <summary>Static summary shown on the host's Plugins page.</summary>
    PluginContribution Contribution { get; }

    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Verifies a saved connection of this plugin's ConnectionKind actually works, using
    /// whatever protocol that connection kind speaks. Called with the connection's decrypted
    /// secret — never the connection ID, so this has no dependency on the host's DbContext.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    /// <summary>
    /// Optional live badge for a specific nav item (matched by exact Href), for one connection.
    /// The host calls this once per connection of the plugin's ConnectionKind and sums the non-null
    /// results into one badge per nav item (see NavMenu.razor). Returning null means "nothing to
    /// report for this href" -- the default implementation does exactly that, so a plugin written
    /// before this method existed, or one with nothing to badge, needs no change at all.
    /// </summary>
    Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    /// <summary>
    /// Optional Dashboard KPI metrics for one connection of this plugin's ConnectionKind. The host
    /// calls this once per connection and sums same-Label metrics into one Dashboard tile per
    /// label. Returning an empty list means "nothing to report" -- the default implementation does
    /// exactly that, so a plugin written before this method existed needs no change at all.
    /// </summary>
    Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([]);

    /// <summary>
    /// Optional point-in-time Active/DeadLetter reading for every one of this plugin's named
    /// resources (e.g. queues) on one connection. The host's background collector calls this
    /// periodically and appends each reading to that resource's metric history, which a plugin's
    /// own UI can then read back (via the same IPluginStore) to draw trend sparklines. Returning
    /// an empty list means "nothing to report" -- the default implementation does exactly that, so
    /// a plugin written before this method existed needs no change at all.
    /// </summary>
    Task<IReadOnlyList<PluginResourceMetric>> GetResourceMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginResourceMetric>>([]);
}
