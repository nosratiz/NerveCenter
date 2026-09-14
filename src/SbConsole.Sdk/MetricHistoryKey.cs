namespace SbConsole.Sdk;

/// <summary>
/// The IPluginStore key format under which a resource's metric history (a JSON array of
/// MetricSnapshotPoint, oldest first) is stored. Shared so the host's background collector
/// (which writes) and a plugin's own UI (which reads) always agree on the key for the same
/// connection/resource pair.
/// </summary>
public static class MetricHistoryKey
{
    public static string For(Guid connectionId, string resourceName) => $"metrics:{connectionId}:{resourceName}";
}
