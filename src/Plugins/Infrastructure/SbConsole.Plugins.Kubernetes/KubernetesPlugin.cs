using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes;

public sealed class KubernetesPlugin : IPlugin
{
    public const string PluginId = "kubernetes";
    public const string Kind = "kubernetes";
    public const string OverviewHref = "/p/kubernetes/overview";
    public const string WorkloadsHref = "/p/kubernetes/workloads";
    public const string NodesHref = "/p/kubernetes/nodes";
    public const string ConfigHref = "/p/kubernetes/config";
    public const string EventsHref = "/p/kubernetes/events";

    public static string DeploymentHref(string ns, string name) =>
        $"/p/kubernetes/workloads/deployments/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";

    public static string PodHref(string ns, string name) =>
        $"/p/kubernetes/pods/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";

    // Resolved in Initialize: the host constructs plugins with new(), so instance hooks have no DI.
    // Until then they fail closed.
    private IKubernetesOperations? _operations;
    private TimeProvider _clock = TimeProvider.System;

    // The badge is polled by the shell; one overview per connection per 15 s is plenty.
    private static readonly TimeSpan BadgeTtl = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int? Count)> _badgeCache = new();

    public string Id => PluginId;
    public string DisplayName => "Kubernetes";
    public string? Monogram => "K8";
    public string Version => "0.1.0";
    public string ConnectionKind => Kind;
    public string ConnectionKindDisplayName => "Kubernetes";

    public Type? ConnectionFormComponentType => typeof(KubernetesConnectionFields);

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Workloads", WorkloadsHref),
        new PluginNavItem("Nodes", NodesHref),
        new PluginNavItem("Config", ConfigHref),
        new PluginNavItem("Events", EventsHref),
    ];

    public PluginContribution Contribution => new(PageCount: 7, ActionCount: 9);

    internal static ConnectionTestResult NotInitialized => new(false, "Kubernetes plugin is not initialized.");

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var summary = KubeconfigSettings.Parse(secret).Summary;
            return new Dictionary<string, string>
            {
                ["Server"] = summary.Host,
                ["Context"] = summary.Context,
                ["TLS"] = summary.TlsVerified ? "verified" : "not verified",
            };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void ConfigureServices(IServiceCollection services) =>
        services.AddMarkedServices(typeof(KubernetesPlugin).Assembly);

    public void Initialize(IServiceProvider services)
    {
        _operations = services.GetRequiredService<IKubernetesOperations>();
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    internal void InitializeForTesting(IKubernetesOperations operations, TimeProvider? clock = null)
    {
        _operations = operations;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        _operations?.TestConnectionAsync(secret, ct) ?? Task.FromResult(NotInitialized);

    /// <summary>Nodes ready and crash-looping pods across the whole cluster; any failure reports nothing.</summary>
    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        if (_operations is null)
        {
            return [];
        }

        K8sOverview overview;
        try
        {
            overview = await _operations.GetOverviewAsync(connectionString, [], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // No logger on the plugin instance; the host reports an unreachable connection on its own.
            return [];
        }

        return
        [
            new PluginDashboardMetric("Nodes ready", overview.Nodes.Count(n => n.ReadyStatus == "True")),
            new PluginDashboardMetric("Crash loops", overview.ProblemPods.Count(p => p.Status == "CrashLoopBackOff")),
        ];
    }

    /// <summary>The top five Needs-attention items, linking to plain page hrefs (the host adds no connectionId).</summary>
    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        if (_operations is null)
        {
            return [];
        }

        var overview = await _operations.GetOverviewAsync(connectionString, [], ct);
        return AttentionRules.Build(overview, _clock.GetUtcNow(), href => href)
            .Take(5)
            .Select(i => new PluginDashboardProblem(i.Severity, i.Title, i.Detail, i.Href))
            .ToList();
    }

    /// <summary>The Needs-attention count on Overview; every other page, and any failure, shows no badge.</summary>
    public async Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default)
    {
        if (_operations is null || navItemHref != OverviewHref)
        {
            return null;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString)));
        var now = _clock.GetUtcNow();
        if (_badgeCache.TryGetValue(key, out var cached) && now - cached.At < BadgeTtl)
        {
            return cached.Count;
        }

        int? count;
        try
        {
            var overview = await _operations.GetOverviewAsync(connectionString, [], ct);
            var total = AttentionRules.BuildAll(overview, now, href => href).Count;
            count = total > 0 ? total : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            // A badge is decoration: a broken probe just hides it (and is not retried for 15 s).
            count = null;
        }

        _badgeCache[key] = (now, count);
        return count;
    }
}
