using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Overview;

/// <summary>The last overview that loaded, when, and for which namespace selection (empty = all).</summary>
public sealed record OverviewSnapshot(K8sOverview Overview, DateTimeOffset LoadedAt, IReadOnlyList<string> Namespaces);

public sealed record OverviewState(OverviewSnapshot? LastGood, DateTimeOffset? LastFailureAt, string? LastError);
