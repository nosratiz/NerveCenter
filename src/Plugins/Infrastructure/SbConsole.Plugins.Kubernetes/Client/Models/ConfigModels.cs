namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sConfigMap(string Namespace, string Name, IReadOnlyDictionary<string, string> Data,
    IReadOnlyDictionary<string, long> BinarySizes, DateTimeOffset CreatedAt, string ResourceVersion);
public sealed record K8sConfigRef(string Namespace, string ConfigMap, string User /*workload or pod name*/, string Via /*env|volume|projected*/);
public sealed record K8sConfigData(IReadOnlyList<K8sConfigMap> ConfigMaps, IReadOnlyList<K8sConfigRef> Refs);
/// <summary>Everything the console ever learns about a Secret. Deliberately has no member that can hold a value.</summary>
public sealed record SecretSummary(string Namespace, string Name, string Type, IReadOnlyList<string> KeyNames,
    DateTimeOffset CreatedAt, DateTimeOffset? TlsNotAfter);
