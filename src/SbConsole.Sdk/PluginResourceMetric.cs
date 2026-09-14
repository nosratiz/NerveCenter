namespace SbConsole.Sdk;

/// <summary>
/// A point-in-time reading for one of a plugin's named resources (e.g. a queue), for one
/// connection. Used by the host's metrics collector to build trend sparklines -- see
/// IPlugin.GetResourceMetricsAsync.
/// </summary>
public sealed record PluginResourceMetric(string ResourceName, long ActiveCount, long DeadLetterCount);
