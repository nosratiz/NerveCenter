using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>Pure V1* → plugin record mapping. No API calls.</summary>
internal static class K8sMappers
{
    private static readonly HashSet<string> WaitingReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "CreateContainerConfigError", "ContainerCreating",
    };

    private static readonly string[] PoolLabels =
    [
        "agentpool", "kubernetes.azure.com/agentpool", "eks.amazonaws.com/nodegroup", "cloud.google.com/gke-nodepool",
    ];

    private static readonly (string Prefix, string Provider)[] ProviderPrefixes =
    [
        ("azure://", "AKS"), ("aws://", "EKS"), ("gce://", "GKE"), ("k3s://", "k3s"), ("kind://", "kind"),
    ];

    private const string InstanceTypeLabel = "node.kubernetes.io/instance-type";
    private const string RoleLabelPrefix = "node-role.kubernetes.io/";

    public static K8sRule ToRule(V1ResourceRule rule) => new(
        (IReadOnlyList<string>?)rule.ApiGroups?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.Resources?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.Verbs?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.ResourceNames?.ToList() ?? []);

    public static K8sRulesReview ToRulesReview(string ns, V1SubjectRulesReviewStatus? status) => new(
        ns,
        status?.Incomplete ?? true,
        (status?.ResourceRules ?? []).Select(ToRule).ToList());

    // ---- Pods ----

    public static K8sPodSummary Pod(V1Pod pod)
    {
        var meta = pod.Metadata;
        var ns = meta.NamespaceProperty ?? string.Empty;
        var phase = pod.Status?.Phase ?? "Unknown";
        var appStatuses = pod.Status?.ContainerStatuses ?? [];
        var initStatuses = pod.Status?.InitContainerStatuses ?? [];
        var labels = Dict(meta.Labels);
        var (owner, replicaSet) = Owner(meta, ns, labels);

        var lastTerminated = appStatuses
            .Where(c => c.LastState?.Terminated is not null)
            .OrderByDescending(c => c.RestartCount)
            .Select(c => c.LastState.Terminated)
            .FirstOrDefault();

        var unschedulable = pod.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "PodScheduled" && c.Reason == "Unschedulable")?.Message;

        var requests = Sum(pod.Spec?.Containers, r => r.Requests);

        return new K8sPodSummary(
            ns, meta.Name, phase, PodStatus(meta, phase, initStatuses, appStatuses),
            appStatuses.Sum(c => c.RestartCount),
            appStatuses.Count(c => c.Ready),
            pod.Spec?.Containers?.Count ?? appStatuses.Count,
            pod.Spec?.NodeName,
            owner, replicaSet,
            lastTerminated?.Reason, lastTerminated?.ExitCode, Utc(lastTerminated?.FinishedAt),
            unschedulable,
            Utc(meta.CreationTimestamp) ?? DateTimeOffset.MinValue,
            requests.Cpu, requests.Mem,
            pod.Spec?.Volumes?.Any(v => v.EmptyDir is not null) ?? false,
            labels,
            owner?.Kind == "DaemonSet",
            meta.Annotations?.ContainsKey("kubernetes.io/config.mirror") ?? false);
    }

    /// <summary>Σ app-container limits (init containers ignored): CPU millicores, memory bytes.</summary>
    public static (long Cpu, long Mem) PodLimits(V1Pod pod) => Sum(pod.Spec?.Containers, r => r.Limits);

    private static string PodStatus(V1ObjectMeta meta, string phase, IList<V1ContainerStatus> init, IList<V1ContainerStatus> app)
    {
        if (meta.DeletionTimestamp is not null)
        {
            return "Terminating";
        }

        var waiting = init.Concat(app)
            .Select(c => c.State?.Waiting?.Reason)
            .FirstOrDefault(r => r is not null && WaitingReasons.Contains(r));
        if (waiting is not null)
        {
            return waiting;
        }

        if (phase == "Running")
        {
            var terminated = app
                .Select(c => c.State?.Terminated?.Reason)
                .FirstOrDefault(r => r is "Error" or "OOMKilled");
            if (terminated is not null)
            {
                return terminated;
            }
        }

        return phase == "Succeeded" ? "Completed" : phase;
    }

    private static (K8sRef? Owner, string? ReplicaSet) Owner(V1ObjectMeta meta, string ns, IReadOnlyDictionary<string, string> labels)
    {
        var refs = meta.OwnerReferences;
        var owner = refs?.FirstOrDefault(r => r.Controller == true) ?? refs?.FirstOrDefault();
        if (owner is null)
        {
            return (null, null);
        }

        if (owner.Kind == "ReplicaSet")
        {
            if (labels.TryGetValue("pod-template-hash", out var hash) && !string.IsNullOrEmpty(hash)
                && owner.Name.EndsWith("-" + hash, StringComparison.Ordinal) && owner.Name.Length > hash.Length + 1)
            {
                return (new K8sRef("Deployment", ns, owner.Name[..^(hash.Length + 1)]), owner.Name);
            }

            return (new K8sRef("ReplicaSet", ns, owner.Name), owner.Name);
        }

        return (new K8sRef(owner.Kind, owner.Kind == "Node" ? null : ns, owner.Name), null);
    }

    // ---- Nodes ----

    public static K8sNodeInfo Node(V1Node node, IEnumerable<K8sPodSummary> podsOnNode)
    {
        var labels = Dict(node.Metadata.Labels);
        var conditions = node.Status?.Conditions ?? [];
        var ready = conditions.FirstOrDefault(c => c.Type == "Ready");
        var active = podsOnNode.Where(p => p.Phase is "Running" or "Pending").ToList();
        var allocatable = node.Status?.Allocatable;

        var roles = labels.Keys
            .Where(k => k.StartsWith(RoleLabelPrefix, StringComparison.Ordinal) && k.Length > RoleLabelPrefix.Length)
            .Select(k => k[RoleLabelPrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();

        var taints = (node.Spec?.Taints ?? [])
            .Where(t => !t.Key.StartsWith("node.kubernetes.io/", StringComparison.Ordinal))
            .Select(t => string.IsNullOrEmpty(t.Value) ? $"{t.Key}:{t.Effect}" : $"{t.Key}={t.Value}:{t.Effect}")
            .ToList();

        return new K8sNodeInfo(
            node.Metadata.Name,
            Pool(labels),
            labels.GetValueOrDefault(InstanceTypeLabel),
            node.Status?.Addresses?.FirstOrDefault(a => a.Type == "InternalIP")?.Address,
            ready?.Status ?? "Unknown",
            Utc(ready?.LastTransitionTime),
            conditions.Where(c => c.Type is "MemoryPressure" or "DiskPressure" or "PIDPressure" && c.Status == "True")
                .Select(c => c.Type).ToList(),
            roles.Count > 0 ? roles : ["agent"],
            taints,
            node.Status?.NodeInfo?.KubeletVersion ?? string.Empty,
            node.Spec?.Unschedulable ?? false,
            K8sFormat.ParseCpuMilli(Quantity(allocatable, "cpu")),
            K8sFormat.ParseBytes(Quantity(allocatable, "memory")),
            active.Sum(p => p.CpuRequestMilli),
            active.Sum(p => p.MemRequestBytes),
            active.Count,
            Utc(node.Metadata.CreationTimestamp) ?? DateTimeOffset.MinValue,
            node.Spec?.ProviderID,
            labels);
    }

    public static string Provider(IReadOnlyList<K8sNodeInfo> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ProviderId is { } id)
            {
                foreach (var (prefix, provider) in ProviderPrefixes)
                {
                    if (id.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return provider;
                    }
                }
            }

            if (node.Labels.GetValueOrDefault(InstanceTypeLabel) == "k3s")
            {
                return "k3s";
            }
        }

        return "Kubernetes";
    }

    public static string? Pool(IReadOnlyDictionary<string, string> labels)
    {
        foreach (var label in PoolLabels)
        {
            if (labels.TryGetValue(label, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    // ---- Events ----

    public static K8sEvent Event(Eventsv1Event e)
    {
        var regarding = e.Regarding;
        // Series.LastObservedTime is the latest occurrence for events.k8s.io-written events; core/v1-written
        // events only carry deprecatedLastTimestamp; eventTime is the first occurrence.
        var lastSeen = Utc(e.Series?.LastObservedTime) ?? Utc(e.DeprecatedLastTimestamp) ?? Utc(e.EventTime)
            ?? Utc(e.Metadata?.CreationTimestamp) ?? DateTimeOffset.MinValue;
        return new K8sEvent(
            e.Metadata?.NamespaceProperty,
            e.Type ?? "Normal",
            e.Reason ?? string.Empty,
            e.Note ?? string.Empty,
            new K8sRef(regarding?.Kind ?? string.Empty, regarding?.NamespaceProperty, regarding?.Name ?? string.Empty),
            e.Series?.Count ?? e.DeprecatedCount ?? 1,
            lastSeen,
            e.ReportingController ?? e.DeprecatedSource?.Component);
    }

    /// <summary>The same event read through the core/v1 API (the built-in "view" role can't list events.k8s.io).</summary>
    public static K8sEvent Event(Corev1Event e)
    {
        var involved = e.InvolvedObject;
        var lastSeen = Utc(e.Series?.LastObservedTime) ?? Utc(e.LastTimestamp) ?? Utc(e.EventTime)
            ?? Utc(e.Metadata?.CreationTimestamp) ?? DateTimeOffset.MinValue;
        return new K8sEvent(
            e.Metadata?.NamespaceProperty,
            e.Type ?? "Normal",
            e.Reason ?? string.Empty,
            e.Message ?? string.Empty,
            new K8sRef(involved?.Kind ?? string.Empty, involved?.NamespaceProperty, involved?.Name ?? string.Empty),
            e.Series?.Count ?? e.Count ?? 1,
            lastSeen,
            e.ReportingComponent is { Length: > 0 } rc ? rc : e.Source?.Component);
    }

    // ---- Workloads ----

    public static K8sWorkload Deployment(V1Deployment d) => Workload(
        K8sWorkloadKind.Deployment, d.Metadata, d.Spec?.Template,
        d.Spec?.Replicas ?? 1, d.Status?.ReadyReplicas ?? 0, d.Status?.UpdatedReplicas ?? 0, d.Status?.AvailableReplicas ?? 0,
        (d.Status?.Conditions ?? []).Select(c => new K8sCondition(c.Type, c.Status, c.Reason, c.Message, Utc(c.LastTransitionTime))).ToList());

    public static K8sWorkload StatefulSet(V1StatefulSet s) => Workload(
        K8sWorkloadKind.StatefulSet, s.Metadata, s.Spec?.Template,
        s.Spec?.Replicas ?? 1, s.Status?.ReadyReplicas ?? 0, s.Status?.UpdatedReplicas ?? 0, s.Status?.AvailableReplicas ?? 0,
        (s.Status?.Conditions ?? []).Select(c => new K8sCondition(c.Type, c.Status, c.Reason, c.Message, Utc(c.LastTransitionTime))).ToList());

    public static K8sWorkload DaemonSet(V1DaemonSet d) => Workload(
        K8sWorkloadKind.DaemonSet, d.Metadata, d.Spec?.Template,
        d.Status?.DesiredNumberScheduled ?? 0, d.Status?.NumberReady ?? 0, d.Status?.UpdatedNumberScheduled ?? 0, d.Status?.NumberAvailable ?? 0,
        (d.Status?.Conditions ?? []).Select(c => new K8sCondition(c.Type, c.Status, c.Reason, c.Message, Utc(c.LastTransitionTime))).ToList());

    public static K8sWorkload Job(V1Job j, DateTimeOffset now)
    {
        var conditions = (j.Status?.Conditions ?? [])
            .Select(c => new K8sCondition(c.Type, c.Status, c.Reason, c.Message, Utc(c.LastTransitionTime))).ToList();
        var failed = conditions.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        var complete = conditions.FirstOrDefault(c => c.Type == "Complete" && c.Status == "True");
        var suspended = j.Spec?.Suspend ?? false;
        var status = failed is not null ? "Failed"
            : complete is not null ? "Complete"
            : suspended ? "Suspended"
            : "Running";

        var start = Utc(j.Status?.StartTime);
        var end = Utc(j.Status?.CompletionTime) ?? failed?.LastTransition ?? (status == "Running" ? now : null);
        TimeSpan? duration = start is { } s && end is { } e && e >= s ? e - s : null;

        var completions = j.Spec?.Completions;
        return Workload(K8sWorkloadKind.Job, j.Metadata, j.Spec?.Template,
            completions ?? 1, j.Status?.Ready ?? 0, 0, 0, conditions) with
        {
            Completions = completions,
            Succeeded = j.Status?.Succeeded ?? 0,
            Failed = j.Status?.Failed ?? 0,
            JobStatus = status,
            FailureReason = failed?.Reason,
            Duration = duration,
            Suspended = suspended,
            Active = j.Status?.Active ?? 0,
        };
    }

    public static K8sWorkload CronJob(V1CronJob c) =>
        Workload(K8sWorkloadKind.CronJob, c.Metadata, c.Spec?.JobTemplate?.Spec?.Template, 0, 0, 0, 0, []) with
        {
            Schedule = c.Spec?.Schedule,
            Suspended = c.Spec?.Suspend ?? false,
            LastSchedule = Utc(c.Status?.LastScheduleTime),
            Active = c.Status?.Active?.Count ?? 0,
        };

    private static K8sWorkload Workload(
        K8sWorkloadKind kind, V1ObjectMeta meta, V1PodTemplateSpec? template,
        int desired, int ready, int upToDate, int available, IReadOnlyList<K8sCondition> conditions) =>
        new(kind, meta.NamespaceProperty ?? string.Empty, meta.Name, desired, ready, upToDate, available,
            (template?.Spec?.Containers ?? []).Select(c => c.Image ?? string.Empty).ToList(),
            Utc(meta.CreationTimestamp) ?? DateTimeOffset.MinValue,
            Dict(meta.Labels), conditions,
            null, 0, 0, null, null, null, null, false, null, 0);

    // ---- Helpers ----

    private static (long Cpu, long Mem) Sum(IList<V1Container>? containers, Func<V1ResourceRequirements, IDictionary<string, ResourceQuantity>?> pick)
    {
        long cpu = 0, mem = 0;
        foreach (var c in containers ?? [])
        {
            var values = c.Resources is null ? null : pick(c.Resources);
            cpu += K8sFormat.ParseCpuMilli(Quantity(values, "cpu"));
            mem += K8sFormat.ParseBytes(Quantity(values, "memory"));
        }

        return (cpu, mem);
    }

    private static string? Quantity(IDictionary<string, ResourceQuantity>? values, string key) =>
        values is not null && values.TryGetValue(key, out var q) ? q?.ToString() : null;

    private static IReadOnlyDictionary<string, string> Dict(IDictionary<string, string>? source) =>
        source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } v
            ? new DateTimeOffset(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc))
            : null;
}
