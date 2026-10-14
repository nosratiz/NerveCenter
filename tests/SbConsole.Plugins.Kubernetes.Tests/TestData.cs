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
}
