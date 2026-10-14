using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests;

internal static class TestData
{
    /// <summary>
    /// A one-cluster kubeconfig. <paramref name="user"/> is the YAML fragment under <c>user:</c>
    /// (indented by the caller's own newlines; default is a static token).
    /// </summary>
    public static string Kubeconfig(string? user = null, string server = "https://k.example:6443", bool skipTls = false, string context = "dev")
    {
        var userBlock = string.Join("\n", (user ?? "token: abc").Split('\n').Select(l => "      " + l));
        return $"""
            apiVersion: v1
            kind: Config
            current-context: {context}
            clusters:
            - name: c1
              cluster:
                server: {server}
                insecure-skip-tls-verify: {(skipTls ? "true" : "false")}
            contexts:
            - name: {context}
              context:
                cluster: c1
                user: u1
            users:
            - name: u1
              user:
            {userBlock}
            """;
    }

    private static readonly string[] AllClusterChecks = ["nodes/patch", "pods/eviction/create", "nodes/list", "pods/list-all"];

    /// <summary>cluster-admin-like facts: one "default" review granting every verb on everything, every cluster check allowed.</summary>
    public static K8sAccessFacts AdminAccess(string identity = "admin") =>
        new(identity,
            [new K8sRulesReview("default", false, [new K8sRule(["*"], ["*"], ["*"], [])])],
            AllClusterChecks.ToDictionary(c => c, _ => true));

    /// <summary>The built-in "view" role: get/list/watch on the common resources, no writes; may list pods cluster-wide.</summary>
    public static K8sAccessFacts ViewerAccess(string identity = "viewer") =>
        new(identity,
            [new K8sRulesReview("default", false,
            [
                new K8sRule(["apps"], ["deployments", "statefulsets", "daemonsets", "replicasets"], ["get", "list", "watch"], []),
                new K8sRule(["batch"], ["jobs", "cronjobs"], ["get", "list", "watch"], []),
                new K8sRule([""], ["pods", "pods/log", "services", "endpoints", "configmaps", "events", "namespaces", "nodes", "persistentvolumeclaims"], ["get", "list", "watch"], []),
                new K8sRule(["policy"], ["poddisruptionbudgets"], ["get", "list", "watch"], []),
                // system:basic-user, granted to every authenticated account.
                new K8sRule(["authorization.k8s.io"], ["selfsubjectaccessreviews", "selfsubjectrulesreviews"], ["create"], []),
                new K8sRule(["authentication.k8s.io"], ["selfsubjectreviews"], ["create"], []),
            ])],
            new Dictionary<string, bool>
            {
                ["nodes/patch"] = false, ["pods/eviction/create"] = false, ["nodes/list"] = false, ["pods/list-all"] = true,
            });

    public static IReadOnlyList<K8sNamespace> Namespaces() =>
        [new("checkout", 18), new("payments", 11), new("payments-canary", 2)];

    /// <summary>A minimal healthy overview: enough for snapshot-store round trips.</summary>
    public static K8sOverview MinimalOverview(DateTimeOffset? at = null) =>
        new(at ?? new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
            new K8sClusterInfo("https://aks-prod-eu.example:443", "aks-prod-eu-admin", "v1.30.4", "AKS", "admin", 3),
            [], new K8sPodCounts(31, 0, 0, 0), [], [], [], 0, 0, 0, null, null);

    public static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static K8sPodSummary Pod(
        string ns = "checkout", string name = "api-1", string phase = "Running", string? status = null, int restarts = 0,
        string? unschedulable = null, string? lastReason = null, int? lastExit = null, string? node = "node-1") =>
        new(ns, name, phase, status ?? phase, restarts, 1, 1, node, new K8sRef("Deployment", ns, "api"), "api-7d9f",
            lastReason, lastExit, null, unschedulable, T0.AddHours(-1), 100, 128L * 1024 * 1024, false,
            new Dictionary<string, string> { ["app"] = "api" }, false, false);

    public static K8sNodeInfo Node(string name = "node-1", string ready = "True", DateTimeOffset? readySince = null, int pods = 10) =>
        new(name, "system", "Standard_D4s_v5", "10.0.0.4", ready, readySince ?? T0.AddDays(-3), [], ["agent"], [], "v1.30.4", false,
            3860, 16L * 1024 * 1024 * 1024, 1000, 4L * 1024 * 1024 * 1024, pods, T0.AddDays(-30), "azure:///vm-0",
            new Dictionary<string, string>());

    public static K8sWorkload Deployment(string ns = "checkout", string name = "api", int desired = 3, int available = 3,
        IReadOnlyList<K8sCondition>? conditions = null) =>
        new(K8sWorkloadKind.Deployment, ns, name, desired, available, desired, available, ["api:1"], T0.AddDays(-1),
            new Dictionary<string, string>(), conditions ?? [], null, 0, 0, null, null, null, null, false, null, 0);

    public static K8sWorkload Job(string ns = "checkout", string name = "migrate", string status = "Failed", string? reason = "BackoffLimitExceeded") =>
        new(K8sWorkloadKind.Job, ns, name, 1, 0, 0, 0, ["migrate:1"], T0.AddHours(-2), new Dictionary<string, string>(), [],
            1, 0, status == "Failed" ? 2 : 0, status, reason, TimeSpan.FromMinutes(2), null, false, null, 0);

    public static K8sEvent Warning(string ns = "checkout", string reason = "BackOff", DateTimeOffset? at = null) =>
        new(ns, "Warning", reason, "Back-off restarting failed container", new K8sRef("Pod", ns, "api-1"), 3, at ?? T0.AddMinutes(-5), "kubelet");

    /// <summary>An overview with the given problem sets; healthy defaults otherwise.</summary>
    public static K8sOverview OverviewWith(
        IReadOnlyList<K8sNodeInfo>? nodes = null, IReadOnlyList<K8sPodSummary>? problemPods = null,
        IReadOnlyList<K8sWorkload>? problemWorkloads = null, IReadOnlyList<K8sEvent>? warnings = null) =>
        MinimalOverview(T0) with
        {
            Nodes = nodes ?? [Node()],
            ProblemPods = problemPods ?? [],
            ProblemWorkloads = problemWorkloads ?? [],
            RecentWarnings = warnings ?? [],
            WarningCount = warnings?.Count ?? 0,
        };

    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// The cluster from mockup 3a, captured at <see cref="T0"/>: 12 nodes in 2 pools (one NotReady for 6 minutes),
    /// 412/6/3/41 pods, two crash loops, an unavailable Deployment, a failed Job, 6 unschedulable pods,
    /// CPU requests 58.6 / 96 cores (limits 142, usage 38%), memory 299 / 384 Gi with exactly one node over 8 Gi free,
    /// and 7 recent warnings (38 in the last hour).
    /// </summary>
    public static K8sOverview Overview()
    {
        var nodes = new List<K8sNodeInfo>();
        for (var i = 0; i < 12; i++)
        {
            var pool = i < 3 ? "system" : "pool1";
            var name = i == 3 ? "aks-pool1-38211-vmss00000a" : $"aks-{pool}-38211-vmss{i:x6}";
            // Memory: one node with 13 Gi free, one with 2 Gi, the rest 7 Gi → 299 Gi requested of 384 Gi.
            var memRequested = i switch { 0 => 19 * GiB, 1 => 30 * GiB, _ => 25 * GiB };
            var cpuRequested = i == 0 ? 3600 : 5000;
            nodes.Add(new K8sNodeInfo(name, pool, "Standard_D8s_v5", $"10.0.0.{10 + i}", i == 3 ? "False" : "True",
                i == 3 ? T0.AddMinutes(-6) : T0.AddDays(-3), [], ["agent"], [], "v1.30.4", false,
                8000, 32 * GiB, cpuRequested, memRequested, i == 3 ? 34 : 30, T0.AddDays(-40), $"azure:///vm-{i}",
                new Dictionary<string, string>()));
        }

        K8sPodSummary Problem(string ns, string name, string status, int restarts, K8sRef owner, string? reason = null, int? exit = null,
            string? unschedulable = null) =>
            new(ns, name, status == "CrashLoopBackOff" ? "Running" : "Pending", status, restarts, 0, 1,
                unschedulable is null ? "aks-pool1-38211-vmss000004" : null, owner, null, reason, exit,
                reason is null ? null : T0.AddMinutes(-2), unschedulable, T0.AddHours(-3), 250, 512L * 1024 * 1024, false,
                new Dictionary<string, string>(), owner.Kind == "DaemonSet", false);

        const string insufficient = "0/12 nodes are available: 1 node(s) had untolerated taint, 11 Insufficient memory.";
        var problemPods = new List<K8sPodSummary>
        {
            Problem("platform", "log-shipper-mq8zl", "CrashLoopBackOff", 6, new K8sRef("DaemonSet", "platform", "log-shipper"), "Error", 1),
            Problem("checkout", "payment-gateway-7d9f8-x2k4q", "CrashLoopBackOff", 14,
                new K8sRef("Deployment", "checkout", "payment-gateway"), "OOMKilled", 137),
        };
        for (var i = 0; i < 6; i++)
        {
            problemPods.Add(Problem("monitoring", $"prometheus-{i}", "Pending", 0, new K8sRef("StatefulSet", "monitoring", "prometheus"),
                unschedulable: insufficient));
        }

        K8sEvent Event(int minutesAgo, string reason, string kind, string ns, string name, string message, int count) =>
            new(kind == "Node" ? null : ns, "Warning", reason, message, new K8sRef(kind, kind == "Node" ? null : ns, name), count,
                T0.AddMinutes(-minutesAgo), "kubelet");

        return new K8sOverview(
            T0,
            new K8sClusterInfo("https://aks-prod-eu-dns-8f2a1c.hcp.westeurope.azmk8s.io:443", "aks-prod-eu-admin", "v1.30.4", "AKS",
                "sbconsole-admin", 14),
            nodes,
            new K8sPodCounts(412, 6, 3, 41),
            problemPods,
            [
                Deployment("checkout", "payment-gateway", 4, 2,
                    [new K8sCondition("Progressing", "False", "ProgressDeadlineExceeded", null, T0.AddMinutes(-30))]),
                Job("payments", "settlement-20261014-0200", "Failed", "BackoffLimitExceeded"),
            ],
            [
                Event(2, "BackOff", "Pod", "checkout", "payment-gateway-7d9f8-x2k4q",
                    "Back-off restarting failed container gateway in pod payment-gateway-7d9f8-x2k4q_checkout", 14),
                Event(4, "FailedScheduling", "Pod", "monitoring", "prometheus-1", insufficient, 22),
                Event(6, "NodeNotReady", "Node", "", "aks-pool1-38211-vmss00000a",
                    "Node aks-pool1-38211-vmss00000a status is now: NodeNotReady", 1),
                Event(9, "Unhealthy", "Pod", "checkout", "checkout-api-6c8d7-r9p2m",
                    "Readiness probe failed: HTTP probe failed with statuscode: 503", 3),
                Event(12, "OOMKilling", "Node", "", "aks-pool1-38211-vmss000004",
                    "Memory cgroup out of memory: Killed process 48213 (gateway)", 2),
                Event(41, "FailedMount", "Pod", "reports", "report-builder-2x8ql",
                    "MountVolume.SetUp failed for volume \"reports-cfg\": configmap \"reports-config\" not found", 6),
                Event(55, "BackOff", "Pod", "platform", "log-shipper-mq8zl", "Back-off restarting failed container shipper", 9),
            ],
            38,
            142_000, 512 * GiB, 0.38, 0.52);
    }

    private const long MiB = 1024L * 1024;

    public static K8sReplicaSet ReplicaSet(string name, long revision, string tag, int desired, int ready, DateTimeOffset created,
        bool current = false, string? changeCause = null, bool hasTemplate = true) =>
        new(name, revision, [$"ghcr.io/nervecenter/payment-gateway:{tag}"], desired, ready, created, changeCause, current, hasTemplate);

    /// <summary>
    /// Mockup 3c, captured at <see cref="T0"/>: checkout/payment-gateway, desired 4 ready 2, RollingUpdate 25% / 0. Revision 23
    /// (2.14.1, 38 min old) has two OOM-crash-looping pods; revision 22 (2.13.4) still serves two; revisions 19–21 are kept
    /// at desired 0. Progressing=False ProgressDeadlineExceeded since 28 min (the 600 s deadline after the rollout began), one Service with 2 endpoints, memory limit 512 Mi.
    /// </summary>
    public static K8sDeploymentDetail PaymentGatewayDetail()
    {
        var labels = new Dictionary<string, string> { ["app"] = "payment-gateway" };
        var deployment = Deployment("checkout", "payment-gateway", 4, 2,
        [
            new K8sCondition("Available", "True", "MinimumReplicasAvailable", "Deployment has minimum availability.", T0.AddDays(-9)),
            new K8sCondition("Progressing", "False", "ProgressDeadlineExceeded",
                "ReplicaSet \"payment-gateway-7d9f8\" has timed out progressing.", T0.AddMinutes(-28)),
            new K8sCondition("ReplicaFailure", "False", null, null, T0.AddDays(-9)),
        ]) with
        {
            Ready = 2, UpToDate = 2, Images = ["ghcr.io/nervecenter/payment-gateway:2.14.1"], CreatedAt = T0.AddDays(-41), Labels = labels,
        };

        K8sPodSummary PgPod(string rs, string suffix, string status, int restarts, string? lastReason, int ready) =>
            new("checkout", $"{rs}-{suffix}", "Running", status, restarts, ready, 1, "aks-pool1-38211-vmss000004",
                new K8sRef("Deployment", "checkout", "payment-gateway"), rs, lastReason, lastReason is null ? null : 137,
                lastReason is null ? null : T0.AddMinutes(-2), null, T0.AddMinutes(-38), 500, 512 * MiB, false,
                new Dictionary<string, string>(labels) { ["pod-template-hash"] = rs[(rs.LastIndexOf('-') + 1)..] }, false, false);

        return new K8sDeploymentDetail(
            deployment, "RollingUpdate", "25%", "0", "app=payment-gateway", 600, 10,
            new K8sResources(500, 512 * MiB, 1000, 512 * MiB), 23,
            [
                ReplicaSet("payment-gateway-7d9f8", 23, "2.14.1", 4, 0, T0.AddMinutes(-38), current: true, changeCause: "bump sdk"),
                ReplicaSet("payment-gateway-5c6b4", 22, "2.13.4", 2, 2, T0.AddDays(-9)),
                ReplicaSet("payment-gateway-8a1e0", 21, "2.13.3", 0, 0, T0.AddDays(-16)),
                ReplicaSet("payment-gateway-6f2c1", 20, "2.13.1", 0, 0, T0.AddDays(-24), changeCause: "manual"),
                ReplicaSet("payment-gateway-49b7d", 19, "2.13.0", 0, 0, T0.AddDays(-30)),
            ],
            [
                PgPod("payment-gateway-7d9f8", "x2k4q", "CrashLoopBackOff", 14, "OOMKilled", 0),
                PgPod("payment-gateway-7d9f8", "p7m2w", "CrashLoopBackOff", 11, "OOMKilled", 0),
                PgPod("payment-gateway-5c6b4", "k8s2d", "Running", 0, null, 1),
                PgPod("payment-gateway-5c6b4", "nq41v", "Running", 0, null, 1),
            ],
            [new K8sServiceRef("payment-gateway", 2)],
            [
                new K8sEvent("checkout", "Warning", "BackOff", "Back-off restarting failed container gateway",
                    new K8sRef("Pod", "checkout", "payment-gateway-7d9f8-x2k4q"), 14, T0.AddMinutes(-2), "kubelet"),
                new K8sEvent("checkout", "Normal", "ScalingReplicaSet", "Scaled up replica set payment-gateway-7d9f8 to 2",
                    new K8sRef("Deployment", "checkout", "payment-gateway"), 1, T0.AddMinutes(-38), "deployment-controller"),
            ],
            false);
    }

    /// <summary>
    /// Mockup 3d, captured at <see cref="T0"/>: checkout/payment-gateway-7d9f8-x2k4q, revision 23's pod, crash-looping on an
    /// OOM kill (137) with 14 restarts; an init container that completed; env from a literal, a ConfigMap, a Secret and fields.
    /// </summary>
    public static K8sPodDetail PaymentGatewayPodDetail()
    {
        const string ns = "checkout";
        const string name = "payment-gateway-7d9f8-x2k4q";
        var pod = PaymentGatewayDetail().Pods.Single(p => p.Name == name);
        var oomAt = T0.AddMinutes(-2);
        return new K8sPodDetail(
            pod, "10.244.3.17", "10.0.0.14", "Burstable", "payment-gateway", 4, 1,
            [new K8sOwnerLink("ReplicaSet", "payment-gateway-7d9f8", 23), new K8sOwnerLink("Deployment", "payment-gateway", null)],
            new K8sNodeHealth("aks-pool1-38211-vmss000004", "True", [], 0.62),
            [
                new K8sContainer("migrate", true, "ghcr.io/nervecenter/payment-gateway-migrate:2.14.1", "Terminated", "Completed",
                    new K8sTermination(0, "Completed", T0.AddMinutes(-38), T0.AddMinutes(-37)), null, 0, false, [],
                    new K8sResources(100, 64 * MiB, null, null)),
                new K8sContainer("gateway", false, "ghcr.io/nervecenter/payment-gateway:2.14.1", "Waiting", "CrashLoopBackOff",
                    null, new K8sTermination(137, "OOMKilled", oomAt.AddSeconds(-41), oomAt), 14, false, ["8080/http", "9090/metrics"],
                    new K8sResources(500, 512 * MiB, 1000, 512 * MiB)),
            ],
            [
                new K8sEnvVar("gateway", "ASPNETCORE_ENVIRONMENT", "Production", K8sEnvSource.Literal, null),
                new K8sEnvVar("gateway", "PG_HOST", "pg-checkout.internal", K8sEnvSource.ConfigMap, "payment-gateway-config"),
                new K8sEnvVar("gateway", "PG_PASSWORD", null, K8sEnvSource.Secret, "payment-gateway-db"),
                new K8sEnvVar("gateway", "POD_NAME", name, K8sEnvSource.FieldRef, "metadata.name"),
                new K8sEnvVar("gateway", "MEM_LIMIT", "512Mi", K8sEnvSource.ResourceFieldRef, "limits.memory"),
            ],
            [
                new K8sEvent(ns, "Warning", "BackOff", "Back-off restarting failed container gateway",
                    new K8sRef("Pod", ns, name), 14, oomAt, "kubelet"),
            ],
            $"apiVersion: v1\nkind: Pod\nmetadata:\n  name: {name}\n  namespace: {ns}\n");
    }

    /// <summary>
    /// Mockup 3d in full, captured at <see cref="T0"/>: the crash-looping payment-gateway pod with init container
    /// <c>migrate</c> (Completed, exit 0), <c>gateway</c> (Waiting CrashLoopBackOff, last exit 137 OOMKilled after 41 s,
    /// 14 restarts, limit 512 Mi) and <c>otel-sidecar</c> (Running, no limits); Secret-sourced env rows included.
    /// </summary>
    public static K8sPodDetail CrashLoopPodDetail()
    {
        const string name = "payment-gateway-7d9f8-x2k4q";
        var basis = PaymentGatewayPodDetail();
        var oomAt = T0.AddMinutes(-2);
        return basis with
        {
            Pod = basis.Pod with { TotalContainers = 2, ReadyContainers = 1 },
            PodIp = "10.244.3.118",
            HostIp = "10.240.0.7",
            VolumeCount = 2,
            Node = new K8sNodeHealth("aks-pool1-38211-vmss000004", "True", [], 0.91),
            Containers =
            [
                new K8sContainer("migrate", true, "ghcr.io/nervecenter/payment-gateway:2.14.1", "Terminated", "Completed",
                    new K8sTermination(0, "Completed", T0.AddMinutes(-38).AddSeconds(-5), T0.AddMinutes(-38)), null, 0, false, [],
                    new K8sResources(null, null, null, null)),
                new K8sContainer("gateway", false, "ghcr.io/nervecenter/payment-gateway:2.14.1", "Waiting", "CrashLoopBackOff",
                    null, new K8sTermination(137, "OOMKilled", oomAt.AddSeconds(-41), oomAt), 14, false, ["8080/http", "9090/metrics"],
                    new K8sResources(500, 512 * MiB, null, 512 * MiB)),
                new K8sContainer("otel-sidecar", false, "otel/opentelemetry-collector-contrib:0.108.0", "Running", null,
                    null, null, 0, true, ["4317/grpc"], new K8sResources(null, null, null, null)),
            ],
            Env =
            [
                new K8sEnvVar("gateway", "PORT", "8080", K8sEnvSource.Literal, null),
                new K8sEnvVar("gateway", "LOG_LEVEL", "info", K8sEnvSource.Literal, null),
                new K8sEnvVar("gateway", "REDIS_URL", "redis://cart-redis:6379/0", K8sEnvSource.ConfigMap, "gateway-config"),
                new K8sEnvVar("gateway", "MERCHANT_CACHE_SIZE", "50000", K8sEnvSource.ConfigMap, "gateway-config"),
                new K8sEnvVar("gateway", "STRIPE_SECRET_KEY", null, K8sEnvSource.Secret, "stripe-live"),
                new K8sEnvVar("gateway", "DB_PASSWORD", null, K8sEnvSource.Secret, "pg-app-rw"),
                new K8sEnvVar("gateway", "POD_NAME", name, K8sEnvSource.FieldRef, "metadata.name"),
                new K8sEnvVar("gateway", "OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317", K8sEnvSource.Literal, null),
                new K8sEnvVar("otel-sidecar", "OTEL_LOG_LEVEL", "warn", K8sEnvSource.Literal, null),
            ],
        };
    }

    /// <summary>
    /// The nodes from mockup 3e, in API (name) order: 12 nodes in 2 pools. pool1 (5 × Standard_D8s_v5) holds
    /// aks-pool1-38211-vmss00000a — Ready Unknown for 6 minutes and cordoned, 34 pods — and aks-pool1-38211-vmss000004 with
    /// MemoryPressure; pool2 (7 × Standard_E16s_v5) is tainted workload=memory. 11 ready · 1 cordoned.
    /// </summary>
    public static IReadOnlyList<K8sNodeInfo> Nodes()
    {
        K8sNodeInfo Make(string pool, string suffix, int i, string type, string ip, long cpuAlloc, long memAlloc, long cpuReq, long memReq,
            int pods, int ageDays, IReadOnlyList<string> taints) =>
            new($"aks-{pool}-{suffix}", pool, type, ip, "True", T0.AddDays(-3), [], ["agent"], taints, "v1.30.4", false,
                cpuAlloc, memAlloc, cpuReq, memReq, pods, T0.AddDays(-ageDays), $"azure:///vm-{pool}-{i}",
                new Dictionary<string, string> { ["agentpool"] = pool });

        var nodes = new List<K8sNodeInfo>();
        for (var i = 1; i <= 4; i++)
        {
            nodes.Add(Make("pool1", $"38211-vmss{i:x6}", i, "Standard_D8s_v5", $"10.240.0.{3 + i}", 7900, 28 * GiB,
                4600 + (i * 100), 21 * GiB, 35 + i, 38, []));
        }

        nodes[3] = nodes[3] with { Pressures = ["MemoryPressure"], CpuRequestedMilli = 5600, MemRequestedBytes = 25 * GiB + GiB / 2, PodCount = 41 };
        nodes.Add(Make("pool1", "38211-vmss00000a", 10, "Standard_D8s_v5", "10.240.0.13", 7900, 28 * GiB, 4600, 21 * GiB, 34, 38, [])
            with { ReadyStatus = "Unknown", ReadySince = T0.AddMinutes(-6), Unschedulable = true });
        for (var i = 0; i < 7; i++)
        {
            nodes.Add(Make("pool2", $"40917-vmss{i:x6}", i, "Standard_E16s_v5", $"10.240.1.{4 + i}", 15800, 119 * GiB,
                7000 - (i * 100), 82 * GiB, 22 + i, 12, ["workload=memory:NoSchedule"]));
        }

        return nodes;
    }

    // ---- Config (mockup 3f) ----

    /// <summary>gateway-config's PROVIDERS_JSON: one long single-line JSON document (collapses as "{…} {size}").</summary>
    public static readonly string ProvidersJson =
        "{\"providers\":[" + string.Join(",", Enumerable.Range(1, 22).Select(i =>
            $"{{\"name\":\"provider-{i}\",\"endpoint\":\"https://p{i}.example/api/v2\",\"timeoutMs\":3000}}")) + "]}";

    /// <summary>gateway-config's logging.yaml: 14 lines (collapses as "14 lines").</summary>
    public static readonly string LoggingYaml = string.Join("\n",
    [
        "level: info", "format: json", "outputs:", "  - stdout", "loggers:", "  payment: debug", "  http: warn", "  redis: warn",
        "  stripe: info", "  klarna: info", "sampling:", "  initial: 100", "  thereafter: 100", "redact: [card_number, cvv]",
    ]) + "\n";

    public static K8sConfigMap GatewayConfig() =>
        new("checkout", "gateway-config",
            new Dictionary<string, string>
            {
                ["REDIS_URL"] = "redis://cart-redis:6379/0", ["MERCHANT_CACHE_SIZE"] = "50000", ["CACHE_TTL_SECONDS"] = "900",
                ["FEATURE_FLAGS"] = "3ds2=on,apple_pay=on,klarna=off", ["PROVIDERS_JSON"] = ProvidersJson, ["logging.yaml"] = LoggingYaml,
            },
            new Dictionary<string, long>(), T0.AddDays(-41), "1001");

    /// <summary>
    /// Mockup 3f's checkout ConfigMaps: gateway-config (6 keys, used by payment-gateway via env and volume),
    /// reports-config (unused) and kube-root-ca.crt (only the automatic projected token volume references it).
    /// </summary>
    public static K8sConfigData ConfigData() =>
        new(
        [
            GatewayConfig(),
            new("checkout", "kube-root-ca.crt",
                new Dictionary<string, string> { ["ca.crt"] = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n" },
                new Dictionary<string, long>(), T0.AddDays(-41), "12"),
            new("checkout", "reports-config",
                new Dictionary<string, string> { ["REPORT_BUCKET"] = "reports-eu", ["SCHEDULE"] = "0 3 * * *" },
                new Dictionary<string, long> { ["logo.png"] = 2048 }, T0.AddDays(-1), "2002"),
        ],
        [
            new("checkout", "gateway-config", "payment-gateway", "env"),
            new("checkout", "gateway-config", "payment-gateway", "volume"),
            new("checkout", "kube-root-ca.crt", "payment-gateway", "projected"),
        ]);

    /// <summary>Mockup 3f's checkout Secrets: metadata only, checkout-tls expiring in 9 days.</summary>
    public static IReadOnlyList<SecretSummary> Secrets() =>
    [
        new("checkout", "stripe-live", "Opaque", ["STRIPE_SECRET_KEY", "STRIPE_WEBHOOK_SECRET"], T0.AddDays(-120), null),
        new("checkout", "pg-app-rw", "Opaque", ["DB_PASSWORD", "DB_USER"], T0.AddDays(-41), null),
        new("checkout", "checkout-tls", "kubernetes.io/tls", ["tls.crt", "tls.key"], T0.AddDays(-80), T0.AddDays(9)),
        new("checkout", "ghcr-pull", "kubernetes.io/dockerconfigjson", [".dockerconfigjson"], T0.AddDays(-41), null),
    ];
}
