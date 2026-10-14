using k8s;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>
/// KubernetesClient implementation of <see cref="IKubernetesOperations"/>. Clients come from
/// <see cref="KubernetesClientFactory"/> (one per secret). Exceptions propagate to the handlers, which turn them
/// into <see cref="FriendlyKubernetesError"/> sentences; only <see cref="TestConnectionAsync"/> catches its own.
/// </summary>
public sealed partial class KubernetesOperations(KubernetesClientFactory clients, TimeProvider clock, ILogger<KubernetesOperations> logger)
    : IKubernetesOperations, ISingletonService
{
    private readonly TimeProvider _clock = clock;

    private IKubernetes Client(string secret) => clients.Get(secret);

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            var client = Client(secret);
            var version = await client.Version.GetCodeAsync(cancellationToken: ct);
            var identity = await GetIdentityAsync(client, secret, ct);
            return new ConnectionTestResult(true, null, $"{version.GitVersion} · as {identity}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Kubernetes connection test failed");
            return new ConnectionTestResult(false, FriendlyKubernetesError.From(ex));
        }
    }

    public async Task<IReadOnlyList<K8sNamespace>> ListNamespacesAsync(string secret, CancellationToken ct = default)
    {
        var client = Client(secret);
        var namespacesTask = client.CoreV1.ListNamespaceAsync(cancellationToken: ct);
        var countsTask = CountPodsByNamespaceAsync(client, ct);
        var namespaces = await namespacesTask;
        var counts = await countsTask;
        return namespaces.Items
            .Select(n => n.Metadata.Name)
            .Order(StringComparer.Ordinal)
            .Select(name => new K8sNamespace(name, counts.GetValueOrDefault(name)))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, int>> CountPodsByNamespaceAsync(IKubernetes client, CancellationToken ct)
    {
        try
        {
            var pods = await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct);
            return pods.Items
                .GroupBy(p => p.Metadata.NamespaceProperty ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        }
        catch (k8s.Autorest.HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing pods in all namespaces is forbidden; namespace pod counts are 0");
            return new Dictionary<string, int>();
        }
    }
}
