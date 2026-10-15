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

    /// <summary>Σ a pod template's app-container requests (one pod's worth): CPU millicores, memory bytes.</summary>
    public static (long Cpu, long Mem) TemplateRequests(V1PodTemplateSpec? template) => Sum(template?.Spec?.Containers, r => r.Requests);

    /// <summary>One pod's requests and limits from a template; a value is null when no container sets it.</summary>
    public static K8sResources TemplateResources(V1PodTemplateSpec? template)
    {
        var containers = template?.Spec?.Containers ?? [];
        long? Total(Func<V1ResourceRequirements, IDictionary<string, ResourceQuantity>?> pick, string key, Func<string?, long> parse)
        {
            var values = containers.Select(c => c.Resources is null ? null : Quantity(pick(c.Resources), key)).Where(v => v is not null).ToList();
            return values.Count == 0 ? null : values.Sum(parse);
        }

        return new K8sResources(
            Total(r => r.Requests, "cpu", K8sFormat.ParseCpuMilli), Total(r => r.Requests, "memory", K8sFormat.ParseBytes),
            Total(r => r.Limits, "cpu", K8sFormat.ParseCpuMilli), Total(r => r.Limits, "memory", K8sFormat.ParseBytes));
    }

    public const string RevisionAnnotation = "deployment.kubernetes.io/revision";
    public const string ChangeCauseAnnotation = "kubernetes.io/change-cause";

    /// <summary>A Deployment-owned ReplicaSet; <paramref name="currentRevision"/> is the Deployment's own revision annotation.</summary>
    public static K8sReplicaSet ReplicaSet(V1ReplicaSet rs, long currentRevision)
    {
        var revision = Revision(rs.Metadata);
        return new K8sReplicaSet(
            rs.Metadata.Name, revision,
            (rs.Spec?.Template?.Spec?.Containers ?? []).Select(c => c.Image ?? string.Empty).ToList(),
            rs.Spec?.Replicas ?? 0, rs.Status?.ReadyReplicas ?? 0,
            Utc(rs.Metadata.CreationTimestamp) ?? DateTimeOffset.MinValue,
            rs.Metadata.Annotations is { } a && a.TryGetValue(ChangeCauseAnnotation, out var cause) && !string.IsNullOrEmpty(cause) ? cause : null,
            revision > 0 && revision == currentRevision,
            rs.Spec?.Template is not null);
    }

    /// <summary>The <c>deployment.kubernetes.io/revision</c> annotation, or 0 when absent or not a number.</summary>
    public static long Revision(V1ObjectMeta? meta) =>
        meta?.Annotations is { } a && a.TryGetValue(RevisionAnnotation, out var value)
            && long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : 0;

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

    // ---- Pod detail ----

    /// <summary>Init containers first (in spec order), then app containers; state from the status of the same name.</summary>
    public static IReadOnlyList<K8sContainer> Containers(V1Pod pod)
    {
        var init = (pod.Spec?.InitContainers ?? []).Select(c => Container(c, true, pod.Status?.InitContainerStatuses));
        var app = (pod.Spec?.Containers ?? []).Select(c => Container(c, false, pod.Status?.ContainerStatuses));
        return init.Concat(app).ToList();
    }

    private static K8sContainer Container(V1Container c, bool isInit, IList<V1ContainerStatus>? statuses)
    {
        var status = statuses?.FirstOrDefault(s => s.Name == c.Name);
        var state = status?.State;
        var (stateName, reason) = state switch
        {
            { Running: not null } => ("Running", (string?)null),
            { Terminated: { } t } => ("Terminated", t.Reason),
            { Waiting: { } w } => ("Waiting", w.Reason),
            _ => ("Waiting", null),
        };

        return new K8sContainer(
            c.Name, isInit, c.Image ?? status?.Image ?? string.Empty, stateName, reason,
            Termination(state?.Terminated), Termination(status?.LastState?.Terminated),
            status?.RestartCount ?? 0, status?.Ready ?? false,
            (c.Ports ?? []).Select(p => $"{p.ContainerPort}/{p.Name ?? (p.Protocol ?? "TCP").ToLowerInvariant()}").ToList(),
            ContainerResources(c));
    }

    private static K8sTermination? Termination(V1ContainerStateTerminated? t) =>
        t is null ? null : new K8sTermination(t.ExitCode, t.Reason, Utc(t.StartedAt), Utc(t.FinishedAt));

    private static K8sResources ContainerResources(V1Container c)
    {
        long? Value(IDictionary<string, ResourceQuantity>? values, string key, Func<string?, long> parse) =>
            Quantity(values, key) is { } q ? parse(q) : null;

        var r = c.Resources;
        return new K8sResources(
            Value(r?.Requests, "cpu", K8sFormat.ParseCpuMilli), Value(r?.Requests, "memory", K8sFormat.ParseBytes),
            Value(r?.Limits, "cpu", K8sFormat.ParseCpuMilli), Value(r?.Limits, "memory", K8sFormat.ParseBytes));
    }

    private static IEnumerable<V1Container> AllContainers(V1Pod pod) =>
        (pod.Spec?.InitContainers ?? []).Concat(pod.Spec?.Containers ?? []);

    /// <summary>The distinct ConfigMaps whose values the env shows (env <c>configMapKeyRef</c> and <c>envFrom configMapRef</c>), ordered by name.</summary>
    public static IReadOnlyList<string> ReferencedConfigMaps(V1Pod pod) =>
        AllContainers(pod)
            .SelectMany(c => (c.EnvFrom ?? []).Select(f => f.ConfigMapRef?.Name)
                .Concat((c.Env ?? []).Select(e => e.ValueFrom?.ConfigMapKeyRef?.Name)))
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    // ---- Config ----

    public static K8sConfigMap ConfigMap(V1ConfigMap map) => new(
        map.Metadata.NamespaceProperty ?? string.Empty,
        map.Metadata.Name,
        Dict(map.Data),
        (map.BinaryData ?? new Dictionary<string, byte[]>()).ToDictionary(kv => kv.Key, kv => (long)(kv.Value?.Length ?? 0), StringComparer.Ordinal),
        Utc(map.Metadata.CreationTimestamp) ?? DateTimeOffset.MinValue,
        map.Metadata.ResourceVersion ?? string.Empty);

    /// <summary>
    /// Every ConfigMap the pod references: env <c>configMapKeyRef</c> and <c>envFrom configMapRef</c> ("env"),
    /// <c>volumes[].configMap</c> ("volume"), <c>volumes[].projected.sources[].configMap</c> ("projected"). The user is the
    /// pod's top-level owner (a Deployment via its ReplicaSet), else the pod itself. Distinct.
    /// </summary>
    public static IReadOnlyList<K8sConfigRef> ConfigRefs(V1Pod pod)
    {
        var ns = pod.Metadata.NamespaceProperty ?? string.Empty;
        var owner = Pod(pod).Owner;
        var user = owner is { Kind: not "Node" } ? owner.Name : pod.Metadata.Name;
        var volumes = pod.Spec?.Volumes ?? [];
        var env = ReferencedConfigMaps(pod).Select(name => (name, Via: "env"));
        var mounted = volumes.Select(v => v.ConfigMap?.Name).OfType<string>().Select(name => (name, Via: "volume"));
        var projected = volumes
            .SelectMany(v => v.Projected?.Sources ?? [])
            .Select(s => s.ConfigMap?.Name).OfType<string>().Select(name => (name, Via: "projected"));
        return env.Concat(mounted).Concat(projected)
            .Where(r => r.name.Length > 0)
            .Distinct()
            .Select(r => new K8sConfigRef(ns, r.name, user, r.Via))
            .ToList();
    }

    /// <summary>
    /// The resolved env of every container (init first; per container <c>envFrom</c> rows, then <c>env</c> rows).
    /// <paramref name="configMaps"/> holds the maps that could be read; any other is Unresolved. Secret-sourced rows
    /// never carry a value: the Secret is not read.
    /// </summary>
    public static IReadOnlyList<K8sEnvVar> Env(V1Pod pod, IReadOnlyDictionary<string, V1ConfigMap> configMaps)
    {
        var rows = new List<K8sEnvVar>();
        foreach (var c in AllContainers(pod))
        {
            foreach (var from in c.EnvFrom ?? [])
            {
                var prefix = from.Prefix ?? string.Empty;
                if (from.ConfigMapRef is { } cmRef)
                {
                    if (configMaps.TryGetValue(cmRef.Name, out var map))
                    {
                        rows.AddRange((map.Data ?? new Dictionary<string, string>())
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => new K8sEnvVar(c.Name, prefix + kv.Key, kv.Value, K8sEnvSource.ConfigMap, cmRef.Name)));
                    }
                    else
                    {
                        rows.Add(new K8sEnvVar(c.Name, prefix + "*", null, K8sEnvSource.Unresolved, cmRef.Name));
                    }
                }
                else if (from.SecretRef is { } secretRef)
                {
                    rows.Add(new K8sEnvVar(c.Name, prefix + "*", null, K8sEnvSource.Secret, secretRef.Name));
                }
            }

            foreach (var e in c.Env ?? [])
            {
                rows.Add(EnvVar(pod, c, e, configMaps));
            }
        }

        return rows;
    }

    private static K8sEnvVar EnvVar(V1Pod pod, V1Container c, V1EnvVar e, IReadOnlyDictionary<string, V1ConfigMap> configMaps)
    {
        var from = e.ValueFrom;
        if (from?.SecretKeyRef is { } secretKey)
        {
            return new K8sEnvVar(c.Name, e.Name, null, K8sEnvSource.Secret, secretKey.Name);
        }

        if (from?.ConfigMapKeyRef is { } cmKey)
        {
            return configMaps.TryGetValue(cmKey.Name, out var map) && map.Data is { } data && data.TryGetValue(cmKey.Key, out var value)
                ? new K8sEnvVar(c.Name, e.Name, value, K8sEnvSource.ConfigMap, cmKey.Name)
                : new K8sEnvVar(c.Name, e.Name, null, K8sEnvSource.Unresolved, cmKey.Name);
        }

        if (from?.FieldRef is { } field)
        {
            var value = field.FieldPath switch
            {
                "metadata.name" => pod.Metadata?.Name,
                "metadata.namespace" => pod.Metadata?.NamespaceProperty,
                "spec.nodeName" => pod.Spec?.NodeName,
                "status.podIP" => pod.Status?.PodIP,
                "spec.serviceAccountName" => pod.Spec?.ServiceAccountName,
                _ => null,
            };
            return new K8sEnvVar(c.Name, e.Name, value, K8sEnvSource.FieldRef, field.FieldPath);
        }

        if (from?.ResourceFieldRef is { } resource)
        {
            var target = string.IsNullOrEmpty(resource.ContainerName)
                ? c
                : AllContainers(pod).FirstOrDefault(x => x.Name == resource.ContainerName);
            var parts = resource.Resource.Split('.', 2);
            var values = parts[0] switch
            {
                "limits" => target?.Resources?.Limits,
                "requests" => target?.Resources?.Requests,
                _ => null,
            };
            return new K8sEnvVar(c.Name, e.Name, parts.Length == 2 ? Quantity(values, parts[1]) : null,
                K8sEnvSource.ResourceFieldRef, resource.Resource);
        }

        return new K8sEnvVar(c.Name, e.Name, e.Value ?? string.Empty, K8sEnvSource.Literal, null);
    }

    /// <summary>The pod's controller (first owner when none is marked controller), or null for a bare pod.</summary>
    public static V1OwnerReference? ControllerOf(V1ObjectMeta? meta)
    {
        var refs = meta?.OwnerReferences;
        return refs?.FirstOrDefault(r => r.Controller == true) ?? refs?.FirstOrDefault();
    }

    /// <summary>
    /// Pod → its owner (ReplicaSet with revision / StatefulSet / DaemonSet / Job / …) → that owner's owner (Deployment /
    /// CronJob), at most two links. <paramref name="ownerMeta"/> is the first owner as read (null when it couldn't be:
    /// the chain stops there).
    /// </summary>
    public static IReadOnlyList<K8sOwnerLink> OwnerChain(V1Pod pod, V1ObjectMeta? ownerMeta)
    {
        if (ControllerOf(pod.Metadata) is not { } first)
        {
            return [];
        }

        long? revision = first.Kind == "ReplicaSet" && Revision(ownerMeta) is > 0 and var r ? r : null;
        var chain = new List<K8sOwnerLink> { new(first.Kind, first.Name, revision) };
        if (ControllerOf(ownerMeta) is { } second)
        {
            chain.Add(new K8sOwnerLink(second.Kind, second.Name, null));
        }

        return chain;
    }

    public static int VolumeCount(V1Pod pod) => pod.Spec?.Volumes?.Count ?? 0;

    /// <summary>Secret volumes plus the Secret sources of projected volumes.</summary>
    public static int SecretMountCount(V1Pod pod) =>
        (pod.Spec?.Volumes ?? []).Sum(v =>
            (v.Secret is not null ? 1 : 0) + (v.Projected?.Sources?.Count(s => s.Secret is not null) ?? 0));

    public static string QosClass(V1Pod pod) => pod.Status?.QosClass ?? "BestEffort";

    /// <summary>
    /// The node facts the pod detail shows. The memory request fraction is Σ requests of the node's active pods over its
    /// allocatable memory; null when the pods couldn't be listed (<paramref name="podsListed"/> false) or nothing is allocatable.
    /// </summary>
    public static K8sNodeHealth NodeHealth(K8sNodeInfo node, bool podsListed) =>
        new(node.Name, node.ReadyStatus, node.Pressures,
            podsListed && node.MemAllocatableBytes > 0 ? (double)node.MemRequestedBytes / node.MemAllocatableBytes : null);

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

    /// <summary>
    /// A policy/v1 PodDisruptionBudget. Only matchLabels are kept: a selector that also has matchExpressions is
    /// treated as its labels alone, which can only over-report pods as blocked (the eviction API stays authoritative).
    /// </summary>
    public static K8sPdb Pdb(V1PodDisruptionBudget pdb)
    {
        var spec = pdb.Spec;
        var rule = spec?.MinAvailable is { } min ? $"minAvailable {min.Value}"
            : spec?.MaxUnavailable is { } max ? $"maxUnavailable {max.Value}"
            : "no rule";
        return new K8sPdb(
            pdb.Metadata.NamespaceProperty ?? string.Empty, pdb.Metadata.Name, Dict(spec?.Selector?.MatchLabels),
            pdb.Status?.DisruptionsAllowed ?? 0, rule, pdb.Status?.CurrentHealthy ?? 0, pdb.Status?.DesiredHealthy ?? 0);
    }

    /// <summary>The Nodes page order: not-Ready nodes first (they need attention), then by name.</summary>
    public static IReadOnlyList<K8sNodeInfo> InNodeDisplayOrder(IEnumerable<K8sNodeInfo> nodes) =>
        nodes.OrderBy(n => n.ReadyStatus == "True" ? 1 : 0)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

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
