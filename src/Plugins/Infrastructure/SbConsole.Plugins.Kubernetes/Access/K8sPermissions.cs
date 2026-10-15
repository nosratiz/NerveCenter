using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Access;

/// <summary>
/// Pure evaluation of SelfSubjectRulesReview results. Pages use it to hide controls the account cannot
/// use; the server's 403 remains the real backstop, so when probing fails <see cref="All"/> renders everything.
/// </summary>
public sealed class K8sPermissions
{
    private static readonly string[] WriteVerbs = ["create", "update", "patch", "delete", "deletecollection"];

    public static readonly (string Group, string Resource, string Verb)[] WorkloadActions =
    [
        ("apps", "deployments/scale", "patch"), ("apps", "deployments", "patch"), ("apps", "deployments", "delete"),
        ("apps", "statefulsets/scale", "patch"), ("apps", "statefulsets", "patch"), ("apps", "statefulsets", "delete"),
        ("apps", "daemonsets", "patch"), ("apps", "daemonsets", "delete"),
        ("batch", "jobs", "delete"), ("batch", "cronjobs", "delete"),
    ];

    public static readonly (string Group, string Resource, string Verb)[] PodActions = [("", "pods", "delete")];
    public static readonly (string Group, string Resource, string Verb)[] ConfigActions = [("", "configmaps", "update")];

    /// <summary>ClusterChecks key: may this account cordon / uncordon nodes.</summary>
    public const string NodesPatch = "nodes/patch";

    /// <summary>ClusterChecks key: may this account evict pods in every namespace (drain).</summary>
    public const string PodsEvictionCreate = "pods/eviction/create";

    /// <summary>The Nodes page's cluster-scoped writes: cordon / uncordon and drain.</summary>
    public static readonly string[] NodeClusterActions = [NodesPatch, PodsEvictionCreate];

    private readonly IReadOnlyList<K8sRulesReview> _reviews;
    private readonly IReadOnlyDictionary<string, bool> _cluster;
    private readonly bool _all;

    private K8sPermissions(string identity, bool incomplete, IReadOnlyList<K8sRulesReview> reviews,
        IReadOnlyDictionary<string, bool> cluster, bool all)
    {
        Identity = identity;
        Incomplete = incomplete;
        _reviews = reviews;
        _cluster = cluster;
        _all = all;
    }

    public static K8sPermissions All { get; } =
        new("unknown", false, [], new Dictionary<string, bool>(), all: true);

    public static K8sPermissions From(K8sAccessFacts facts) =>
        new(facts.Identity, facts.Reviews.Any(r => r.Incomplete), facts.Reviews, facts.ClusterChecks, all: false);

    public string Identity { get; }

    /// <summary>True when any namespace's rule review was incomplete (the cluster's authorizer could not enumerate everything).</summary>
    public bool Incomplete { get; }

    // system:basic-user grants every account create on the self-review APIs; they say nothing about the cluster's data.
    private static readonly string[] SelfReviewGroups = ["authorization.k8s.io", "authentication.k8s.io"];

    public bool IsReadOnly =>
        !_all && !_reviews.SelectMany(r => r.Rules)
            .Where(rule => !(rule.ApiGroups.Count > 0 && rule.ApiGroups.All(g => SelfReviewGroups.Contains(g))))
            .Any(rule => rule.Verbs.Any(v => v == "*" || WriteVerbs.Contains(v)));

    public bool CanCluster(string check) => _all || (_cluster.TryGetValue(check, out var allowed) && allowed);

    public bool Can(string apiGroup, string resource, string verb, string? ns = null)
    {
        if (_all)
        {
            return true;
        }

        if (ns is null)
        {
            return _reviews.Count > 0 && _reviews.All(r => Grants(r, apiGroup, resource, verb));
        }

        var review = _reviews.FirstOrDefault(r => r.Namespace == ns);
        if (review is null && _reviews.Count == 1 && _reviews[0].Namespace == "default")
        {
            review = _reviews[0];
        }

        return review is not null && Grants(review, apiGroup, resource, verb);
    }

    public IReadOnlyList<string> Missing(IEnumerable<(string Group, string Resource, string Verb)> needs, string? ns = null) =>
        needs.Where(n => !Can(n.Group, n.Resource, n.Verb, ns))
            .Select(n => $"{n.Resource} · {n.Verb}")
            .Distinct()
            .ToList();

    /// <summary>The cluster checks this account fails, as "resource · verb" ("pods/eviction/create" → "pods/eviction · create").</summary>
    public IReadOnlyList<string> MissingCluster(IEnumerable<string> checks) =>
        checks.Where(c => !CanCluster(c))
            .Select(c => c.LastIndexOf('/') is var i and > 0 ? $"{c[..i]} · {c[(i + 1)..]}" : c)
            .Distinct()
            .ToList();

    private static bool Grants(K8sRulesReview review, string group, string resource, string verb) =>
        review.Rules.Any(rule =>
            rule.ResourceNames.Count == 0
            && rule.ApiGroups.Any(g => g == "*" || g == group)
            && rule.Resources.Any(r => ResourceMatches(r, resource))
            && rule.Verbs.Any(v => v == "*" || v == verb));

    private static bool ResourceMatches(string ruleResource, string resource) =>
        ruleResource == "*" || ruleResource == resource
        || (ruleResource.EndsWith("/*", StringComparison.Ordinal)
            && resource.StartsWith(ruleResource[..^1], StringComparison.Ordinal));
}
