using System.Security.Cryptography;
using System.Text;
using k8s;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>
/// One <see cref="IKubernetes"/> per connection secret, cached by the secret's SHA-256 (never the secret itself)
/// so HTTP connections are reused across calls. At most <see cref="MaxClients"/>; the least-recently used is evicted and disposed.
/// </summary>
public sealed class KubernetesClientFactory : ISingletonService, IDisposable
{
    public const int MaxClients = 32;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, IKubernetes Client)>> _byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, IKubernetes Client)> _recency = new(); // first = most recently used
    private bool _disposed;

    /// <exception cref="FormatException">The secret isn't a valid kubeconfig connection secret.</exception>
    public IKubernetes Get(string secret)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret ?? string.Empty)));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_byKey.TryGetValue(key, out var hit))
            {
                _recency.Remove(hit);
                _recency.AddFirst(hit);
                return hit.Value.Client;
            }
        }

        var client = Create(KubeconfigSettings.Parse(secret!));

        IKubernetes? evicted = null;
        IKubernetes result;
        lock (_lock)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(KubernetesClientFactory));
            }

            if (_byKey.TryGetValue(key, out var raced))
            {
                // Another caller built the same client first; keep theirs.
                evicted = client;
                _recency.Remove(raced);
                _recency.AddFirst(raced);
                result = raced.Value.Client;
            }
            else
            {
                _byKey[key] = _recency.AddFirst((key, client));
                result = client;
                if (_byKey.Count > MaxClients)
                {
                    var oldest = _recency.Last!;
                    _recency.RemoveLast();
                    _byKey.Remove(oldest.Value.Key);
                    evicted = oldest.Value.Client;
                }
            }
        }

        evicted?.Dispose();
        return result;
    }

    private static k8s.Kubernetes Create(KubeconfigSettings settings)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(settings.Kubeconfig));
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream, currentContext: settings.Context);
        config.HttpClientTimeout = Timeout;
        return new k8s.Kubernetes(config);
    }

    public void Dispose()
    {
        List<IKubernetes> clients;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            clients = _recency.Select(e => e.Client).ToList();
            _recency.Clear();
            _byKey.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
