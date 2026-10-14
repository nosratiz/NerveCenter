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
            ])],
            new Dictionary<string, bool>
            {
                ["nodes/patch"] = false, ["pods/eviction/create"] = false, ["nodes/list"] = false, ["pods/list-all"] = true,
            });

    public static IReadOnlyList<K8sNamespace> Namespaces() =>
        [new("checkout", 18), new("payments", 11), new("payments-canary", 2)];

    /// <summary>A minimal healthy overview: enough for snapshot-store round trips.</summary>
    public static K8sOverview Overview(DateTimeOffset? at = null) =>
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
        Overview(T0) with
        {
            Nodes = nodes ?? [Node()],
            ProblemPods = problemPods ?? [],
            ProblemWorkloads = problemWorkloads ?? [],
            RecentWarnings = warnings ?? [],
            WarningCount = warnings?.Count ?? 0,
        };
}
