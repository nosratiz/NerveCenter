using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>Global constraint: parallel per-namespace fetches are capped at 8.</summary>
    internal const int MaxNamespaceFanOut = 8;

    private static readonly TimeSpan WarningsWindow = TimeSpan.FromHours(1);
    private const int MaxRecentWarnings = 50;

    public async Task<K8sOverview> GetOverviewAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var client = Client(secret);
        var kubeconfig = KubeconfigSettings.Parse(secret).Summary;
        var now = _clock.GetUtcNow();
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
        bool InScope(string? ns) => scope is null || (ns is not null && scope.Contains(ns));

        // One gate per overview call: pods, deployments, jobs and events share the cap of 8 namespace requests.
        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var versionTask = client.Version.GetCodeAsync(cancellationToken: ct);
        var nodesTask = ListNodesOrEmptyAsync(client, ct);
        var podsTask = ListScopedAsync(namespaces,
            async () => (await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var deploymentsTask = ListScopedAsync(namespaces,
            async () => (await client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var jobsTask = ListScopedAsync(namespaces,
            async () => (await client.BatchV1.ListJobForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var eventsTask = FetchEventsAsync(client, namespaces, "Warning", gate, logger, ct);
        var namespaceCountTask = CountNamespacesAsync(client, namespaces, ct);
        var identityTask = GetIdentityAsync(client, secret, ct);
        var usageTask = NodeUsageAsync(client, ct);

        // Await everything before reading any result, so one failure never leaves the others unobserved.
        await Task.WhenAll(versionTask, nodesTask, podsTask, deploymentsTask, jobsTask, eventsTask, namespaceCountTask, identityTask, usageTask);

        // A cluster-wide pod list has every pod (node requests stay accurate); counts use only the selected namespaces.
        var allPods = podsTask.Result;
        var summaries = allPods.Select(K8sMappers.Pod).ToList();
        var (nodeList, nodesForbidden) = nodesTask.Result;
        var nodes = NodesWithPods(nodeList, summaries);
        var scoped = summaries.Where(p => InScope(p.Namespace)).ToList();

        var limits = allPods
            .Where(p => p.Status?.Phase is "Running" or "Pending")
            .Select(K8sMappers.PodLimits)
            .Aggregate((Cpu: 0L, Mem: 0L), (acc, l) => (acc.Cpu + l.Cpu, acc.Mem + l.Mem));

        var problemWorkloads = deploymentsTask.Result.Select(K8sMappers.Deployment)
            .Where(d => InScope(d.Namespace) && d.IsUnhealthy)
            .Concat(jobsTask.Result.Select(j => K8sMappers.Job(j, now)).Where(j => InScope(j.Namespace) && j.JobStatus == "Failed"))
            .ToList();

        var warnings = eventsTask.Result
            .Where(e => e.Type == "Warning" && InScope(e.Namespace) && now - e.LastSeen <= WarningsWindow)
            .OrderByDescending(e => e.LastSeen)
            .ToList();

        var cpuAllocatable = nodes.Sum(n => n.CpuAllocatableMilli);
        var memAllocatable = nodes.Sum(n => n.MemAllocatableBytes);
        var usage = usageTask.Result;

        return new K8sOverview(
            now,
            new K8sClusterInfo(kubeconfig.Server, kubeconfig.Context, versionTask.Result.GitVersion, K8sMappers.Provider(nodes),
                identityTask.Result, namespaceCountTask.Result),
            nodes,
            new K8sPodCounts(
                scoped.Count(p => p.Phase == "Running"), scoped.Count(p => p.Phase == "Pending"),
                scoped.Count(p => p.Phase == "Failed"), scoped.Count(p => p.Phase == "Succeeded")),
            scoped.Where(p => p.Status == "CrashLoopBackOff"
                    || (p.Phase == "Pending" && p.UnschedulableMessage is not null)
                    || p.Phase == "Failed")
                .ToList(),
            problemWorkloads,
            warnings.Take(MaxRecentWarnings).ToList(),
            warnings.Count,
            limits.Cpu,
            limits.Mem,
            usage is { } u && cpuAllocatable > 0 ? (double)u.Cpu / cpuAllocatable : null,
            usage is { } m && memAllocatable > 0 ? (double)m.Mem / memAllocatable : null,
            nodesForbidden);
    }

    /// <summary>Nodes are cluster-scoped; an account that can't list them (e.g. the built-in "view" role) sees none, flagged as forbidden.</summary>
    private async Task<(IList<V1Node> Nodes, bool Forbidden)> ListNodesOrEmptyAsync(IKubernetes client, CancellationToken ct)
    {
        try
        {
            return ((await client.CoreV1.ListNodeAsync(cancellationToken: ct)).Items, false);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing nodes is forbidden; the overview shows no nodes");
            return ([], true);
        }
    }

    private async Task<int> CountNamespacesAsync(IKubernetes client, IReadOnlyList<string> namespaces, CancellationToken ct)
    {
        try
        {
            return (await client.CoreV1.ListNamespaceAsync(cancellationToken: ct)).Items.Count;
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing namespaces is forbidden; counting the selected ones");
            return namespaces.Count;
        }
    }

    /// <summary>Σ node CPU (millicores) and memory (bytes) from metrics.k8s.io; null when metrics-server is absent or fails.</summary>
    private async Task<(long Cpu, long Mem)?> NodeUsageAsync(IKubernetes client, CancellationToken ct)
    {
        try
        {
            // The metrics extension takes no CancellationToken; WaitAsync gives the caller its cancellation back.
            var request = client.GetKubernetesNodesMetricsAsync();
            _ = request.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var metrics = await request.WaitAsync(ct);
            long cpu = 0, mem = 0;
            foreach (var item in metrics.Items ?? [])
            {
                if (item.Usage is null)
                {
                    continue;
                }

                if (item.Usage.TryGetValue("cpu", out var c))
                {
                    cpu += K8sFormat.ParseCpuMilli(c?.ToString());
                }

                if (item.Usage.TryGetValue("memory", out var m))
                {
                    mem += K8sFormat.ParseBytes(m?.ToString());
                }
            }

            return (cpu, mem);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Node metrics are unavailable; usage is omitted");
            return null;
        }
    }

    /// <summary>
    /// Lists cluster-wide (callers filter to the selection client-side); when that is forbidden and namespaces are
    /// selected, lists each namespace instead, through the caller's shared <paramref name="gate"/> (<see cref="MaxNamespaceFanOut"/> at a time per overview). A 403 with no
    /// selection, or on a selected namespace, propagates.
    /// </summary>
    internal static async Task<IReadOnlyList<T>> ListScopedAsync<T>(
        IReadOnlyList<string> namespaces, Func<Task<IList<T>>> all, Func<string, Task<IList<T>>> one, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            return (await all()).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403 && namespaces.Count > 0)
        {
            // Namespace-scoped account: fall back to one list per selected namespace.
        }

        return await PerNamespaceAsync(namespaces, one, gate, ct);
    }

    /// <summary>One list per distinct namespace through the shared <paramref name="gate"/>, concatenated.</summary>
    internal static Task<IReadOnlyList<T>> PerNamespaceAsync<T>(
        IReadOnlyList<string> namespaces, Func<string, Task<IList<T>>> one, SemaphoreSlim gate, CancellationToken ct) =>
        FanOutAsync(namespaces.Distinct(StringComparer.Ordinal).ToList(), one, gate, ct);

    /// <summary>Runs <paramref name="one"/> per item, at most the <paramref name="gate"/>'s count at a time, concatenating the results.</summary>
    internal static async Task<IReadOnlyList<T>> FanOutAsync<TItem, T>(
        IReadOnlyList<TItem> items, Func<TItem, Task<IList<T>>> one, SemaphoreSlim gate, CancellationToken ct)
    {
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await one(item);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var lists = await Task.WhenAll(tasks);
        return lists.SelectMany(l => l).ToList();
    }
}
