using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    private const string TlsSecretType = "kubernetes.io/tls";
    private const string TlsCertKey = "tls.crt";

    /// <summary>
    /// ConfigMaps in scope plus every reference to them from the pods in the same namespaces (spec §9.1). Cluster-wide
    /// first; when that is forbidden and namespaces are selected, one list per namespace (shared fan-out gate).
    /// </summary>
    public async Task<K8sConfigData> ListConfigMapsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var client = Client(secret);
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
        bool InScope(string? ns) => scope is null || (ns is not null && scope.Contains(ns));

        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var mapsTask = ListScopedAsync(namespaces,
            async () => (await client.CoreV1.ListConfigMapForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var podsTask = ListScopedAsync(namespaces,
            async () => (await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct)).Items, gate, ct);
        await Task.WhenAll(mapsTask, podsTask);

        var maps = mapsTask.Result
            .Where(m => InScope(m.Metadata.NamespaceProperty))
            .Select(K8sMappers.ConfigMap)
            .OrderBy(m => m.Namespace, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var refs = podsTask.Result
            .Where(p => InScope(p.Metadata.NamespaceProperty))
            .SelectMany(K8sMappers.ConfigRefs)
            .Distinct()
            .ToList();
        return new K8sConfigData(maps, refs);
    }

    /// <summary>
    /// Replaces the map's <c>data</c> (binaryData untouched) under the caller's <paramref name="resourceVersion"/>: if
    /// anyone changed it since, the API server answers 409, which FriendlyKubernetesError words as "It changed since you
    /// loaded it — reload and try again."
    /// </summary>
    public async Task UpdateConfigMapAsync(string secret, string ns, string name, IReadOnlyDictionary<string, string> data,
        string resourceVersion, CancellationToken ct = default)
    {
        var client = Client(secret);
        var map = await client.CoreV1.ReadNamespacedConfigMapAsync(name, ns, cancellationToken: ct);
        map.Data = new Dictionary<string, string>(data, StringComparer.Ordinal);
        map.Metadata.ResourceVersion = resourceVersion;
        await client.CoreV1.ReplaceNamespacedConfigMapAsync(map, name, ns, cancellationToken: ct);
    }

    /// <summary>
    /// Spec §9.2 — the plugin's only Secret-touching code. Secrets are listed (never read one by one) and reduced to
    /// <see cref="SecretSummary"/> right here: key names, type, age and, for TLS secrets, the certificate's expiry. No
    /// <see cref="V1Secret"/> or byte of its data leaves this method; nothing is cached or logged. HTTP failures (403, 401…)
    /// propagate unchanged so they read friendly; any other failure is replaced by a new exception with no inner one, so
    /// no Secret content can ride along in an exception message.
    /// </summary>
    public async Task<IReadOnlyList<SecretSummary>> ListSecretsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        try
        {
            var client = Client(secret);
            var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
            using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
            IReadOnlyList<V1Secret>? secrets = await ListScopedAsync(namespaces,
                async () => (await client.CoreV1.ListSecretForAllNamespacesAsync(cancellationToken: ct)).Items,
                async ns => (await client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct)).Items, gate, ct);

            var summaries = secrets
                .Where(s => scope is null || (s.Metadata.NamespaceProperty is { } ns && scope.Contains(ns)))
                .Select(Summarize)
                .OrderBy(s => s.Namespace, StringComparer.Ordinal)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            secrets = null;
            return summaries;
        }
        catch (HttpOperationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Listing secrets failed.");
        }
    }

    private static SecretSummary Summarize(V1Secret s)
    {
        var data = s.Data;
        var type = s.Type ?? "Opaque";
        DateTimeOffset? notAfter = null;
        if (type == TlsSecretType && data is not null && data.TryGetValue(TlsCertKey, out var crt) && crt is not null)
        {
            notAfter = CertificateNotAfter(crt);
        }

        return new SecretSummary(
            s.Metadata.NamespaceProperty ?? string.Empty,
            s.Metadata.Name,
            type,
            (data?.Keys ?? []).Order(StringComparer.Ordinal).ToList(),
            s.Metadata.CreationTimestamp is { } created
                ? new DateTimeOffset(DateTime.SpecifyKind(created.ToUniversalTime(), DateTimeKind.Utc))
                : DateTimeOffset.MinValue,
            notAfter);
    }

    /// <summary>The leaf certificate's NotAfter (UTC) from PEM, else DER; null when it doesn't parse. Never logged.</summary>
    private static DateTimeOffset? CertificateNotAfter(byte[] crt)
    {
        try
        {
            var text = Encoding.UTF8.GetString(crt);
            using var cert = text.Contains("-----BEGIN", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(crt);
            return new DateTimeOffset(DateTime.SpecifyKind(cert.NotAfter.ToUniversalTime(), DateTimeKind.Utc));
        }
        catch (Exception)
        {
            // Unparseable certificate: no expiry. The exception may quote the content, so it is dropped, not logged.
            return null;
        }
    }
}
