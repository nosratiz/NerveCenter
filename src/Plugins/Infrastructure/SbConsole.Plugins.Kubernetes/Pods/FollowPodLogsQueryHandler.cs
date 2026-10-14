using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Pods;

/// <summary>
/// Follows a container's current log. Unlike the other queries this does not return a <see cref="PluginResult"/>: an
/// unknown connection throws <see cref="InvalidOperationException"/>(<see cref="HandlerRunner.ConnectionNotFound"/>) and
/// stream failures propagate (after being logged) — the log viewer shows them through <see cref="FriendlyKubernetesError"/>.
/// </summary>
public sealed class FollowPodLogsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<FollowPodLogsQueryHandler> logger) : IScopedService
{
    public async IAsyncEnumerable<string> StreamAsync(
        Guid connectionId, string ns, string pod, K8sLogRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct)
            ?? throw new InvalidOperationException(HandlerRunner.ConnectionNotFound);

        await using var lines = operations.FollowLogsAsync(secret, ns, pod, request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try
            {
                more = await lines.MoveNextAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Following the log of {Namespace}/{Pod} container {Container} failed", ns, pod, request.Container);
                throw;
            }

            if (!more)
            {
                yield break;
            }

            yield return lines.Current;
        }
    }
}
