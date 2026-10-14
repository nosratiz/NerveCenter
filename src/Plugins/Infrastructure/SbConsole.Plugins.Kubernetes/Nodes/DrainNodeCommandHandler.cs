using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Nodes;

public sealed record DrainOptions(bool DeleteEmptyDirData, bool Force, int GracePeriodSeconds);

/// <summary>Drain one node. <see cref="K8sTarget.Namespace"/> is null: nodes are cluster-scoped.</summary>
public sealed record DrainCommand(K8sTarget Target, DrainOptions Options);

public enum DrainPodOutcome { Evicted, Deleted, Blocked, Skipped, Failed }

public sealed record DrainPodResult(string Namespace, string Name, DrainPodOutcome Outcome, string? Reason);

/// <summary><see cref="Last"/> is null on the first report (after planning), which carries only the total.</summary>
public sealed record DrainProgress(int Done, int Total, DrainPodResult? Last);

/// <summary>Per-pod results in plan order (pods never started because of a stop or the deadline are absent).</summary>
public sealed record DrainResult(IReadOnlyList<DrainPodResult> Pods, bool Cancelled, bool TimedOut);

/// <summary>
/// kubectl drain through the Eviction API (spec §10.1): cordon, plan, then evict every non-DaemonSet pod
/// <see cref="DrainPlanner.Parallelism"/> at a time. A PDB refusal (429) is retried every <see cref="PdbRetryInterval"/>
/// until the overall <see cref="Deadline"/>, or with Force the pod is deleted instead. A caller stop schedules nothing
/// more and leaves the node cordoned. One <c>k8s.node.drain</c> audit row (Destructive) is written at the end, whatever
/// happened; it succeeds only when every pod went and nothing was stopped. Confirmation is the dialog's job.
/// </summary>
public sealed class DrainNodeCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit, TimeProvider clock,
    ILogger<DrainNodeCommandHandler> logger) : IScopedService
{
    public const string Action = "k8s.node.drain";
    public static readonly TimeSpan PdbRetryInterval = TimeSpan.FromSeconds(5);
    private const int MaxAuditPods = 50;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The overall drain timeout (5 min); tests shorten it.</summary>
    internal TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(5);

    public async Task<PluginResult<DrainResult>> HandleAsync(DrainCommand cmd, IProgress<DrainProgress>? progress, CancellationToken ct = default)
    {
        var t = cmd.Target;
        var options = cmd.Options;
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, t.ConnectionId, $"{Action} on {t.AuditTarget}", ct);
        if (secret is null)
        {
            return PluginResult<DrainResult>.Fail(secretError!);
        }

        using var deadline = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;

        DrainPlan plan;
        try
        {
            await operations.SetUnschedulableAsync(secret, t.Name, true, token);
            plan = DrainPlanner.Plan(await operations.GetDrainInputsAsync(secret, t.Name, token), options.GracePeriodSeconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var stopped = new DrainResult([], ct.IsCancellationRequested, !ct.IsCancellationRequested);
            await AuditAsync(t, options, stopped, 0);
            return PluginResult<DrainResult>.Ok(stopped);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action} on {Target} failed before evicting.", Action, t.AuditTarget);
            var friendly = FriendlyKubernetesError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, t.AuditTarget, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult<DrainResult>.Fail(friendly);
        }

        var work = plan.Pods.Where(p => p.Group != DrainGroup.DaemonSet).ToList();
        var results = new DrainPodResult?[work.Count];
        var done = 0;
        progress?.Report(new DrainProgress(0, work.Count, null));

        using (var gate = new SemaphoreSlim(DrainPlanner.Parallelism))
        {
            var running = new List<Task>(work.Count);
            for (var i = 0; i < work.Count; i++)
            {
                try
                {
                    await gate.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break; // stopped or out of time: schedule nothing more
                }

                running.Add(RunOneAsync(i));
            }

            await Task.WhenAll(running);

            async Task RunOneAsync(int index)
            {
                try
                {
                    var result = await DrainPodAsync(secret, work[index], options, token, deadline.Token);
                    results[index] = result;
                    progress?.Report(new DrainProgress(Interlocked.Increment(ref done), work.Count, result));
                }
                finally
                {
                    gate.Release();
                }
            }
        }

        var cancelled = ct.IsCancellationRequested;
        var drain = new DrainResult(results.OfType<DrainPodResult>().ToList(), cancelled, !cancelled && deadline.IsCancellationRequested);
        await AuditAsync(t, options, drain, work.Count);
        return PluginResult<DrainResult>.Ok(drain);
    }

    private async Task<DrainPodResult> DrainPodAsync(string secret, DrainPodPlan plan, DrainOptions options, CancellationToken token, CancellationToken deadline)
    {
        var (ns, name) = (plan.Pod.Namespace, plan.Pod.Name);
        if (plan.HasEmptyDir && !options.DeleteEmptyDirData)
        {
            return new DrainPodResult(ns, name, DrainPodOutcome.Skipped, "has emptyDir data");
        }

        if (plan.Group == DrainGroup.Unmanaged && !options.Force)
        {
            return new DrainPodResult(ns, name, DrainPodOutcome.Skipped, "no controller — use force");
        }

        var blocked = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                switch (await operations.EvictPodAsync(secret, ns, name, options.GracePeriodSeconds, token))
                {
                    case K8sEvictionOutcome.Evicted:
                        return new DrainPodResult(ns, name, DrainPodOutcome.Evicted, null);
                    case K8sEvictionOutcome.NotFound:
                        return new DrainPodResult(ns, name, DrainPodOutcome.Evicted, "already gone");
                }

                blocked = true;
                if (options.Force)
                {
                    await operations.DeletePodAsync(secret, ns, name, options.GracePeriodSeconds, token);
                    return new DrainPodResult(ns, name, DrainPodOutcome.Deleted, "forced past a PodDisruptionBudget");
                }

                await Task.Delay(PdbRetryInterval, clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var why = deadline.IsCancellationRequested ? "timed out" : "stopped";
            return blocked
                ? new DrainPodResult(ns, name, DrainPodOutcome.Blocked, $"PodDisruptionBudget still blocking ({why})")
                : new DrainPodResult(ns, name, DrainPodOutcome.Failed, why);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Draining pod {Namespace}/{Name} failed.", ns, name);
            return new DrainPodResult(ns, name, DrainPodOutcome.Failed, FriendlyKubernetesError.From(ex));
        }
    }

    private Task AuditAsync(K8sTarget target, DrainOptions options, DrainResult result, int total)
    {
        int Count(DrainPodOutcome o) => result.Pods.Count(p => p.Outcome == o);
        var (blocked, failed, skipped) = (Count(DrainPodOutcome.Blocked), Count(DrainPodOutcome.Failed), Count(DrainPodOutcome.Skipped));

        var detail = $"grace {options.GracePeriodSeconds.ToString(Inv)}s · force {Lower(options.Force)} · emptyDir {Lower(options.DeleteEmptyDirData)}"
                     + $" · evicted {Count(DrainPodOutcome.Evicted).ToString(Inv)} · deleted {Count(DrainPodOutcome.Deleted).ToString(Inv)}"
                     + $" · blocked {blocked.ToString(Inv)} · skipped {skipped.ToString(Inv)}";
        if (failed > 0)
        {
            detail += $" · failed {failed.ToString(Inv)}";
        }

        var progressText = $"{result.Pods.Count.ToString(Inv)} of {total.ToString(Inv)}";
        if (result.Cancelled)
        {
            detail += $" · cancelled after {progressText}";
        }
        else if (result.TimedOut)
        {
            detail += $" · timed out after {progressText}";
        }

        var gone = result.Pods.Where(p => p.Outcome is DrainPodOutcome.Evicted or DrainPodOutcome.Deleted)
            .Select(p => $"{p.Namespace}/{p.Name}")
            .ToList();
        if (gone.Count > 0)
        {
            detail += " · pods: " + string.Join(", ", gone.Take(MaxAuditPods)) + (gone.Count > MaxAuditPods ? ", …" : "");
        }

        var succeeded = blocked == 0 && failed == 0 && skipped == 0 && !result.Cancelled && !result.TimedOut;
        return HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target.AuditTarget, ActionRisk.Destructive, succeeded, detail);
    }

    private static string Lower(bool value) => value ? "true" : "false";
}
