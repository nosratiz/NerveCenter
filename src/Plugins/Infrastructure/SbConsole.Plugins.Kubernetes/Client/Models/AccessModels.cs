namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sRule(IReadOnlyList<string> ApiGroups, IReadOnlyList<string> Resources, IReadOnlyList<string> Verbs, IReadOnlyList<string> ResourceNames);
public sealed record K8sRulesReview(string Namespace, bool Incomplete, IReadOnlyList<K8sRule> Rules);
public sealed record K8sAccessFacts(string Identity, IReadOnlyList<K8sRulesReview> Reviews, IReadOnlyDictionary<string, bool> ClusterChecks);
// ClusterChecks keys (exact): "nodes/patch", "pods/eviction/create", "nodes/list", "pods/list-all"
