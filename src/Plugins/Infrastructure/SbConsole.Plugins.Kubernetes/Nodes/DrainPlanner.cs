using System.Globalization;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Nodes;

/// <summary>What a drain will do to one pod, in the order the preview lists them.</summary>
public enum DrainGroup { BlockedByPdb, Unmanaged, WillEvict, DaemonSet }

/// <summary>
/// One pod's fate: the group plus a short note ("PDB ledger minAvailable 3 · now 3" | "1 of 6 reschedules" |
/// "brief outage" | "ignored" | "no controller").
/// </summary>
public sealed record DrainPodPlan(K8sPodSummary Pod, DrainGroup Group, string Note, bool HasEmptyDir);

public sealed record DrainPlan(string Node, IReadOnlyList<DrainPodPlan> Pods, int WillEvict, int DaemonSets, int Blocked, TimeSpan Estimate);

/// <summary>
/// The eviction preview (spec §10.1, mockup 3h), pure: groups the node's pods by what a drain will do, PDB-blocked pods
/// first because they decide whether the drain finishes. Mirror (static) pods are left out entirely: the API server
/// can't evict them.
/// </summary>
public static class DrainPlanner
{
    /// <summary>Evictions run this many at a time (spec §10.1); the estimate assumes each batch takes one grace period.</summary>
    public const int Parallelism = 5;

    public static DrainPlan Plan(K8sDrainInputs inputs, int gracePeriodSeconds)
    {
        var pods = inputs.Pods.Where(p => !p.IsMirrorPod).ToList();
        var perOwner = pods.Where(p => p.Owner is not null)
            .GroupBy(p => K8sDrainInputs.OwnerKey(p.Owner!, p.Namespace), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var planned = pods.Select(pod => Classify(pod, inputs, perOwner))
            .OrderBy(p => p.Group)
            .ThenBy(p => p.Pod.Namespace, StringComparer.Ordinal)
            .ThenBy(p => p.Pod.Name, StringComparer.Ordinal)
            .ToList();

        var willEvict = planned.Count(p => p.Group == DrainGroup.WillEvict);
        return new DrainPlan(
            inputs.Node.Name, planned, willEvict,
            planned.Count(p => p.Group == DrainGroup.DaemonSet),
            planned.Count(p => p.Group == DrainGroup.BlockedByPdb),
            Estimate(willEvict, gracePeriodSeconds));
    }

    /// <summary>ceil(pods / 5) × grace: zero when nothing is evicted.</summary>
    public static TimeSpan Estimate(int willEvict, int gracePeriodSeconds) =>
        willEvict <= 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds((long)Math.Ceiling(willEvict / (double)Parallelism) * Math.Max(0, gracePeriodSeconds));

    private static DrainPodPlan Classify(K8sPodSummary pod, K8sDrainInputs inputs, IReadOnlyDictionary<string, int> perOwner)
    {
        if (pod.IsDaemonSetPod)
        {
            return new DrainPodPlan(pod, DrainGroup.DaemonSet, "ignored", pod.HasEmptyDir);
        }

        if (BlockingPdb(pod, inputs.Pdbs) is { } pdb)
        {
            var note = pdb.Rule.StartsWith("minAvailable", StringComparison.Ordinal)
                ? $"PDB {pdb.Name} {pdb.Rule} · now {pdb.CurrentHealthy.ToString(CultureInfo.InvariantCulture)}"
                : $"PDB {pdb.Name} {pdb.Rule}";
            if (pod.Owner is null)
            {
                note += " · no controller"; // the drain skips it without Force whatever the PDB says
            }

            return new DrainPodPlan(pod, DrainGroup.BlockedByPdb, note, pod.HasEmptyDir);
        }

        if (pod.Owner is not { } owner)
        {
            return new DrainPodPlan(pod, DrainGroup.Unmanaged, "no controller", pod.HasEmptyDir);
        }

        var key = K8sDrainInputs.OwnerKey(owner, pod.Namespace);
        string replicaNote;
        if (inputs.OwnerReplicas.TryGetValue(key, out var replicas))
        {
            var here = perOwner.GetValueOrDefault(key, 1);
            replicaNote = replicas <= 1 || here >= replicas
                ? "brief outage"
                : $"{here.ToString(CultureInfo.InvariantCulture)} of {replicas.ToString(CultureInfo.InvariantCulture)} reschedules";
        }
        else
        {
            replicaNote = "reschedules";
        }

        return new DrainPodPlan(pod, DrainGroup.WillEvict, replicaNote, pod.HasEmptyDir);
    }

    /// <summary>A PDB in the pod's namespace whose selector is a subset of the pod's labels and that allows no disruption now.</summary>
    private static K8sPdb? BlockingPdb(K8sPodSummary pod, IReadOnlyList<K8sPdb> pdbs) =>
        pdbs.Where(p => p.Namespace == pod.Namespace && p.DisruptionsAllowed <= 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault(p => p.Selector.All(kv => pod.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value));
}
