using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>The cluster-scoped checks every page relies on. Keys are part of the shared contract.</summary>
    private static readonly (string Key, string Resource, string? Subresource, string Verb)[] ClusterChecks =
    [
        ("nodes/patch", "nodes", null, "patch"),
        ("pods/eviction/create", "pods", "eviction", "create"),
        ("nodes/list", "nodes", null, "list"),
        ("pods/list-all", "pods", null, "list"),
    ];

    public async Task<K8sAccessFacts> GetAccessAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var client = Client(secret);
        IReadOnlyList<string> targets = namespaces.Count == 0 ? ["default"] : namespaces;

        var identityTask = GetIdentityAsync(client, secret, ct);
        var reviewTasks = targets.Select(ns => RulesReviewAsync(client, ns, ct)).ToList();
        var checkTasks = ClusterChecks.Select(c => AccessCheckAsync(client, c.Resource, c.Subresource, c.Verb, ct)).ToList();

        var identity = await identityTask;
        var reviews = await Task.WhenAll(reviewTasks);
        var checks = await Task.WhenAll(checkTasks);

        return new K8sAccessFacts(
            identity,
            reviews,
            ClusterChecks.Select((c, i) => (c.Key, Allowed: checks[i])).ToDictionary(x => x.Key, x => x.Allowed, StringComparer.Ordinal));
    }

    /// <summary>Who the API server says we are; falls back to the kubeconfig user name when SelfSubjectReview isn't available.</summary>
    private static async Task<string> GetIdentityAsync(IKubernetes client, string secret, CancellationToken ct)
    {
        try
        {
            var review = await client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: ct);
            var name = review.Status?.UserInfo?.Username;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
        {
            // Older cluster, or the review is forbidden: use the kubeconfig's own name for the user.
        }

        return KubeconfigSettings.Parse(secret).Summary.User;
    }

    private static async Task<K8sRulesReview> RulesReviewAsync(IKubernetes client, string ns, CancellationToken ct)
    {
        var review = await client.AuthorizationV1.CreateSelfSubjectRulesReviewAsync(
            new V1SelfSubjectRulesReview { Spec = new V1SelfSubjectRulesReviewSpec { NamespaceProperty = ns } },
            cancellationToken: ct);
        return K8sMappers.ToRulesReview(ns, review.Status);
    }

    private static async Task<bool> AccessCheckAsync(IKubernetes client, string resource, string? subresource, string verb, CancellationToken ct)
    {
        var review = await client.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(
            new V1SelfSubjectAccessReview
            {
                Spec = new V1SelfSubjectAccessReviewSpec
                {
                    ResourceAttributes = new V1ResourceAttributes { Resource = resource, Subresource = subresource, Verb = verb },
                },
            },
            cancellationToken: ct);
        return review.Status?.Allowed ?? false;
    }
}
