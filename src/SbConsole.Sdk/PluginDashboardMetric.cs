namespace SbConsole.Sdk;

/// <summary>
/// One labeled count a plugin contributes to the host Dashboard, for one connection. The host
/// sums same-Label metrics across every connection of every plugin into one Dashboard tile per
/// label (see IPlugin.GetDashboardMetricsAsync).
/// </summary>
public sealed record PluginDashboardMetric(string Label, int Count);
