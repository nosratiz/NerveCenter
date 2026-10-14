using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.KubeConfigModels;
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

    private static k8s.Kubernetes Create(KubeconfigSettings settings) => new(BuildConfiguration(settings));

    /// <summary>
    /// Builds from an in-memory config holding only the validated context, its cluster and its user, so the client's
    /// case-insensitive name lookup can never land on an entry the validator didn't approve.
    /// </summary>
    internal static KubernetesClientConfiguration BuildConfiguration(KubeconfigSettings settings)
    {
        // Parse already validated; re-validate here so a settings object built by hand can't skip it.
        var validation = KubeconfigValidator.Validate(settings.Kubeconfig, settings.Context);
        if (validation.Summary is null)
        {
            throw new FormatException(string.Join("; ", validation.Errors));
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(settings.Kubeconfig));
        var trimmed = Trim(KubernetesClientConfiguration.LoadKubeConfig(stream), validation.Summary.Context);
        var config = KubernetesClientConfiguration.BuildConfigFromConfigObject(trimmed, trimmed.CurrentContext);
        config.HttpClientTimeout = Timeout;
        return config;
    }

    /// <summary>A deep copy of exactly one context (matched by exact name), its cluster and its user.</summary>
    internal static K8SConfiguration Trim(K8SConfiguration config, string context)
    {
        var ctx = (config.Contexts ?? []).FirstOrDefault(c => c.Name == context)
            ?? throw new FormatException($"Context '{context}' isn't in this kubeconfig.");
        var cluster = (config.Clusters ?? []).FirstOrDefault(c => c.Name == ctx.ContextDetails?.Cluster)
            ?? throw new FormatException($"Context '{context}' points at cluster '{ctx.ContextDetails?.Cluster}', which isn't defined.");
        var user = (config.Users ?? []).FirstOrDefault(u => u.Name == ctx.ContextDetails?.User)
            ?? throw new FormatException($"Context '{context}' points at user '{ctx.ContextDetails?.User}', which isn't defined.");

        var subset = new K8SConfiguration
        {
            ApiVersion = config.ApiVersion,
            Kind = config.Kind,
            CurrentContext = ctx.Name,
            Contexts = [ctx],
            Clusters = [cluster],
            Users = [user],
        };

        // Round-trip through YAML: a deep copy with no references back into the parsed file.
        return KubernetesYaml.Deserialize<K8SConfiguration>(KubernetesYaml.Serialize(subset));
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
