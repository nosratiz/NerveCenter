namespace SbConsole.Sdk;

/// <summary>
/// One labeled count a plugin contributes to the host Dashboard, for one connection. The host
/// sums same-Label metrics across that plugin's own connections and shows them, Label verbatim,
/// on the plugin's Dashboard fleet card -- labels are never merged across plugins (see
/// IPlugin.GetDashboardMetricsAsync).
/// </summary>
public sealed record PluginDashboardMetric(string Label, int Count);
