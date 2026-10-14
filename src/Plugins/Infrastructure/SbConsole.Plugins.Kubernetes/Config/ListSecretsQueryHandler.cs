using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Config;

/// <summary>
/// Secret metadata in the selected namespaces (empty = all): <see cref="SecretSummary"/> only, never a value (spec §9.2).
/// A 403 reads "Your account can't list secrets."; the Config page shows it in the Secrets card alone.
/// </summary>
public sealed class ListSecretsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListSecretsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<SecretSummary>>> HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.secrets.list",
            secret => operations.ListSecretsAsync(secret, namespaces, ct), ct);
}
