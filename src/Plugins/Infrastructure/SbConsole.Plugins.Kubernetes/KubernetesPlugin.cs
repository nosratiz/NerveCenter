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

    // Metrics, problems and the badge all derive from one overview per connection per 15 s (success or failure).
    private static readonly TimeSpan OverviewTtl = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, K8sOverview? Overview, Exception? Error)> _overviewCache = new();

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

    /// <summary>The cached overview for the secret, or the cached failure (failures are cached too).</summary>
    private async Task<(K8sOverview? Overview, Exception? Error)> CachedOverviewAsync(string secret, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var now = _clock.GetUtcNow();
        if (_overviewCache.TryGetValue(key, out var cached) && now - cached.At < OverviewTtl)
        {
            return (cached.Overview, cached.Error);
        }

        K8sOverview? overview = null;
        Exception? error = null;
        try
        {
            overview = await _operations!.GetOverviewAsync(secret, [], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No logger on the plugin instance; the failure is surfaced to callers that need it.
            error = ex;
        }

        foreach (var entry in _overviewCache)
        {
            if (now - entry.Value.At >= OverviewTtl)
            {
                _overviewCache.TryRemove(entry.Key, out _);
            }
        }

        _overviewCache[key] = (now, overview, error);
        return (overview, error);
    }

    /// <summary>Nodes ready and crash-looping pods across the whole cluster; any failure reports nothing.</summary>
    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        if (_operations is null || await CachedOverviewAsync(connectionString, ct) is not { Overview: { } overview })
        {
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

        var (overview, error) = await CachedOverviewAsync(connectionString, ct);
        if (overview is null)
        {
            // The host marks a connection "not checked" only when this throws; [] would read as healthy.
            throw new InvalidOperationException(
                FriendlyKubernetesError.From(error ?? new InvalidOperationException("No overview available.")), error);
        }

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

        try
        {
            if (await CachedOverviewAsync(connectionString, ct) is not { Overview: { } overview })
            {
                return null;
            }

            var total = AttentionRules.BuildAll(overview, _clock.GetUtcNow(), href => href).Count;
            return total > 0 ? total : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
