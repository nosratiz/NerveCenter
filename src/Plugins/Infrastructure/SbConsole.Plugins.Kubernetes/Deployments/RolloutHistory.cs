using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Deployments;

/// <summary>One rollout-history row (one ReplicaSet revision). <see cref="Image"/> joins the template's images with " · ".</summary>
public sealed record RolloutRow(long Revision, string Image, DateTimeOffset CreatedAt, string? ChangeCause, bool IsCurrent, bool IsStuck, bool CanRollback, int ReadyPods);

/// <summary>
/// A rollout past its progress deadline: the new revision, how long since it started, how many of its pods crash-loop
/// (<see cref="Reason"/> "OOM" when one was OOMKilled), and the newest older revision still serving ready pods.
/// </summary>
public sealed record StuckRollout(long NewRevision, TimeSpan ProgressingFor, int CrashLoopingPods, string? Reason, long? ServingRevision, int ServingPods);

/// <summary>Pure rollout logic for the Deployment detail page (spec §7). No API calls.</summary>
public static class RolloutHistory
{
    public static IReadOnlyList<RolloutRow> Build(K8sDeploymentDetail d)
    {
        var stuck = IsProgressDeadlineExceeded(d);
        return d.ReplicaSets
            .OrderByDescending(r => r.Revision)
            .Select(r => new RolloutRow(
                r.Revision, K8sFormat.Images(r.Images), r.CreatedAt, r.ChangeCause,
                r.IsCurrent, r.IsCurrent && stuck, !r.IsCurrent && r.HasTemplate, r.Ready))
            .ToList();
    }

    public static StuckRollout? Stuck(K8sDeploymentDetail d, DateTimeOffset now)
    {
        if (!IsProgressDeadlineExceeded(d))
        {
            return null;
        }

        var current = d.ReplicaSets.FirstOrDefault(r => r.IsCurrent);
        var started = current?.CreatedAt ?? Progressing(d)?.LastTransition ?? now;
        var crashLooping = current is null
            ? []
            : d.Pods.Where(p => p.ReplicaSet == current.Name && p.Status == "CrashLoopBackOff").ToList();
        var serving = Serving(d);
        return new StuckRollout(
            current?.Revision ?? d.CurrentRevision,
            now - started < TimeSpan.Zero ? TimeSpan.Zero : now - started,
            crashLooping.Count,
            crashLooping.Any(p => p.LastTerminationReason == "OOMKilled") ? "OOM" : null,
            serving?.Revision,
            serving?.Ready ?? 0);
    }

    /// <summary>The revision a "Rollback" with no choice goes to: the serving revision, else the newest older one with a template.</summary>
    public static long? RollbackTarget(K8sDeploymentDetail d)
    {
        var serving = Serving(d);
        if (serving is { HasTemplate: true })
        {
            return serving.Revision;
        }

        return d.ReplicaSets
            .Where(r => !r.IsCurrent && r.HasTemplate && r.Revision < d.CurrentRevision)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefault()?.Revision;
    }

    private static K8sReplicaSet? Serving(K8sDeploymentDetail d) =>
        d.ReplicaSets
            .Where(r => !r.IsCurrent && r.Ready > 0)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefault();

    private static K8sCondition? Progressing(K8sDeploymentDetail d) =>
        d.Deployment.Conditions.FirstOrDefault(c => c.Type == "Progressing");

    private static bool IsProgressDeadlineExceeded(K8sDeploymentDetail d) =>
        Progressing(d) is { Status: "False", Reason: "ProgressDeadlineExceeded" };
}
