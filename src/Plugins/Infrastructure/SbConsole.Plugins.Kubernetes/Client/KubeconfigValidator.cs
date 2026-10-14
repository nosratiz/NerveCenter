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

        var name = string.IsNullOrEmpty(context) ? current ?? contexts[0] : context;
        var ctx = config!.Contexts.FirstOrDefault(c => c.Name == name);
        if (ctx is null)
        {
            return new KubeconfigValidation(contexts, current, [$"Context '{name}' isn't in this kubeconfig."], null);
        }

        var errors = new List<string>();
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
            var endpoint = cluster.ClusterEndpoint;
            server = endpoint?.Server;
            if (!Uri.TryCreate(server, UriKind.Absolute, out uri) || !IsAllowedScheme(uri))
            {
                uri = null;
                errors.Add("The cluster server must be an https:// URL.");
            }

            if (!string.IsNullOrEmpty(endpoint?.CertificateAuthority))
            {
                errors.Add("certificate-authority points at a file on the SbConsole server; paste it as certificate-authority-data instead.");
            }
        }

        string? credentialKind = null;
        if (user is not null)
        {
            var cred = user.UserCredentials;
            if (cred?.ExternalExecution is not null)
            {
                errors.Add(string.Format(ExecSentence, "exec"));
            }

            if (cred?.AuthProvider is not null)
            {
                errors.Add(string.Format(ExecSentence, "auth-provider"));
            }

            if (!string.IsNullOrEmpty(cred?.ClientCertificate) || !string.IsNullOrEmpty(cred?.ClientKey))
            {
                errors.Add("client-certificate / client-key point at files; use client-certificate-data / client-key-data.");
            }

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
            var alreadyRejected = errors.Count > 0 && (cred?.ExternalExecution is not null || cred?.AuthProvider is not null
                || !string.IsNullOrEmpty(cred?.ClientCertificate) || !string.IsNullOrEmpty(cred?.ClientKey));
            if (kinds.Count == 0 && !alreadyRejected)
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
            server!, uri.Host, name, user!.Name, !cluster!.ClusterEndpoint.SkipTlsVerify, credentialKind);
        return new KubeconfigValidation(contexts, current, errors, summary);
    }

    private static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1");

    private static KubeconfigValidation Fail(string error) => new([], null, [error], null);
}
