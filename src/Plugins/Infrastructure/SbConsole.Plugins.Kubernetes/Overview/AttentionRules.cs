using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Overview;

/// <summary>One "Needs attention" row. <paramref name="Href"/> already carries the connectionId when the caller's link adds it.</summary>
public sealed record AttentionItem(
    string Chip /*crash loop|not ready|unavailable|job failed|pending*/, string Severity /*Error|Warning*/,
    string? Namespace, string Title, string Detail, string LinkKind /*Pod|Node|Deployment|Jobs|Events*/, string Href);

/// <summary>
/// Pure "Needs attention" rules over an overview, worst first: crash loops (by restarts), not-ready nodes,
/// unavailable Deployments, failed Jobs, then unschedulable pods grouped per namespace.
/// </summary>
public static class AttentionRules
{
    public const int Cap = 10;
    private const int MessageMax = 120;

    public static readonly string JobsHref = KubernetesPlugin.WorkloadsHref + "?tab=jobs";

    /// <summary>The first <see cref="Cap"/> items.</summary>
    public static IReadOnlyList<AttentionItem> Build(K8sOverview overview, DateTimeOffset now, Func<string, string> link) =>
        BuildAll(overview, now, link).Take(Cap).ToList();

    /// <summary>Every item, uncapped (the page's "N more" and the nav badge count).</summary>
    public static IReadOnlyList<AttentionItem> BuildAll(K8sOverview overview, DateTimeOffset now, Func<string, string> link)
    {
        var items = new List<AttentionItem>();

        items.AddRange(overview.ProblemPods
            .Where(p => p.Status == "CrashLoopBackOff")
            .OrderByDescending(p => p.Restarts)
            .ThenBy(p => p.Namespace, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new AttentionItem("crash loop", "Error", p.Namespace, p.Name, CrashDetail(p), "Pod",
                link(KubernetesPlugin.PodHref(p.Namespace, p.Name)))));

        items.AddRange(overview.Nodes
            .Where(n => n.ReadyStatus != "True")
            .Select(n => new AttentionItem("not ready", "Error", null, n.Name, NodeDetail(n, now), "Node",
                link(KubernetesPlugin.NodesHref))));

        items.AddRange(overview.ProblemWorkloads
            .Where(w => w.Kind == K8sWorkloadKind.Deployment && w.IsUnhealthy)
            .Select(w => new AttentionItem("unavailable", "Error", w.Namespace, w.Name, DeploymentDetail(w), "Deployment",
                link(KubernetesPlugin.DeploymentHref(w.Namespace, w.Name)))));

        items.AddRange(overview.ProblemWorkloads
            .Where(w => w.Kind == K8sWorkloadKind.Job && w.JobStatus == "Failed")
            .Select(w => new AttentionItem("job failed", "Error", w.Namespace, w.Name, w.FailureReason ?? "Failed", "Jobs",
                link(JobsHref))));

        items.AddRange(overview.ProblemPods
            .Where(p => p.UnschedulableMessage is not null && p.Status != "CrashLoopBackOff")
            .GroupBy(p => p.Namespace, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var count = g.Count();
                return new AttentionItem("pending", "Warning", g.Key,
                    count == 1 ? "1 pod unschedulable" : $"{count} pods unschedulable",
                    Truncate(g.First().UnschedulableMessage!), "Events", link(KubernetesPlugin.EventsHref));
            }));

        return items;
    }

    /// <summary>A link function that appends <c>connectionId</c> (with <c>&amp;</c> when the href already has a query).</summary>
    public static Func<string, string> WithConnection(Guid connectionId) =>
        href => href + (href.Contains('?') ? "&" : "?") + "connectionId=" + connectionId;

    private static string CrashDetail(K8sPodSummary p)
    {
        var detail = p.Restarts == 1 ? "1 restart" : $"{p.Restarts} restarts";
        if (p.LastTerminationReason is { } reason)
        {
            detail += p.LastExitCode is { } code ? $" · last exit {reason} ({code})" : $" · last exit {reason}";
        }

        return detail;
    }

    private static string NodeDetail(K8sNodeInfo n, DateTimeOffset now)
    {
        var state = n.ReadyStatus == "False" ? "NotReady" : n.ReadyStatus;
        if (n.ReadySince is { } since)
        {
            state += " for " + K8sFormat.Age(now - since);
        }

        return $"{state} · {n.PodCount} {(n.PodCount == 1 ? "pod" : "pods")} affected";
    }

    private static string DeploymentDetail(K8sWorkload w)
    {
        var detail = $"{w.Available}/{w.Desired} available";
        var stalled = w.Conditions.FirstOrDefault(c => c.Type == "Progressing" && c.Status == "False");
        return stalled?.Reason is { } reason ? $"{detail} · {reason}" : detail;
    }

    private static string Truncate(string message) =>
        message.Length <= MessageMax ? message : message[..(MessageMax - 1)] + "…";
}
