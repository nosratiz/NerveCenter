using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Config;

/// <summary>One workload (or bare pod) using a ConfigMap, and how: "env", "volume", "projected" (in that order).</summary>
public sealed record ConfigMapUser(string Name, IReadOnlyList<string> Via);

public sealed record ConfigMapRow(K8sConfigMap Map, IReadOnlyList<ConfigMapUser> UsedBy)
{
    public bool Unused => UsedBy.Count == 0;
}

/// <summary>Pure: joins ConfigMaps with the pod references to them (mockup 3f's "Used by" column).</summary>
public static class ConfigMapUsage
{
    /// <summary>Every namespace gets this map, and every pod's automatic token volume projects it.</summary>
    public const string RootCaConfigMap = "kube-root-ca.crt";

    public const string AllPods = "all pods";

    private static readonly string[] ViaOrder = ["env", "volume", "projected"];

    /// <summary>
    /// Rows by namespace then name; users by name. kube-root-ca.crt is "all pods · projected" when nothing references it
    /// explicitly (by env or a plain volume) — the projected references are the automatic service-account token volume.
    /// </summary>
    public static IReadOnlyList<ConfigMapRow> Resolve(K8sConfigData data)
    {
        var refs = data.Refs.ToLookup(r => (r.Namespace, r.ConfigMap));
        return data.ConfigMaps
            .OrderBy(m => m.Namespace, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new ConfigMapRow(m, Users(m, refs[(m.Namespace, m.Name)])))
            .ToList();
    }

    private static IReadOnlyList<ConfigMapUser> Users(K8sConfigMap map, IEnumerable<K8sConfigRef> refs)
    {
        var users = refs
            .GroupBy(r => r.User, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ConfigMapUser(g.Key, ViaOrder.Where(v => g.Any(r => r.Via == v))
                .Concat(g.Select(r => r.Via).Where(v => !ViaOrder.Contains(v)).Distinct(StringComparer.Ordinal))
                .ToList()))
            .ToList();

        if (map.Name != RootCaConfigMap)
        {
            return users;
        }

        var explicitUsers = users.Where(u => u.Via.Any(v => v != "projected")).ToList();
        return explicitUsers.Count > 0 ? explicitUsers : [new ConfigMapUser(AllPods, ["projected"])];
    }
}
