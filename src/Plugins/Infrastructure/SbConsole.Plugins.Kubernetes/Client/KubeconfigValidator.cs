// Allowed exception to the "only KubernetesOperations*/ClientFactory/K8sMappers touch k8s.*" rule: this file parses kubeconfig models to validate them.
using System.Text;
using k8s;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record KubeconfigSummary(string Server, string Host, string Context, string User, bool TlsVerified, string CredentialKind /*token|client-certificate|basic*/);

public sealed record KubeconfigValidation(
    IReadOnlyList<string> Contexts, string? CurrentContext, IReadOnlyList<string> Errors, KubeconfigSummary? Summary);

/// <summary>
/// Accepts only kubeconfigs that carry everything inline and a single static credential: the console
/// never runs programs (exec / auth-provider) or reads files on the SbConsole server on a user's behalf.
/// Every user and cluster in the file is checked, not only the selected ones, and names must be unique ignoring case:
/// KubernetesClient resolves context/cluster/user names case-insensitively (first match), so a case variant could
/// otherwise swap an unchecked entry in for the checked one.
/// </summary>
public static class KubeconfigValidator
{
    public const int MaxBytes = 256 * 1024;

    private const string ExecSentence =
        "This user signs in by running a program ({0}). SbConsole only accepts static credentials — use a ServiceAccount token (see docker/README.md › Kubernetes).";

    public static KubeconfigValidation Validate(string kubeconfig, string? context)
    {
        var bytes = Encoding.UTF8.GetBytes(kubeconfig ?? string.Empty);
        if (bytes.Length > MaxBytes)
        {
            return Fail("The kubeconfig is larger than 256 KB.");
        }

        k8s.KubeConfigModels.K8SConfiguration config;
        try
        {
            config = KubernetesClientConfiguration.LoadKubeConfig(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            return Fail($"This isn't a valid kubeconfig: {FriendlyError.Truncate(ex.Message)}");
        }

        var contexts = (config?.Contexts ?? []).Select(c => c.Name).ToList();
        var current = string.IsNullOrEmpty(config?.CurrentContext) ? null : config!.CurrentContext;
        if (contexts.Count == 0)
        {
            return new KubeconfigValidation(contexts, current, ["The kubeconfig has no contexts."], null);
        }

        var duplicates = DuplicateNameErrors("context", config!.Contexts.Select(c => c.Name))
            .Concat(DuplicateNameErrors("cluster", (config.Clusters ?? []).Select(c => c.Name)))
            .Concat(DuplicateNameErrors("user", (config.Users ?? []).Select(u => u.Name)))
            .ToList();
        if (duplicates.Count > 0)
        {
            return new KubeconfigValidation(contexts, current, duplicates, null);
        }

        var errors = new List<string>();
        foreach (var c in config.Clusters ?? [])
        {
            if (!string.IsNullOrEmpty(c.ClusterEndpoint?.CertificateAuthority))
            {
                errors.Add($"Cluster '{c.Name}': certificate-authority points at a file on the SbConsole server; paste it as certificate-authority-data instead.");
            }
        }

        var rejectedUsers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in config.Users ?? [])
        {
            var cred = u.UserCredentials;
            var count = errors.Count;
            if (cred?.ExternalExecution is not null)
            {
                errors.Add($"User '{u.Name}': " + string.Format(ExecSentence, "exec"));
            }

            if (cred?.AuthProvider is not null)
            {
                errors.Add($"User '{u.Name}': " + string.Format(ExecSentence, "auth-provider"));
            }

            if (!string.IsNullOrEmpty(cred?.ClientCertificate) || !string.IsNullOrEmpty(cred?.ClientKey))
            {
                errors.Add($"User '{u.Name}': client-certificate / client-key point at files; use client-certificate-data / client-key-data.");
            }

            if (errors.Count > count)
            {
                rejectedUsers.Add(u.Name);
            }
        }

        var name = string.IsNullOrEmpty(context) ? current ?? contexts[0] : context;
        var ctx = config.Contexts.FirstOrDefault(c => c.Name == name);
        if (ctx is null)
        {
            errors.Insert(0, $"Context '{name}' isn't in this kubeconfig.");
            return new KubeconfigValidation(contexts, current, errors, null);
        }

        var cluster = (config.Clusters ?? []).FirstOrDefault(c => c.Name == ctx.ContextDetails?.Cluster);
        var user = (config.Users ?? []).FirstOrDefault(u => u.Name == ctx.ContextDetails?.User);
        if (cluster is null)
        {
            errors.Add($"Context '{name}' points at cluster '{ctx.ContextDetails?.Cluster}', which isn't defined.");
        }

        if (user is null)
        {
            errors.Add($"Context '{name}' points at user '{ctx.ContextDetails?.User}', which isn't defined.");
        }

        string? server = null;
        Uri? uri = null;
        if (cluster is not null)
        {
            server = cluster.ClusterEndpoint?.Server;
            if (!Uri.TryCreate(server, UriKind.Absolute, out uri) || !IsAllowedScheme(uri))
            {
                uri = null;
                errors.Add("The cluster server must be an https:// URL.");
            }
        }

        string? credentialKind = null;
        if (user is not null)
        {
            var cred = user.UserCredentials;
            var kinds = new List<string>();
            if (!string.IsNullOrEmpty(cred?.Token))
            {
                kinds.Add("token");
            }

            if (!string.IsNullOrEmpty(cred?.ClientCertificateData) && !string.IsNullOrEmpty(cred.ClientKeyData))
            {
                kinds.Add("client-certificate");
            }

            if (!string.IsNullOrEmpty(cred?.UserName) && !string.IsNullOrEmpty(cred.Password))
            {
                kinds.Add("basic");
            }

            // A program-based or file-based user is already rejected above; adding "no credential" on top is noise.
            if (kinds.Count == 0 && !rejectedUsers.Contains(user.Name))
            {
                errors.Add($"User '{user.Name}' has no static credential (token, client certificate data, or username/password).");
            }
            else if (kinds.Count > 1)
            {
                errors.Add($"User '{user.Name}' has more than one credential; keep exactly one.");
            }
            else if (kinds.Count == 1)
            {
                credentialKind = kinds[0];
            }
        }

        if (errors.Count > 0 || uri is null || credentialKind is null)
        {
            return new KubeconfigValidation(contexts, current, errors, null);
        }

        var summary = new KubeconfigSummary(
            server!, uri.Host, name, user!.Name, !cluster!.ClusterEndpoint!.SkipTlsVerify, credentialKind);
        return new KubeconfigValidation(contexts, current, errors, summary);
    }

    private static IEnumerable<string> DuplicateNameErrors(string kind, IEnumerable<string?> names)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var key = n ?? string.Empty;
            if (seen.TryGetValue(key, out var first))
            {
                yield return $"Names in a kubeconfig must be unique ignoring case: {kind} '{first}' and '{key}'.";
            }
            else
            {
                seen[key] = key;
            }
        }
    }

    private static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1");

    private static KubeconfigValidation Fail(string error) => new([], null, [error], null);
}
