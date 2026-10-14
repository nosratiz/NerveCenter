using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kubernetes.Client;
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

    // Dashboard hooks return nothing until Task 8.
    public Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([]);

    public Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardProblem>>([]);
}
