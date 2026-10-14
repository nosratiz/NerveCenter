using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>Pure V1* → plugin record mapping. No API calls.</summary>
internal static class K8sMappers
{
    public static K8sRule ToRule(V1ResourceRule rule) => new(
        (IReadOnlyList<string>?)rule.ApiGroups?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.Resources?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.Verbs?.ToList() ?? [],
        (IReadOnlyList<string>?)rule.ResourceNames?.ToList() ?? []);

    public static K8sRulesReview ToRulesReview(string ns, V1SubjectRulesReviewStatus? status) => new(
        ns,
        status?.Incomplete ?? true,
        (status?.ResourceRules ?? []).Select(ToRule).ToList());
}
