using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>The map for the selection plus every description saved on this connection (keyed by node id).</summary>
public sealed record ArchitectureView(ArchitectureGraph Graph, IReadOnlyDictionary<string, ArchDescription> Descriptions);

/// <summary>Detects the dependency map for the selected namespaces (empty = all) and layers the console's overlay on it. Read-only: never audited.</summary>
public sealed class GetArchitectureQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections,
    [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store, ILogger<GetArchitectureQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<ArchitectureView>> HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.arch.get", async secret =>
        {
            var inputsTask = operations.GetArchitectureInputsAsync(secret, namespaces, ct);
            var overlay = await ArchitectureStore.ReadOverlayAsync(store, connectionId, ct);
            var descriptions = await ArchitectureStore.ReadDescriptionsAsync(store, connectionId, ct);
            var graph = ArchitectureBuilder.Build(await inputsTask, namespaces, overlay.ToOverlay());
            return new ArchitectureView(graph, descriptions);
        }, ct);
}
