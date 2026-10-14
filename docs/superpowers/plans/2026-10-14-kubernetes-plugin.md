# Kubernetes Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `SbConsole.Plugins.Kubernetes` — Overview, Workloads, Deployment detail, Pod detail, Nodes
(with drain), Config, Events, RBAC gating — per `docs/superpowers/specs/2026-10-14-kubernetes-plugin-design.md`
(the spec) and mockup `/Users/nimanosrati/Desktop/UI mockups for NerveCenter/SbConsole Kubernetes.dc.html`
(frames 3a–3i).

**Architecture:** One plugin project referencing `SbConsole.Sdk` + `KubernetesClient`. A single
`IKubernetesOperations` wrapper owns every API call and maps `k8s.Models.*` into the plugin's own records
(pure mappers in `K8sMappers`, unit-tested without a cluster). Handlers (`IScopedService`) wrap it with
secret lookup, audit and friendly errors (`HandlerRunner` / `PluginResult`, copied from Postgres). Razor
pages under `/p/kubernetes/*` inject handlers. Pure decision logic (attention, rollout history, drain plan,
permissions, config-map usage, event grouping, exit codes) lives in static classes with their own tests.

**Tech Stack:** .NET 10, KubernetesClient 21.0.8, MudBlazor 9.9.0, xUnit 2.9.3, FluentAssertions 7.2.0,
NSubstitute 6.2.0, bUnit 2.10.3, Testcontainers.K3s 4.16.0, Microsoft.Extensions.TimeProvider.Testing 10.10.0.

## Global Constraints

- `TreatWarningsAsErrors=true`; gate before every commit: `dotnet build -warnaserror` and
  `dotnet test tests/SbConsole.Plugins.Kubernetes.Tests` green; full `dotnet test` at the end of each phase.
- TDD: failing test first for every behaviour. Test names `Snake_case_sentences`. bUnit via
  `BunitContext` + `Render<T>` (the old `TestContext`/`RenderComponent` names are obsolete → build error).
- Plugin references `SbConsole.Sdk` only (plus NuGet). Never the DbContext. Persistence only through the
  keyed `IPluginStore` (`[FromKeyedServices(KubernetesPlugin.PluginId)]` in handlers,
  `[Inject(Key = KubernetesPlugin.PluginId)]` in components).
- DI by marker: handlers `IScopedService`; `KubernetesOperations`, `KubernetesClientFactory`, `AccessCache`
  `ISingletonService`; `ConfigureServices` calls `services.AddMarkedServices(typeof(KubernetesPlugin).Assembly)`.
- Only `KubernetesOperations*.cs` and `KubernetesClientFactory.cs` may `using k8s;` for API calls.
  `K8sMappers.cs` may use `k8s.Models` types (pure mapping). No other file references `k8s.*`.
- **No `V1Secret` (or any Secret `Data`/`StringData`) leaves `KubernetesOperations.Config.cs`**, is cached,
  logged, or put in an exception message. Env values from `secretKeyRef`/`secretRef` are never fetched.
- Raw exception text reaches the UI or audit rows only through `FriendlyKubernetesError` (which caps via
  `SbConsole.Sdk.FriendlyError.Truncate`); full exceptions are logged server-side.
- Destructive actions go through `IConfirmationService.ConfirmAsync(ConfirmationRequest)` with
  `Destructive: true`; `IsProd` comes from the `ConnectionInfo` the picker loaded via
  `IConnectionProvider.ListAsync("kubernetes")` (server-side), never from the query string.
- Colours only via `--mud-palette-*` CSS variables; no hex in `kubernetes.css`. CSS class prefix `k8s-`.
- Plugin id / kind `kubernetes`; Monogram `K8`; DisplayName `Kubernetes`; Version `0.1.0`.
- Routes (exact): `/p/kubernetes/overview`, `/p/kubernetes/workloads`, `/p/kubernetes/workloads/deployments/{Namespace}/{Name}`,
  `/p/kubernetes/pods/{Namespace}/{Name}`, `/p/kubernetes/nodes`, `/p/kubernetes/config`, `/p/kubernetes/events`.
  Query-string keys: `connectionId`, `ns` (comma-separated), `tab`, `container`, `previous`.
- Audit actions (exact): `k8s.deployment.scale`, `k8s.statefulset.scale`, `k8s.deployment.restart`,
  `k8s.statefulset.restart`, `k8s.daemonset.restart`, `k8s.deployment.rollback`, `k8s.node.cordon`,
  `k8s.node.uncordon`, `k8s.configmap.update`, `k8s.pod.delete`, `k8s.deployment.delete`,
  `k8s.statefulset.delete`, `k8s.daemonset.delete`, `k8s.job.delete`, `k8s.cronjob.delete`, `k8s.node.drain`.
  Target format `"{connectionName}/{namespace}/{name}"` (cluster-scoped: `"{connectionName}/{name}"`).
- Thresholds (exact): refresh Overview & Events-live 5 s, Workloads/Nodes/Detail pages 10 s, Config none;
  access cache 5 min; overview warnings window 1 h; attention cap 10; list render cap 50 rows then "show all";
  log tails 100/500/2000 (default 500); log render throttle 250 ms; download logs 10,000 lines per container;
  drain eviction parallelism 5, PDB retry every 5 s, overall timeout 5 min, grace default 30 s;
  TLS expiry warning < 30 d, error < 7 d; kubeconfig max 256 KB; parallel per-namespace fetch max 8;
  ConfigMap values collapse when > 200 chars or multi-line.
- Out of scope — never render controls for: Exec, Port-forward, Add label, Edit taints.

---

## File map

```
src/Plugins/Infrastructure/SbConsole.Plugins.Kubernetes/
  SbConsole.Plugins.Kubernetes.csproj
  KubernetesPlugin.cs                 identity, nav, summary, test, dashboard hooks
  HandlerRunner.cs  PluginResult.cs   (Postgres pattern, FriendlyKubernetesError)
  CommandRunner.cs                    shared body of every audited command
  Client/
    KubeconfigSettings.cs  KubeconfigValidator.cs  KubernetesConnectionFields.razor
    KubernetesClientFactory.cs        IKubernetes per secret hash, built from KubeconfigSettings
    IKubernetesOperations.cs          the contract below
    KubernetesOperations.cs (+ .Access .Overview .Workloads .Deployments .Pods .Logs .Nodes .Drain .Config .Events partials)
    K8sMappers.cs                     V1* → plugin records (pure)
    Models/ CommonModels.cs OverviewModels.cs WorkloadModels.cs PodModels.cs NodeModels.cs ConfigModels.cs AccessModels.cs
    FriendlyKubernetesError.cs  K8sErrorInfo.cs  K8sFormat.cs  ExitCodeInterpreter.cs  YamlDescribe.cs
  Access/    K8sPermissions.cs  AccessCache.cs  GetPermissionsQueryHandler.cs
  Namespaces/ ListNamespacesQueryHandler.cs  NamespaceSelectionStore.cs
  Overview/  AttentionRules.cs  OverviewSnapshot.cs  OverviewSnapshotStore.cs  GetOverviewQueryHandler.cs
  Workloads/ ListWorkloadsQueryHandler.cs  CountWorkloadsQueryHandler.cs  GetScaleInfoQueryHandler.cs
             GetResourceYamlQueryHandler.cs  ScaleWorkloadCommandHandler.cs  RestartWorkloadCommandHandler.cs
             DeleteWorkloadCommandHandler.cs  WorkloadCommands.cs
  Deployments/ RolloutHistory.cs  GetDeploymentDetailQueryHandler.cs  RollbackDeploymentCommandHandler.cs
  Pods/      GetPodDetailQueryHandler.cs  ReadPodLogsQueryHandler.cs  FollowPodLogsQueryHandler.cs
             DownloadPodLogsQueryHandler.cs  DeletePodCommandHandler.cs
  Nodes/     ListNodesQueryHandler.cs  ListNodePodsQueryHandler.cs  SetNodeSchedulableCommandHandler.cs
             DrainPlanner.cs  PreviewDrainQueryHandler.cs  DrainNodeCommandHandler.cs
  Config/    ConfigMapUsage.cs  ListConfigMapsQueryHandler.cs  ListSecretsQueryHandler.cs  UpdateConfigMapCommandHandler.cs
  Events/    EventGrouping.cs  ListEventsQueryHandler.cs
  Components/ _Imports.razor K8sPageHeader.razor K8sConnectionPicker.razor K8sNamespacePicker.razor RbacChip.razor
              K8sStatTile.razor K8sErrorAlert.razor ReadyBar.razor PhaseChip.razor YamlView.razor YamlDialog.razor
              ScaleDialog.razor LogViewer.razor DrainDialog.razor ConfigMapEditDialog.razor K8sPoller.cs
  Pages/      _Imports.razor Overview.razor Workloads.razor DeploymentDetail.razor PodDetail.razor Nodes.razor Config.razor Events.razor
  wwwroot/    kubernetes.css kubernetes.js
tests/SbConsole.Plugins.Kubernetes.Tests/
  SbConsole.Plugins.Kubernetes.Tests.csproj  InMemoryPluginStore.cs  TestData.cs (record builders)
  Infrastructure/ DockerFactAttribute.cs (copy of Postgres's, namespace changed)  K3sFixture.cs
  Client/ Access/ Namespaces/ Overview/ Workloads/ Deployments/ Pods/ Nodes/ Config/ Events/  (unit + handler tests)
  Operations/  (Docker tests against k3s, one class per partial)
  Pages/ K8sPageTestBase.cs + one test class per page/component
  KubernetesPluginTests.cs
Modify: SbConsole.slnx, src/SbConsole.Web/SbConsole.Web.csproj, src/SbConsole.Web/Program.cs, Dockerfile,
        docker-compose.yml, docker/sqlite/demo-connections.json, docker/README.md, docs/design.md (§19), CLAUDE.md, README.md
Create: docker/kubernetes/tokens.csv, docker/kubernetes/seed/*.yaml, docker/kubernetes/seed.sh
```

## Shared contract — `IKubernetesOperations` and models

Every task reads this section. Names here are final; tasks must not rename them. All namespaced list
methods take `IReadOnlyList<string> namespaces`; **empty = all namespaces**. CPU is integer millicores,
memory is bytes. Times are `DateTimeOffset` (UTC). All records live in `SbConsole.Plugins.Kubernetes.Client`.

```csharp
public interface IKubernetesOperations
{
    // Connection & access (Task 4)
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<K8sNamespace>> ListNamespacesAsync(string secret, CancellationToken ct = default);
    Task<K8sAccessFacts> GetAccessAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Overview (Task 8)
    Task<K8sOverview> GetOverviewAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Workloads (Task 10)
    Task<IReadOnlyList<K8sWorkload>> ListWorkloadsAsync(string secret, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector, CancellationToken ct = default);
    Task<IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>> CountWorkloadsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);
    Task<K8sScaleInfo> GetScaleInfoAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default);
    Task<int> ScaleAsync(string secret, K8sWorkloadKind kind, string ns, string name, int replicas, CancellationToken ct = default); // returns previous replicas
    Task RestartAsync(string secret, K8sWorkloadKind kind, string ns, string name, DateTimeOffset at, CancellationToken ct = default);
    Task DeleteWorkloadAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default);
    Task<string?> GetYamlAsync(string secret, K8sResourceKind kind, string? ns, string name, CancellationToken ct = default); // null = not found; managedFields stripped

    // Deployment detail (Task 12)
    Task<K8sDeploymentDetail?> GetDeploymentDetailAsync(string secret, string ns, string name, CancellationToken ct = default);
    Task<long> RollbackDeploymentAsync(string secret, string ns, string name, long toRevision, CancellationToken ct = default); // returns from-revision

    // Pods (Tasks 13–14)
    Task<K8sPodDetail?> GetPodDetailAsync(string secret, string ns, string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ReadLogsAsync(string secret, string ns, string pod, K8sLogRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> FollowLogsAsync(string secret, string ns, string pod, K8sLogRequest request, CancellationToken ct = default);
    Task<byte[]> DownloadAllLogsAsync(string secret, string ns, string pod, CancellationToken ct = default);
    Task DeletePodAsync(string secret, string ns, string name, int? gracePeriodSeconds, CancellationToken ct = default);

    // Nodes (Tasks 15–16)
    Task<IReadOnlyList<K8sNodeInfo>> ListNodesAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<K8sPodSummary>> ListPodsOnNodeAsync(string secret, string node, CancellationToken ct = default);
    Task SetUnschedulableAsync(string secret, string node, bool unschedulable, CancellationToken ct = default);
    Task<K8sDrainInputs> GetDrainInputsAsync(string secret, string node, CancellationToken ct = default);
    Task<K8sEvictionOutcome> EvictPodAsync(string secret, string ns, string name, int gracePeriodSeconds, CancellationToken ct = default);

    // Config (Task 17)
    Task<K8sConfigData> ListConfigMapsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);
    Task UpdateConfigMapAsync(string secret, string ns, string name, IReadOnlyDictionary<string, string> data, string resourceVersion, CancellationToken ct = default);
    Task<IReadOnlyList<SecretSummary>> ListSecretsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Events (Task 18)
    Task<IReadOnlyList<K8sEvent>> ListEventsAsync(string secret, IReadOnlyList<string> namespaces, TimeSpan window, CancellationToken ct = default);
}
```

```csharp
// Models/CommonModels.cs
public enum K8sWorkloadKind { Deployment, StatefulSet, DaemonSet, Job, CronJob }
public enum K8sResourceKind { Deployment, StatefulSet, DaemonSet, Job, CronJob, Pod, Node, ConfigMap }
public sealed record K8sRef(string Kind, string? Namespace, string Name);
public sealed record K8sCondition(string Type, string Status, string? Reason, string? Message, DateTimeOffset? LastTransition);
public sealed record K8sResources(long? CpuRequestMilli, long? MemRequestBytes, long? CpuLimitMilli, long? MemLimitBytes);
public sealed record K8sEvent(
    string? Namespace, string Type /*Warning|Normal*/, string Reason, string Message, K8sRef Regarding,
    int Count, DateTimeOffset LastSeen, string? Reporter);
public sealed record K8sNamespace(string Name, int PodCount);

// Models/AccessModels.cs
public sealed record K8sRule(IReadOnlyList<string> ApiGroups, IReadOnlyList<string> Resources, IReadOnlyList<string> Verbs, IReadOnlyList<string> ResourceNames);
public sealed record K8sRulesReview(string Namespace, bool Incomplete, IReadOnlyList<K8sRule> Rules);
public sealed record K8sAccessFacts(string Identity, IReadOnlyList<K8sRulesReview> Reviews, IReadOnlyDictionary<string, bool> ClusterChecks);
// ClusterChecks keys (exact): "nodes/patch", "pods/eviction/create", "nodes/list", "pods/list-all"

// Models/PodModels.cs
public sealed record K8sPodSummary(
    string Namespace, string Name, string Phase, string Status /*display: Running|Pending|CrashLoopBackOff|Completed|Terminating|ImagePullBackOff|…*/,
    int Restarts, int ReadyContainers, int TotalContainers, string? NodeName, K8sRef? Owner /*top-level: Deployment via RS hash, else direct owner*/,
    string? ReplicaSet, string? LastTerminationReason, int? LastExitCode, DateTimeOffset? LastTerminatedAt,
    string? UnschedulableMessage, DateTimeOffset CreatedAt, long CpuRequestMilli, long MemRequestBytes,
    bool HasEmptyDir, IReadOnlyDictionary<string, string> Labels, bool IsDaemonSetPod, bool IsMirrorPod);
public sealed record K8sTermination(int ExitCode, string? Reason, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public sealed record K8sContainer(
    string Name, bool IsInit, string Image, string State /*Running|Waiting|Terminated*/, string? StateReason,
    K8sTermination? CurrentTermination, K8sTermination? LastTermination, int Restarts, bool Ready,
    IReadOnlyList<string> Ports /*"8080/http"*/, K8sResources Resources);
public enum K8sEnvSource { Literal, ConfigMap, Secret, FieldRef, ResourceFieldRef, Unresolved }
public sealed record K8sEnvVar(string Container, string Name, string? Value /*always null for Secret*/, K8sEnvSource Source, string? SourceName);
public sealed record K8sOwnerLink(string Kind, string Name, long? Revision);
public sealed record K8sNodeHealth(string Name, string ReadyStatus, IReadOnlyList<string> Pressures, double? MemoryRequestFraction);
public sealed record K8sPodDetail(
    K8sPodSummary Pod, string? PodIp, string? HostIp, string QosClass, string? ServiceAccount,
    int VolumeCount, int SecretMountCount, IReadOnlyList<K8sOwnerLink> OwnerChain, K8sNodeHealth? Node,
    IReadOnlyList<K8sContainer> Containers, IReadOnlyList<K8sEnvVar> Env, IReadOnlyList<K8sEvent> Events,
    string DescribeYaml);
public sealed record K8sLogRequest(string Container, bool Previous, int TailLines, bool Timestamps);

// Models/WorkloadModels.cs
public sealed record K8sWorkload(
    K8sWorkloadKind Kind, string Namespace, string Name, int Desired, int Ready, int UpToDate, int Available,
    IReadOnlyList<string> Images, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<K8sCondition> Conditions,
    // Job / CronJob only (null/0 otherwise):
    int? Completions, int Succeeded, int Failed, string? JobStatus /*Complete|Failed|Running|Suspended*/,
    string? FailureReason, TimeSpan? Duration, string? Schedule, bool Suspended, DateTimeOffset? LastSchedule, int Active)
{
    public bool IsUnhealthy => Kind switch
    {
        K8sWorkloadKind.Job => JobStatus == "Failed",
        K8sWorkloadKind.CronJob => false,
        _ => Desired > 0 && (Available < Desired
             || Conditions.Any(c => c.Type == "Progressing" && c.Status == "False")),
    };
}
public sealed record K8sKindCount(int Count, bool HasFailure);
public sealed record K8sScaleInfo(int Replicas, int Ready, bool HpaManaged, string? HpaName, long PerPodCpuMilli, long PerPodMemBytes);
public sealed record K8sReplicaSet(string Name, long Revision, IReadOnlyList<string> Images, int Desired, int Ready,
    DateTimeOffset CreatedAt, string? ChangeCause, bool IsCurrent, bool HasTemplate);
public sealed record K8sServiceRef(string Name, int ReadyEndpoints);
public sealed record K8sDeploymentDetail(
    K8sWorkload Deployment, string Strategy, string? MaxSurge, string? MaxUnavailable, string Selector,
    int? ProgressDeadlineSeconds, int RevisionHistoryLimit, K8sResources PodResources, long CurrentRevision,
    IReadOnlyList<K8sReplicaSet> ReplicaSets, IReadOnlyList<K8sPodSummary> Pods, IReadOnlyList<K8sServiceRef> Services,
    IReadOnlyList<K8sEvent> Events, bool HpaManaged);

// Models/NodeModels.cs
public sealed record K8sNodeInfo(
    string Name, string? Pool, string? InstanceType, string? InternalIp, string ReadyStatus /*True|False|Unknown*/,
    DateTimeOffset? ReadySince, IReadOnlyList<string> Pressures, IReadOnlyList<string> Roles, IReadOnlyList<string> Taints,
    string KubeletVersion, bool Unschedulable, long CpuAllocatableMilli, long MemAllocatableBytes,
    long CpuRequestedMilli, long MemRequestedBytes, int PodCount, DateTimeOffset CreatedAt, string? ProviderId,
    IReadOnlyDictionary<string, string> Labels);
public sealed record K8sPdb(string Namespace, string Name, IReadOnlyDictionary<string, string> Selector,
    int DisruptionsAllowed, string Rule /*"minAvailable 3" | "maxUnavailable 0"*/, int CurrentHealthy, int DesiredHealthy);
public sealed record K8sDrainInputs(K8sNodeInfo Node, IReadOnlyList<K8sPodSummary> Pods, IReadOnlyList<K8sPdb> Pdbs,
    IReadOnlyDictionary<string, int> OwnerReplicas /*key "Kind/ns/name"*/);
public enum K8sEvictionOutcome { Evicted, BlockedByPdb, NotFound }

// Models/OverviewModels.cs
public sealed record K8sClusterInfo(string ServerUrl, string Context, string ServerVersion, string Provider,
    string Identity, int NamespaceCount);
public sealed record K8sPodCounts(int Running, int Pending, int Failed, int Succeeded);
public sealed record K8sOverview(
    DateTimeOffset CapturedAt, K8sClusterInfo Cluster, IReadOnlyList<K8sNodeInfo> Nodes, K8sPodCounts Pods,
    IReadOnlyList<K8sPodSummary> ProblemPods /*CrashLoopBackOff, Pending-unschedulable, Failed*/,
    IReadOnlyList<K8sWorkload> ProblemWorkloads /*unhealthy Deployments + failed Jobs*/,
    IReadOnlyList<K8sEvent> RecentWarnings /*last 1 h, newest first, max 50*/, int WarningCount,
    long CpuLimitsMilli, long MemLimitsBytes, double? CpuUsageFraction, double? MemUsageFraction);

// Models/ConfigModels.cs
public sealed record K8sConfigMap(string Namespace, string Name, IReadOnlyDictionary<string, string> Data,
    IReadOnlyDictionary<string, long> BinarySizes, DateTimeOffset CreatedAt, string ResourceVersion);
public sealed record K8sConfigRef(string Namespace, string ConfigMap, string User /*workload or pod name*/, string Via /*env|volume|projected*/);
public sealed record K8sConfigData(IReadOnlyList<K8sConfigMap> ConfigMaps, IReadOnlyList<K8sConfigRef> Refs);
/// <summary>Everything the console ever learns about a Secret. Deliberately has no member that can hold a value.</summary>
public sealed record SecretSummary(string Namespace, string Name, string Type, IReadOnlyList<string> KeyNames,
    DateTimeOffset CreatedAt, DateTimeOffset? TlsNotAfter);
```

Handler result types (shared by all tasks): `PluginResult` / `PluginResult<T>` (Task 3), command input records:

```csharp
// Workloads/WorkloadCommands.cs
public sealed record K8sTarget(Guid ConnectionId, string ConnectionName, string? Namespace, string Name)
{
    public string AuditTarget => Namespace is null ? $"{ConnectionName}/{Name}" : $"{ConnectionName}/{Namespace}/{Name}";
}
public sealed record ScaleCommand(K8sTarget Target, K8sWorkloadKind Kind, int Replicas);
public sealed record WorkloadCommand(K8sTarget Target, K8sWorkloadKind Kind);
```

---

# Phase 1 — Foundation

### Task 1: Project scaffold, kubeconfig settings + validator, plugin identity, contract

**Files:**
- Create: `src/Plugins/Infrastructure/SbConsole.Plugins.Kubernetes/SbConsole.Plugins.Kubernetes.csproj`,
  `KubernetesPlugin.cs`, `Client/KubeconfigSettings.cs`, `Client/KubeconfigValidator.cs`,
  `Client/IKubernetesOperations.cs` and every `Client/Models/*.cs` file **exactly as in the shared contract**,
  `Workloads/WorkloadCommands.cs`, `Components/_Imports.razor`, `Pages/_Imports.razor`,
  `wwwroot/kubernetes.css` (header comment only)
- Create: `tests/SbConsole.Plugins.Kubernetes.Tests/SbConsole.Plugins.Kubernetes.Tests.csproj`, `InMemoryPluginStore.cs`
  (copy of Postgres's, namespace `SbConsole.Plugins.Kubernetes.Tests`), `Client/KubeconfigValidatorTests.cs`,
  `Client/KubeconfigSettingsTests.cs`, `KubernetesPluginTests.cs`, `TestData.cs`
- Modify: `SbConsole.slnx` (new folder `/src/Plugins/Infrastructure/` with the plugin; test project in `/tests/`),
  `src/SbConsole.Web/SbConsole.Web.csproj` (ProjectReference `..\Plugins\Infrastructure\SbConsole.Plugins.Kubernetes\SbConsole.Plugins.Kubernetes.csproj`),
  `src/SbConsole.Web/Program.cs` (after the Postgres line: `builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Kubernetes.KubernetesPlugin>();`),
  `Dockerfile` (COPY line for the csproj beside the Postgres one)

**Interfaces — Produces:**
```csharp
public sealed record KubeconfigSettings(string Kubeconfig, string Context)
{
    public static KubeconfigSettings Parse(string secret);           // FormatException: bad JSON, missing fields, or validator errors (joined "; ")
    public string ToSecretString();                                  // {"kubeconfig":"…","context":"…"} (System.Text.Json, camelCase)
    public KubeconfigSummary Summary { get; }                        // computed once in Parse / ctor via validator
}
public sealed record KubeconfigSummary(string Server, string Host, string Context, string User, bool TlsVerified, string CredentialKind /*token|client-certificate|basic*/);
public sealed record KubeconfigValidation(IReadOnlyList<string> Contexts, string? CurrentContext, IReadOnlyList<string> Errors, KubeconfigSummary? Summary);
public static class KubeconfigValidator
{
    public const int MaxBytes = 256 * 1024;
    public static KubeconfigValidation Validate(string kubeconfig, string? context); // context null → current-context
}
public sealed class KubernetesPlugin : IPlugin
{
    public const string PluginId = "kubernetes", Kind = "kubernetes",
        OverviewHref = "/p/kubernetes/overview", WorkloadsHref = "/p/kubernetes/workloads",
        NodesHref = "/p/kubernetes/nodes", ConfigHref = "/p/kubernetes/config", EventsHref = "/p/kubernetes/events";
    public static string DeploymentHref(string ns, string name) => $"/p/kubernetes/workloads/deployments/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
    public static string PodHref(string ns, string name) => $"/p/kubernetes/pods/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(name)}";
}
```

csproj:
```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <ItemGroup><ProjectReference Include="..\..\..\SbConsole.Sdk\SbConsole.Sdk.csproj" /></ItemGroup>
  <ItemGroup><InternalsVisibleTo Include="SbConsole.Plugins.Kubernetes.Tests" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="KubernetesClient" Version="21.0.8" />
    <PackageReference Include="MudBlazor" Version="9.9.0" />
  </ItemGroup>
  <ItemGroup>
    <!-- Pages/*.razor need Razor component + [Authorize] support from the shared framework. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
```
Test csproj = Postgres test csproj with `Testcontainers.K3s` 4.16.0 instead of `Testcontainers.PostgreSql`, and
ProjectReference `..\..\src\Plugins\Infrastructure\SbConsole.Plugins.Kubernetes\SbConsole.Plugins.Kubernetes.csproj`.
`_Imports.razor` files mirror Postgres's with `SbConsole.Plugins.Kubernetes.Components` / `.Client`.

**Validator rules** (spec §3.2). Parse with `KubernetesClientConfiguration.LoadKubeConfig(new MemoryStream(utf8))`
inside try/catch (any exception → error "This isn't a valid kubeconfig: {FriendlyError.Truncate(ex.Message)}").
Errors (exact strings — tests assert them):
- byte length > MaxBytes → `"The kubeconfig is larger than 256 KB."`
- no contexts → `"The kubeconfig has no contexts."`
- context name not found → `"Context '{name}' isn't in this kubeconfig."`
- context's cluster / user entry missing → `"Context '{name}' points at cluster '{c}', which isn't defined."` / `… user '{u}' …`
- server not absolute, or scheme not https (http allowed only when host is `localhost` / `127.0.0.1`) → `"The cluster server must be an https:// URL."`
- `ClusterEndpoint.CertificateAuthority` set → `"certificate-authority points at a file on the SbConsole server; paste it as certificate-authority-data instead."`
- `UserCredentials.ExternalExecution` set → `"This user signs in by running a program (exec). SbConsole only accepts static credentials — use a ServiceAccount token (see docker/README.md › Kubernetes)."`
- `UserCredentials.AuthProvider` set → same sentence with `(auth-provider)` instead of `(exec)`
- `ClientCertificate` or `ClientKey` (paths) set → `"client-certificate / client-key point at files; use client-certificate-data / client-key-data."`
- credential count: token (non-empty `Token`), cert (`ClientCertificateData` + `ClientKeyData`), basic (`UserName` + `Password`); 0 → `"User '{u}' has no static credential (token, client certificate data, or username/password)."`; >1 → `"User '{u}' has more than one credential; keep exactly one."`
Summary: `Server` = server URL, `Host` = `Uri.Host`, `TlsVerified` = `!SkipTlsVerify`. Contexts = all context names in file order.

**Plugin identity:** `DisplayName "Kubernetes"`, `Monogram "K8"`, `Version "0.1.0"`, `ConnectionKindDisplayName "Kubernetes"`,
`ConnectionFormComponentType => null` until Task 2. NavItems Overview / Workloads / Nodes / Config / Events (in that order,
hrefs above). `Contribution => new(PageCount: 7, ActionCount: 9)`. `GetConnectionSummary`: `Server = Host`,
`Context = Context`, `TLS = verified|not verified`; FormatException → empty dictionary; never user/token.
`ConfigureServices` → `AddMarkedServices`. `Initialize` resolves `IKubernetesOperations` + `TimeProvider`;
`InitializeForTesting(IKubernetesOperations, TimeProvider?)` internal; `TestConnectionAsync` fails closed with
`new(false, "Kubernetes plugin is not initialized.")`. Dashboard hooks return `[]` until Task 8.

`TestData.cs`: static `Kubeconfig(string? user = null, string server = "https://k.example:6443", bool skipTls = false, string context = "dev")`
returning a YAML string with one cluster/context/user; the `user` parameter is a YAML fragment for the `user:` block
(default `token: abc`). Used by every validator test.

- [ ] **Step 1: Write failing tests.** `KubeconfigValidatorTests`: `Token_user_is_valid` (summary Host `k.example`, CredentialKind `token`, TlsVerified true),
  `Client_certificate_data_user_is_valid`, `Basic_auth_user_is_valid`, `Exec_user_is_rejected_with_the_exec_sentence`,
  `Auth_provider_user_is_rejected`, `Certificate_authority_path_is_rejected`, `Client_certificate_path_is_rejected`,
  `No_credential_is_rejected`, `Two_credentials_are_rejected`, `Unknown_context_is_rejected`, `Missing_cluster_entry_is_rejected`,
  `Http_server_is_rejected_except_localhost`, `Insecure_skip_tls_is_accepted_and_reported`, `Oversized_input_is_rejected`,
  `Garbage_yaml_is_rejected_without_throwing`, `Null_context_uses_current_context`, `Lists_all_contexts_in_file_order`.
  `KubeconfigSettingsTests`: `Round_trips_through_ToSecretString`, `Parse_rejects_bad_json`, `Parse_rejects_an_exec_kubeconfig`,
  `ToSecretString_does_not_reformat_the_yaml` (byte-identical kubeconfig).
  `KubernetesPluginTests`: identity values, nav hrefs in order, `DeploymentHref_escapes_segments`, summary has Server/Context/TLS and
  never the token text, summary empty on garbage, `TestConnection_fails_closed_before_initialize`.
- [ ] **Step 2:** `dotnet test tests/SbConsole.Plugins.Kubernetes.Tests` → compile failure / red.
- [ ] **Step 3:** Implement. Add projects to `SbConsole.slnx`, the Web reference, the Program.cs line, the Dockerfile COPY line.
- [ ] **Step 4:** Tests green; `dotnet build -warnaserror` green;
  `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ProgramDiRegistration|FullyQualifiedName~PluginRegistry"` green.
- [ ] **Step 5:** Commit `feat(kubernetes): plugin scaffold, kubeconfig validation, identity`.

### Task 2: Connection form

**Files:** Create `Client/KubernetesConnectionFields.razor`; test `Client/KubernetesConnectionFieldsTests.cs`.
Set `ConnectionFormComponentType => typeof(KubernetesConnectionFields)`.

Same contract as `PostgresConnectionFields.razor` (`InitialSecret` read once in `OnInitialized`, `SecretChanged`,
unused `IsProd`). Markup:
- `MudTextField` multi-line `Lines="12"`, class `k8s-conn-kubeconfig k8s-mono`, label "Kubeconfig", `Immediate="true"`,
  `DebounceInterval="300"`; helper text "Paste a kubeconfig with a static credential (ServiceAccount token, client certificate data, or username/password). exec and auth-provider sign-in are not supported."
- `MudSelect` class `k8s-conn-context`, label "Context", items = `Validation.Contexts`; on kubeconfig change the selection keeps the
  current value if still present, else the file's `current-context`, else the first.
- Errors: each `Validation.Errors` line in a `MudAlert Severity.Error Dense` with class `k8s-conn-error` (only after the kubeconfig field was touched).
- When valid: a caption `k8s-conn-summary`: "{Server} · user {User} · {CredentialKind}" plus a warning chip `k8s-conn-tls-off`
  "TLS not verified" when `!TlsVerified`.
- Emits `SecretChanged(new KubeconfigSettings(yaml, context).ToSecretString())` only when there are no errors.

- [ ] Tests first (bUnit, `BunitContext`, `Services.AddMudServices()`, `JSInterop.Mode = Loose`, render inside a `MudPopoverProvider`):
  `Fills_fields_from_the_initial_secret`, `Pasting_a_valid_kubeconfig_emits_a_secret_with_its_current_context`,
  `Pasting_an_exec_kubeconfig_shows_the_error_and_emits_nothing`, `Choosing_another_context_emits_it`,
  `Shows_tls_not_verified_chip`, `Unparsable_initial_secret_renders_an_empty_form`.
- [ ] Red → implement → green → commit `feat(kubernetes): connection form`.

### Task 3: Errors, formatting, exit codes, handler plumbing

**Files:** Create `Client/FriendlyKubernetesError.cs`, `Client/K8sErrorInfo.cs`, `Client/K8sFormat.cs`, `Client/ExitCodeInterpreter.cs`,
`HandlerRunner.cs`, `PluginResult.cs`, `CommandRunner.cs`; tests `Client/FriendlyKubernetesErrorTests.cs`, `Client/K8sFormatTests.cs`,
`Client/ExitCodeInterpreterTests.cs`, `CommandRunnerTests.cs`.

```csharp
public sealed record K8sErrorInfo(string Message, int? StatusCode, string? Reason, bool IsUnreachable);
public static class FriendlyKubernetesError
{
    public static string From(Exception ex) => Describe(ex).Message;
    public static K8sErrorInfo Describe(Exception ex);
}
public static class K8sFormat
{
    public static string Age(TimeSpan t);            // "45s" "6m" "7h" "41d" (floor; ≥ 2y → "2y")
    public static string Ago(TimeSpan t);            // "just now" (< 5 s) else Age + " ago"
    public static string Cpu(long milli);            // < 1000 → "500m"; else cores with ≤ 1 decimal, trailing .0 dropped: 1500 → "1.5", 58600 → "58.6"
    public static string Bytes(long b);              // binary units, "512 Mi", "1.5 Gi", "28 Gi", "119 Gi", "900 Ki", "12 B"
    public static string Percent(double fraction);   // 0.613 → "61%"
    public static string Images(IReadOnlyList<string> images); // " · "-joined
    public static long ParseCpuMilli(string? quantity);   // pure (no k8s types): "500m"→500, "2"→2000, "0.5"→500, null/""→0
    public static long ParseBytes(string? quantity);      // "512Mi", "1Gi", "128974848", "1G", "500M", "1e3"; null→0; unknown suffix → FormatException
}
public static class ExitCodeInterpreter
{
    public static string Explain(K8sTermination t, long? memoryLimitBytes);
}
```
`FriendlyKubernetesError.Describe` (spec §12) — exact messages:
- `k8s.Autorest.HttpOperationException` with `Response.StatusCode`: 401 → "The cluster rejected the credentials, or they have expired."; 403 →
  "Your account can't {verb} {resource}{ in ns}." parsed from the Status JSON `message` (`User "x" cannot patch resource "deployments/scale" in API group "apps" in the namespace "checkout"` → verb `patch`, resource `deployments/scale`, ns `checkout`); unparsable → "Your account isn't allowed to do that."; 404 → "It no longer exists — refresh."; 409 → "It changed since you loaded it — reload and try again."; 422 → "The cluster rejected the change: {Status message truncated}."; 429 → "Blocked by a PodDisruptionBudget — try again later."; ≥ 500 → "The API server returned an error ({code})."; StatusCode set on the info, `IsUnreachable` false.
- `HttpRequestException` whose inner is `AuthenticationException` → "The server's certificate isn't trusted — add its CA to the kubeconfig." (unreachable true)
- `HttpRequestException` with `SocketException` ConnectionRefused → "Can't reach the cluster — the connection was refused."; HostNotFound/TryAgain/NoData → "Can't reach the cluster — the host name didn't resolve."; else "Can't reach the cluster."
- `TaskCanceledException`/`TimeoutException` not caused by the caller → "Can't reach the cluster — it didn't answer in time."
- `FormatException` → "The saved connection settings are invalid: {truncated}"
- `k8s.Exceptions.KubeConfigException` → "The saved kubeconfig can't be used: {truncated}"
- default → `FriendlyError.From(ex)`.
The Status JSON is read from `HttpOperationException.Response.Content`; any parse failure falls back silently. Never include `Request` content.

`ExitCodeInterpreter.Explain` (exact): 137 + Reason OOMKilled → "Killed by the kernel at the memory limit ({Bytes(limit)})." (no limit → "Killed by the kernel: out of memory (OOMKilled)."); 137 otherwise → "Killed with SIGKILL (exit 137) — often the liveness probe or a forced stop."; 143 → "Stopped with SIGTERM (exit 143)."; 0 → "Completed (exit 0)."; 1, 2 → "The application exited with an error (exit {n}). Check the last log lines above."; 126 → "The command isn't executable (exit 126)."; 127 → "The command wasn't found (exit 127)."; else "Exited with code {n}." When `StartedAt` and `FinishedAt` are both set, append " Ran {Age(finished-started)}." 

`HandlerRunner` / `PluginResult`: copy Postgres's files, replacing `FriendlyPostgresError` with `FriendlyKubernetesError` and log text "Kubernetes query failed".

```csharp
internal static class CommandRunner
{
    /// Resolves the secret (not audited on failure: nothing ran), runs work, audits success with the detail work returns,
    /// audits failure with the friendly message, never throws except caller cancellation.
    public static async Task<PluginResult> RunAsync(IConnectionProvider connections, IAuditScope audit, ILogger logger,
        Guid connectionId, string action, string target, ActionRisk risk, Func<string, Task<string?>> work, CancellationToken ct);
    public static async Task<PluginResult<T>> RunAsync<T>(…, Func<string, Task<(T Value, string? Detail)>> work, CancellationToken ct);
}
```

- [ ] Tests first: friendly error per row above (build `HttpOperationException { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), statusJson) }`),
  403 parse with and without namespace, cluster-scoped 403 (`nodes "n1"` → "Your account can't patch nodes."), message never contains the
  request URL or bearer token (build a Request with an `Authorization` header and assert); `K8sFormat` table tests for every example above
  plus `ParseBytes("1.5Gi")`, `ParseCpuMilli("250m")`, bad suffix throws; exit-code table tests incl. the "Ran 41s." suffix;
  `CommandRunner`: missing connection → `HandlerRunner.ConnectionNotFound`, no audit; success audits `(action, target, risk, true, detail)`;
  failure audits `(…, false, friendly)` and returns the friendly error; caller cancellation rethrows; an audit write failure is logged, not thrown.
- [ ] Red → implement → green → commit `feat(kubernetes): friendly errors, formatting, command runner`.

### Task 4: k3s fixture, client factory, operations core (test, namespaces, access)

**Files:** Create `Client/KubernetesClientFactory.cs`, `Client/KubernetesOperations.cs`, `Client/KubernetesOperations.Access.cs`,
`Client/K8sMappers.cs` (start); tests `Infrastructure/DockerFactAttribute.cs` (copy), `Infrastructure/K3sFixture.cs`,
`Operations/AccessOperationsTests.cs`, `Client/KubernetesClientFactoryTests.cs`.

```csharp
public sealed class KubernetesClientFactory : ISingletonService, IDisposable
{
    // Keyed by SHA-256 of the secret (never the secret itself). Builds KubernetesClientConfiguration via
    // KubernetesClientConfiguration.BuildConfigFromConfigFile(stream, currentContext: settings.Context), sets HttpClientTimeout = 30 s,
    // creates new Kubernetes(config). Max 32 cached clients; evicts least-recently used and disposes it.
    public IKubernetes Get(string secret);   // throws FormatException from KubeconfigSettings.Parse
}
public sealed partial class KubernetesOperations(KubernetesClientFactory clients, TimeProvider clock, ILogger<KubernetesOperations> logger)
    : IKubernetesOperations, ISingletonService;
```
`KubernetesClientFactory` is registered by marker; `TimeProvider` is already registered by the host.
Operations not yet implemented throw `NotImplementedException` in this task (each later task replaces its own).

- `TestConnectionAsync`: `client.Version.GetCodeAsync()`; then identity via `client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview())`
  (`Status.UserInfo.Username`); on 404/403 fall back to `settings.Summary.User`. OK message `$"{version.GitVersion} · as {identity}"`
  (e.g. `v1.30.6+k3s1 · as system:admin`). Failure → `new(false, FriendlyKubernetesError.From(ex))`, full exception logged.
- `ListNamespacesAsync`: `ListNamespaceAsync()` names sorted ordinal + pod counts from `ListPodForAllNamespacesAsync()` grouped by namespace
  (if listing all pods is forbidden, PodCount 0 for all).
- `GetAccessAsync(namespaces)`: identity as above; for each namespace in `namespaces` (empty → `["default"]`) a
  `CreateSelfSubjectRulesReviewAsync(new V1SelfSubjectRulesReview { Spec = new() { NamespaceProperty = ns } })` → `K8sRulesReview`
  (resource rules only; `Incomplete` = `Status.Incomplete`); ClusterChecks via `CreateSelfSubjectAccessReviewAsync` for
  `nodes/patch` (resource nodes, verb patch), `pods/eviction/create` (resource pods, subresource eviction, verb create, no namespace),
  `nodes/list`, `pods/list-all` (resource pods, verb list, no namespace). Run reviews in parallel.

`K3sFixture` (collection `"k3s"`): when Docker is available start `new K3sBuilder("rancher/k3s:v1.30.6-k3s1").Build()`; `AdminSecret`
= `new KubeconfigSettings(await container.GetKubeconfigAsync(), "default").ToSecretString()` (read the context name from the returned
YAML's `current-context` with the validator instead of hard-coding "default"); `Admin` = a raw `Kubernetes` client for seeding;
`Operations` = `new KubernetesOperations(new KubernetesClientFactory(), TimeProvider.System, NullLogger<…>.Instance)`.
Helper `Task<string> ViewerSecretAsync()`: creates namespace `sbc-test`, ServiceAccount `viewer` there, ClusterRoleBinding `sbc-viewer` →
ClusterRole `view`, mints a token with `CreateNamespacedServiceAccountTokenAsync(new Authenticationv1TokenRequest { Spec = new() { ExpirationSeconds = 3600 } }, "viewer", "sbc-test")`
and returns a token kubeconfig secret (same server/CA as admin). Helper `Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)` polls every 500 ms.
Seeding helpers each test class uses: `ApplyAsync(V1Deployment)`, etc. — keep them in the fixture.

- [ ] Tests first (Docker): `Test_connection_reports_version_and_identity`, `Test_connection_with_a_bad_token_fails_with_the_401_sentence`,
  `Lists_namespaces_with_pod_counts` (kube-system has > 0), `Admin_access_allows_scale_and_node_patch`,
  `Viewer_access_has_list_but_not_patch` (rules review for `sbc-test` contains no `patch` on `deployments`; `nodes/patch` false),
  factory unit test: `Same_secret_returns_the_same_client`, `Different_secrets_return_different_clients`, `Bad_secret_throws_FormatException`.
- [ ] Wire `KubernetesPlugin.TestConnectionAsync` → `_operations.TestConnectionAsync`.
- [ ] Red → implement → green (Docker tests run locally; `SBC_SKIP_DOCKER_TESTS=1 dotnet test` skips them) → commit
  `feat(kubernetes): client factory, k3s fixture, connection test and access probes`.

### Task 5: Local k3s stack, seed, demo connections

**Files:** Create `docker/kubernetes/tokens.csv`, `docker/kubernetes/seed.sh`, `docker/kubernetes/seed/00-namespaces.yaml`,
`01-rbac.yaml`, `10-checkout.yaml`, `11-payments.yaml`, `12-platform.yaml`, `13-monitoring.yaml`, `20-config.yaml`,
`30-payment-gateway-v2.yaml` (applied after v1 is ready); modify `docker-compose.yml`, `docker/sqlite/demo-connections.json`,
`.env.example` (`K3S_PORT=6443`), `docker/README.md` (Kubernetes section).

- `tokens.csv` (checked in; local only, documented as such):
  ```
  sbc-demo-admin-token-local-only,sbconsole-admin,sbconsole-admin
  sbc-demo-viewer-token-local-only,sbconsole-viewer,sbconsole-viewer
  ```
- compose service `k3s`: `image: rancher/k3s:v1.30.6-k3s1`, `container_name: nervecenter-k3s`, `profiles: ["kubernetes"]`, `privileged: true`,
  `command: ["server", "--disable=traefik", "--tls-san=k3s", "--tls-san=localhost", "--kube-apiserver-arg=token-auth-file=/seed/tokens.csv"]`,
  `ports: ["${K3S_PORT:-6443}:6443"]`, volumes `./docker/kubernetes/tokens.csv:/seed/tokens.csv:ro`, `k3s-data:/var/lib/rancher/k3s`,
  `tmpfs: [/run, /var/run]`, healthcheck `kubectl get --raw=/readyz` (interval 5 s, retries 60).
- `k3s-seed`: `image: bitnami/kubectl:1.30`, profile kubernetes, `depends_on: k3s: condition: service_healthy`,
  mounts `./docker/kubernetes:/work:ro`, `entrypoint: ["sh", "/work/seed.sh"]`, env `KUBE_SERVER=https://k3s:6443`,
  `KUBE_TOKEN=sbc-demo-admin-token-local-only`. `seed.sh`: `set -eu`; build `K="kubectl --server=$KUBE_SERVER --token=$KUBE_TOKEN --insecure-skip-tls-verify"`;
  `$K apply -f /work/seed/0*.yaml -f /work/seed/1*.yaml -f /work/seed/2*.yaml`; `$K -n checkout rollout status deploy/payment-gateway --timeout=180s`;
  `$K apply -f /work/seed/30-payment-gateway-v2.yaml`; echo done. Idempotent (apply).
- Add volume `k3s-data`. The `app` service is unchanged (it already mounts the demo-connections file).
- Seed content (spec §13): namespaces checkout, payments, platform, monitoring, reporting; `01-rbac.yaml` ClusterRoleBindings
  `sbconsole-admin` (subject `kind: User, name: sbconsole-admin`, roleRef ClusterRole `cluster-admin`) and `sbconsole-viewer` (→ `view`);
  checkout: `payment-gateway` (v1: `nginx:1.27-alpine`, 2 replicas, `progressDeadlineSeconds: 60`, `strategy.rollingUpdate.maxUnavailable: 0`,
  `maxSurge: 25%`, annotation `kubernetes.io/change-cause: "initial"`, requests 50m/64Mi, limit memory 64Mi, labels app=payment-gateway,
  Service `payment-gateway`), `checkout-api` (3× nginx, ready), `cart` (2× nginx + `redis:7.2-alpine` sidecar), `pricing` (1×),
  ConfigMap `gateway-config` (REDIS_URL, MERCHANT_CACHE_SIZE, CACHE_TTL_SECONDS, FEATURE_FLAGS, PROVIDERS_JSON 1.8 KB JSON, logging.yaml 14 lines)
  used by payment-gateway as env + volume, `reports-config` (unused), Secrets `stripe-live` (Opaque, 2 keys), `checkout-tls`
  (`kubernetes.io/tls`, generated by `seed.sh` on every run so it always expires ~9 days out:
  `openssl req -x509 -newkey rsa:2048 -nodes -days 9 -subj /CN=checkout.local -keyout /tmp/tls.key -out /tmp/tls.crt` then
  `$K -n checkout create secret tls checkout-tls --cert /tmp/tls.crt --key /tmp/tls.key --dry-run=client -o yaml | $K apply -f -`;
  if the image lacks `openssl`, switch `k3s-seed` to `alpine/k8s:1.30.6`, which ships both kubectl and openssl),
  `ghcr-pull` (dockerconfigjson, dummy); HPA `checkout-api` (cpu 70 %, 3–6) to demo "HPA managed" in Scale.
  payments: `settlement-api`, `ledger` StatefulSet (3× nginx, headless Service) + PDB `ledger` `minAvailable: 3`, Job
  `settlement-20261014-0200` (`busybox:1.36`, `sh -c "echo settling; exit 1"`, `backoffLimit: 1`), CronJob `nightly-settlement` (`0 2 * * *`), `webhooks` Deployment scaled to 0.
  platform: DaemonSet `log-shipper` (busybox, `sh -c "echo starting; sleep 5; echo 'config error: missing SHIPPER_TOKEN'; exit 1"` → CrashLoopBackOff).
  monitoring: Deployment `prometheus` 1 replica requesting `memory: 999Gi` (unschedulable).
  reporting: Deployment `report-builder` mounting ConfigMap `reports-config-missing` (FailedMount events).
  `30-payment-gateway-v2.yaml`: same Deployment, image `busybox:1.36`, command `sh -c "echo gateway starting · version 2.14.1; head -c 400m /dev/zero | tail"`,
  annotation change-cause `"bump sdk"`, memory limit 64Mi → OOMKilled crash loop; readiness never true → rollout stuck → `ProgressDeadlineExceeded` after 60 s.
- `demo-connections.json`: append two entries, kind `kubernetes`, names `k3s-local` (tags `["prod"]`) and `k3s-local-viewer` (tags `["demo"]`);
  secret = `KubeconfigSettings` JSON whose kubeconfig has cluster `k3s` (`server: https://k3s:6443`, `insecure-skip-tls-verify: true`),
  user with the static token, context `k3s-local` / `k3s-local-viewer`. The kubeconfig YAML is stored JSON-escaped (`\n` line breaks)
  inside the `kubeconfig` field; the test below proves both entries parse.
- README Kubernetes section: profile command, what each seeded namespace demonstrates, what single-node k3s can't show (NotReady node,
  multi-node drain — handler/page tests cover them), host-run tip (copy the demo file, replace `https://k3s:6443` with
  `https://localhost:6443`), and **How to mint a ServiceAccount token for a real cluster**:
  `kubectl -n kube-system create serviceaccount sbconsole`, bind `view` (or a custom operator role), `kubectl -n kube-system create token sbconsole --duration=8760h`,
  then a kubeconfig template with `token:`.

- [ ] Test first: `DemoConnectionsKubernetesTests.Demo_file_kubernetes_entries_parse` (reads `docker/sqlite/demo-connections.json` via a path relative to the
  repo root found by walking up to `SbConsole.slnx`; every `kind == "kubernetes"` secret parses with `KubeconfigSettings.Parse`, two entries, contexts as named).
- [ ] Implement files. Verify manually:
  `docker compose --profile kubernetes up -d` then
  `docker compose logs k3s-seed` ends with `done`;
  `kubectl --server=https://localhost:6443 --token=sbc-demo-viewer-token-local-only --insecure-skip-tls-verify get pods -A` lists pods and
  shows `payment-gateway-…` in `CrashLoopBackOff` / `OOMKilled` within ~2 min; same with viewer token `auth can-i patch deployments -n checkout` → `no`.
  If the `rancher/k3s:v1.30.6-k3s1` tag doesn't pull, pick the newest `v1.30.*-k3s*` tag that does and use it in both compose and `K3sFixture`.
- [ ] Commit `feat(kubernetes): local k3s stack with seeded demo states and demo connections`.

---

# Phase 2 — Access, shared components, Overview

### Task 6: Permissions model, access cache, permissions handler

**Files:** Create `Access/K8sPermissions.cs`, `Access/AccessCache.cs`, `Access/GetPermissionsQueryHandler.cs`;
tests `Access/K8sPermissionsTests.cs`, `Access/AccessCacheTests.cs`, `Access/GetPermissionsQueryHandlerTests.cs`.

```csharp
public sealed class K8sPermissions
{
    public static K8sPermissions From(K8sAccessFacts facts);
    public static K8sPermissions All { get; }            // used when probing failed: render controls, server 403 is the backstop
    public string Identity { get; }
    public bool Incomplete { get; }                      // any review incomplete
    /// resource like "deployments", "deployments/scale", "pods/eviction"; apiGroup "" core, "apps", "batch", "policy".
    /// ns null = "every reviewed namespace allows it". Wildcards "*" in groups/resources/verbs match. Rules with ResourceNames never grant.
    public bool Can(string apiGroup, string resource, string verb, string? ns = null);
    public bool CanCluster(string check);                // ClusterChecks key; missing key → false
    public bool IsReadOnly { get; }                      // no create/update/patch/delete anywhere in the reviewed rules
    /// For the RBAC chip tooltip: of the given (group, resource, verb) needs, the ones not granted, formatted "deployments/scale · patch".
    public IReadOnlyList<string> Missing(IEnumerable<(string Group, string Resource, string Verb)> needs, string? ns = null);
}
public sealed class AccessCache(TimeProvider clock) : ISingletonService
{   // key: connectionId + sorted namespaces joined ","; TTL 5 min; Invalidate(connectionId) drops all keys for it.
    public bool TryGet(Guid connectionId, IReadOnlyList<string> namespaces, out K8sPermissions permissions);
    public void Set(Guid connectionId, IReadOnlyList<string> namespaces, K8sPermissions permissions);
    public void Invalidate(Guid connectionId);
}
GetPermissionsQueryHandler(IKubernetesOperations, IConnectionProvider, AccessCache, ILogger<…>).HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, bool refresh = false, CancellationToken ct = default) → PluginResult<K8sPermissions>
   // cache hit unless refresh; on probe failure (not cancellation) log a warning and return Ok(K8sPermissions.All) — never block a page.
```
Page needs (used by later tasks, define as static readonly arrays on `K8sPermissions`):
`WorkloadActions` = (apps, deployments/scale, patch), (apps, deployments, patch), (apps, deployments, delete), (apps, statefulsets/scale, patch),
(apps, statefulsets, patch), (apps, statefulsets, delete), (apps, daemonsets, patch), (apps, daemonsets, delete), (batch, jobs, delete), (batch, cronjobs, delete);
`PodActions` = ("", pods, delete); `ConfigActions` = ("", configmaps, update).

- [ ] Tests first: exact-match grant; wildcard verb/resource/group; ResourceNames rule doesn't grant; subresource needs its own rule
  (`deployments` patch does not grant `deployments/scale`); ns-specific (`checkout` yes, `payments` no) with ns null → false;
  `IsReadOnly` for a `view`-like rule set; `Missing` formatting; cache TTL with `FakeTimeProvider` (hit at 4:59, miss at 5:01), namespace
  order-insensitive key, `Invalidate`; handler cache hit doesn't call ops, refresh does, failure returns `All`.
- [ ] Red → implement → green → commit `feat(kubernetes): RBAC permissions model and access cache`.

### Task 7: Shared components, namespace picker, poller, stylesheet

**Files:** Create `Components/K8sPageHeader.razor`, `K8sConnectionPicker.razor`, `K8sNamespacePicker.razor`, `RbacChip.razor`, `K8sStatTile.razor`,
`K8sErrorAlert.razor`, `ReadyBar.razor`, `PhaseChip.razor`, `YamlView.razor`, `YamlDialog.razor`, `K8sPoller.cs`;
`Namespaces/ListNamespacesQueryHandler.cs`, `Namespaces/NamespaceSelectionStore.cs`; `wwwroot/kubernetes.css`, `wwwroot/kubernetes.js`;
tests `Pages/K8sPageTestBase.cs`, `Pages/K8sConnectionPickerTests.cs`, `Pages/K8sNamespacePickerTests.cs`, `Pages/RbacChipTests.cs`,
`Pages/SmallComponentTests.cs`, `Components/K8sPollerTests.cs`, `Namespaces/NamespaceSelectionStoreTests.cs`.

- `K8sPageHeader`, `K8sConnectionPicker`, `K8sStatTile`, `K8sErrorAlert`: copy the Postgres components, rename prefix `pg-` → `k8s-`,
  stylesheet `_content/SbConsole.Plugins.Kubernetes/kubernetes.css`. The picker reads the overview snapshot store (Task 8 creates
  `OverviewSnapshotStore`; in this task add the store file too — same shape as Postgres's but typed on `K8sOverview`). Picker server chip shows
  `Summary["Server"]` · `Summary["Context"]`. `K8sErrorAlert` takes `K8sErrorInfo` (Details collapse shows `HTTP {StatusCode}` when set).
- `NamespaceSelectionStore`: key `ns:{connectionId}:{user}` → JSON string array. `Task<IReadOnlyList<string>> GetAsync(IPluginStore, Guid, string user, ct)`,
  `Task SetAsync(…, IReadOnlyList<string>)`; bad JSON → empty.
- `ListNamespacesQueryHandler.HandleAsync(Guid connectionId, ct) → PluginResult<IReadOnlyList<K8sNamespace>>` (HandlerRunner.QueryAsync).
- `K8sNamespacePicker` (spec §4): parameters `ConnectionId` (Guid?), `SelectionChanged` (`EventCallback<IReadOnlyList<string>>`), `ManageUrl` (default true).
  On connection change: load namespaces; initial selection = `?ns=` (comma list, filtered to existing) else stored selection else empty.
  Renders `MudSelect T="string" MultiSelection="true"` class `k8s-ns-picker`, `SearchBox="true"`, a first item "All namespaces (N)" (class
  `k8s-ns-all`) that clears the selection, then one item per namespace with its pod count (`k8s-ns-option`). Trigger shows chips of selected names
  or "All namespaces"; under the list a caption "Selection is kept per cluster." On change: store (user from `AuthenticationStateProvider` name, fallback "anonymous"),
  update `?ns=` (replace, omitted when empty), raise `SelectionChanged`. Load failure → a small error chip `k8s-ns-error` and selection = all.
- `RbacChip`: parameters `Permissions` (K8sPermissions?), `Needs` (needs list), `Namespace`. Renders nothing when `Permissions` is null or
  `Missing(needs)` is empty. Else `MudTooltip` around `MudChip` class `k8s-rbac-chip` text "read-only (RBAC)" when `IsReadOnly` else "limited (RBAC)";
  tooltip "Connected as {Identity}. Missing for this page: {string.Join(", ", missing)}. Reads work normally."
- `ReadyBar`: `Ready`, `Desired` → text "{r}/{d}" + `MudProgressLinear` (`k8s-ready-bar`) colour Success when equal, Warning when 0 < r < d, Error when r == 0 && d > 0, Default when d == 0.
- `PhaseChip`: `Status`, `Restarts`, `Href?` → chip colour: Running → Success, Pending/ContainerCreating → Warning, CrashLoopBackOff/Error/OOMKilled/ImagePullBackOff/Failed → Error, Terminating → Default (outlined), Completed/Succeeded → Info; text = pod short name passed as `Label` + " ↻{n}" when restarts > 0.
- `YamlView`: `Yaml` string → `<pre class="k8s-yaml">`; top-level keys foldable (split on lines starting with a non-space key followed by `:`); buttons Copy (`kubernetes.js` `copyText`) and Expand all.
- `YamlDialog`: `MudDialog` with title and `YamlView`, loads via `GetResourceYamlQueryHandler` (Task 10 creates it — in this task create the dialog taking a `Func<Task<PluginResult<string?>>> Load` parameter instead, so it has no handler dependency).
- `K8sPoller` (plain class, not a component):
  ```csharp
  public sealed class K8sPoller(TimeProvider clock, TimeSpan interval, Func<Task> tick) : IDisposable
  {
      public bool Paused { get; set; }      // dialogs set this
      public bool Hidden { get; set; }      // set from kubernetes.js visibilitychange via JSInvokable on the page
      public void Start(); public void Stop(); public void Dispose();
      // tick runs only when !Paused && !Hidden && no tick is in flight; exceptions from tick are swallowed (the page shows its own error state).
  }
  ```
- `kubernetes.js` (ES module): `export function copyText(text)`, `export function downloadFile(name, streamRef)` (same as postgres.js's download),
  `export function watchVisibility(dotnetRef)` (calls `dotnetRef.invokeMethodAsync('SetHidden', document.hidden)` on `visibilitychange`, returns
  an object with `dispose()`), `export function scrollToBottom(id)`.
- `kubernetes.css`: layout classes used by every page (`k8s-page`, `k8s-header`, `k8s-tiles` grid 5→2→1 columns, `k8s-card-head`, `k8s-mono`,
  `k8s-muted`, `k8s-stale` opacity .55, `k8s-num` right-aligned tabular, `k8s-table-wrap` overflow-x auto, `k8s-log` monospace pre with max-height 60vh,
  `k8s-yaml`), colours only via `var(--mud-palette-*)`.

`K8sPageTestBase` mirrors `PgPageTestBase`: substitutes `IKubernetesOperations`, `IConnectionProvider` (one `kubernetes` connection named
`aks-prod-eu`, tags `["prod"]`, Summary `Server=aks-prod-eu.example`, `Context=aks-prod-eu-admin`, `TLS=verified`), `IAuditScope`,
`IConfirmationService` (returns true), `InMemoryPluginStore`, `FakeTimeProvider` at 2026-10-14T12:00Z, `AccessCache`, `ISnackbar` via `AddMudServices`,
fake auth "admin", `RenderWithPopovers`. `RegisterHandlers()` constructs every handler that exists so far; later tasks append to it.
`Operations.GetAccessAsync(...)` defaults to admin-like facts (`TestData.AdminAccess()`); `TestData.ViewerAccess()` gives view-only rules.

- [ ] Tests first: picker (first connection selected, `?connectionId=` honoured, PROD chip, red dot from snapshot store);
  namespace picker (loads with counts, `?ns=` wins over stored, stored used otherwise, choosing two namespaces stores them and raises the event,
  "All namespaces" clears, load failure → error chip + empty selection); store round-trip and bad JSON; RbacChip (hidden for admin, "read-only (RBAC)"
  for viewer with tooltip text listing `deployments/scale · patch`); ReadyBar colours; PhaseChip colours and restart suffix; poller with
  `FakeTimeProvider` (ticks every interval, skips while Paused/Hidden, no overlap while a tick is awaiting, Dispose stops).
- [ ] Red → implement → green → commit `feat(kubernetes): shared components, namespace picker, poller`.

### Task 8: Overview operations, mappers, attention rules, handler, dashboard hooks

**Files:** Create `Client/KubernetesOperations.Overview.cs`, extend `Client/K8sMappers.cs`, `Overview/AttentionRules.cs`, `Overview/OverviewSnapshot.cs`,
`Overview/GetOverviewQueryHandler.cs`; modify `KubernetesPlugin.cs` (dashboard hooks + nav badge); tests `Client/K8sMappersTests.cs`,
`Overview/AttentionRulesTests.cs`, `Overview/GetOverviewQueryHandlerTests.cs`, `KubernetesPluginDashboardTests.cs`, `Operations/OverviewOperationsTests.cs`.

```csharp
internal static class K8sMappers
{
    public static K8sPodSummary Pod(V1Pod pod);                              // see rules below
    public static K8sNodeInfo Node(V1Node node, IEnumerable<K8sPodSummary> podsOnNode);
    public static K8sEvent Event(Eventsv1Event e);
    public static K8sWorkload Deployment(V1Deployment d); StatefulSet(V1StatefulSet s); DaemonSet(V1DaemonSet d); Job(V1Job j, DateTimeOffset now); CronJob(V1CronJob c);
    public static string Provider(IReadOnlyList<K8sNodeInfo> nodes);         // providerID prefix azure:// → AKS, aws:// → EKS, gce:// → GKE, k3s:// → k3s, kind:// → kind; label "node.kubernetes.io/instance-type" =="k3s" → k3s; else "Kubernetes"
    public static string? Pool(IReadOnlyDictionary<string, string> labels);  // agentpool | kubernetes.azure.com/agentpool | eks.amazonaws.com/nodegroup | cloud.google.com/gke-nodepool
}
public sealed record AttentionItem(string Chip /*crash loop|not ready|unavailable|job failed|pending*/, string Severity /*Error|Warning*/,
    string? Namespace, string Title, string Detail, string LinkKind /*Pod|Node|Deployment|Jobs|Events*/, string Href);
public static class AttentionRules
{
    public const int Cap = 10;
    public static IReadOnlyList<AttentionItem> Build(K8sOverview overview, DateTimeOffset now, Func<string, string> link);
    // link: appends ?connectionId=… to an href. Ordering: crash loops (by restarts desc), not-ready nodes, unavailable deployments,
    // failed jobs, unschedulable pods grouped per namespace ("{n} pods unschedulable", detail = first scheduler message truncated to 120).
}
GetOverviewQueryHandler(ops, connections, [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store, TimeProvider clock, ILogger)
    .HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) → PluginResult<OverviewView>
public sealed record OverviewView(K8sOverview? Overview, bool IsStale, DateTimeOffset? StaleSince, K8sErrorInfo? Error);
```
Pod mapping rules: `Status` = `metadata.deletionTimestamp` set → "Terminating"; else first container (init first) with `waiting.reason` in
{CrashLoopBackOff, ImagePullBackOff, ErrImagePull, CreateContainerConfigError, ContainerCreating} → that reason; else a terminated container with
reason Error/OOMKilled while phase Running → that reason; else phase (`Succeeded` → "Completed"). `Restarts` = sum of app containers' restartCount.
`LastTermination*` from the container with the most restarts (`lastState.terminated`). `UnschedulableMessage` = `PodScheduled` condition with
reason `Unschedulable` → its message. Requests = sum of app containers' requests (init containers ignored). `Owner`: ownerReference ReplicaSet named
`{x}-{hash}` where hash == label `pod-template-hash` → `K8sRef("Deployment", ns, x)` and `ReplicaSet` = RS name; other owners as-is.
`IsDaemonSetPod` = owner kind DaemonSet; `IsMirrorPod` = annotation `kubernetes.io/config.mirror`.
Node mapping: Ready condition status & lastTransitionTime; `Pressures` = Memory/Disk/PIDPressure conditions with status True; `Roles` from
`node-role.kubernetes.io/{role}` labels, else `["agent"]`; `Taints` = `key=value:effect` excluding `node.kubernetes.io/*`; allocatable via
`K8sFormat.ParseCpuMilli/ParseBytes`; requested = sum over non-terminal (`Running`/`Pending`) pods on the node; `PodCount` = those pods.
Operations `GetOverviewAsync`: in parallel — version, nodes, pods (all namespaces filtered to `namespaces` client-side when the account can list
cluster-wide — try all-namespaces first; on 403 fall back to per-namespace lists, max 8 concurrent), deployments, jobs, events.k8s.io
(`type=Warning` via fieldSelector `type=Warning`, filtered to last 1 h by `EventTime ?? DeprecatedLastTimestamp ?? metadata.creationTimestamp`),
namespaces count, identity (`SelfSubjectReview`, fallback kubeconfig user), and `GetKubernetesNodesMetricsAsync()` (any exception → usage null).
Nodes are cluster-wide even when namespaces are selected (tiles describe the cluster); pods/deployments/jobs/events are scoped.
`CpuUsageFraction` = Σ node metrics cpu / Σ allocatable.

Handler: snapshot store exactly like Postgres's (`overview:{connectionId}`, `WriteGoodAsync` / `WriteFailureAsync`), but the stored overview is
only reused as stale data when the namespace selection matches — store `Namespaces` in `OverviewSnapshot(K8sOverview Overview, DateTimeOffset LoadedAt, IReadOnlyList<string> Namespaces)`.
Failure → `Ok(new OverviewView(last, true, last.LoadedAt, info))` (or null overview).

Plugin hooks: `GetDashboardMetricsAsync(secret)` → `[("Nodes ready", ready), ("Crash loops", crashLoops)]` from `GetOverviewAsync(secret, [])`; failure → `[]`.
`GetDashboardProblemsAsync` → `AttentionRules.Build(...).Take(5)` mapped to `PluginDashboardProblem(Severity, Title, Detail, Href)` with hrefs
without connectionId (the host doesn't add one; use the plain page href). `GetNavBadgeAsync(OverviewHref)` → attention count (null when 0), cached 15 s per
secret hash like Postgres's badge cache; other hrefs → null.

- [ ] Tests first: mappers on hand-built `V1Pod`/`V1Node` objects (crash-loop status, OOM last termination, Terminating, unschedulable message,
  Deployment owner via pod-template-hash, DaemonSet flag, requests sum, node pressures/roles/taints/pool, provider detection table);
  attention ordering, cap, unschedulable grouping, links include connectionId; handler: success stores snapshot, failure returns stale with error,
  different namespaces → no stale reuse; dashboard metrics/problems/badge (cache 15 s with FakeTimeProvider).
  Docker: `Overview_against_k3s_counts_nodes_and_pods` (1 node ready, kube-system pods running), `Overview_scoped_to_a_namespace_only_counts_its_pods`,
  `Crash_looping_pod_is_reported` (create a pod `busybox sh -c "exit 1"` with restartPolicy Always, wait until status CrashLoopBackOff, assert in ProblemPods).
- [ ] Red → implement → green → commit `feat(kubernetes): overview operations, attention rules, dashboard hooks`.

### Task 9: Overview page

**Files:** Create `Pages/Overview.razor`; test `Pages/OverviewPageTests.cs`.

`@page "/p/kubernetes/overview"` (mockup 3a):
- Header "Overview": `K8sConnectionPicker`, `K8sNamespacePicker`, meta, actions: "refreshed N s ago" (`k8s-overview-refreshed`) / "last ok HH:mm",
  live indicator `k8s-live` ("watch · live" when the poller is running), Refresh button `k8s-overview-refresh` (also invalidates `AccessCache`).
- Cluster header `MudPaper` `k8s-cluster-header`: provider chip `k8s-provider-chip`, `ServerUrl` · context `Context`; line 2 "server {ServerVersion} · {n} nodes in {pools} pools · {ns} namespaces"
  (pools = distinct non-null `Pool`; omit "in N pools" when 0); line 3 "connected as {Identity}".
- Tiles (`K8sStatTile`): `k8s-tile-nodes` "Nodes ready" "{ready} / {total}", Warning when ready < total, caption = first not-ready node short name + "NotReady {Age}" else "all ready";
  `k8s-tile-pods` "Pods" "{running} running", caption "{pending} pending · {failed} failed · {succeeded} succeeded";
  `k8s-tile-crashloop` "CrashLoopBackOff" count, Error outline (`Warning` param plus class `k8s-error-outline`) when > 0, caption first two `ns/ownerName`, link "Open pod →" to the first;
  `k8s-tile-cpu` "CPU requests" percent with meter, caption "{Cpu(req)} / {Cpu(alloc)} cores · limits {Cpu(limits)}" + (usage → " · usage {pct}");
  `k8s-tile-memory` "Memory requests" percent with meter, caption "{Bytes(req)} / {Bytes(alloc)}" + " · {n} nodes with > 8 Gi free".
- "Needs attention" card `k8s-attention`: count chip, "worst first", items from `AttentionRules.Build` with chip (Error/Warning colour), namespace,
  title, detail (mono), link text "{LinkKind} →". Empty → "Nothing needs attention." (`k8s-attention-empty`).
- "Recent Warning events" card `k8s-recent-events`: "last 1h · {WarningCount}", "All events" link; first 6 rows: age, reason, kind + name, message (single line, ellipsis), count chip "×N".
- Unreachable / stale: identical behaviour to Postgres Overview (error alert with Retry / Edit connection, dimmed tiles "stale · HH:mm", skeletons when never loaded, retry every 30 s).
- Poller 5 s (`K8sPoller`), paused while hidden (wire `kubernetes.js` `watchVisibility` with a `[JSInvokable] SetHidden(bool)`), stopped on dispose.
- Footnote "Metrics are reads and are not audited."

- [ ] Tests first: tiles render values and captions from a canned `K8sOverview` (`TestData.Overview()` — add it: 12 nodes, 1 NotReady, 2 crash loops, etc.
  matching mockup 3a numbers), crash-loop tile has error outline, attention items in order with links, recent events (6 max), stale state dims tiles and
  shows "stale · 11:58", error alert + retry countdown, poller reloads after advancing time 5 s, namespace change reloads with that namespace list,
  connection change clears old data before load.
- [ ] Red → implement → green → full gate (`dotnet test`) → commit `feat(kubernetes): Overview page`.

---

# Phase 3 — Workloads

### Task 10: Workload operations and handlers

**Files:** Create `Client/KubernetesOperations.Workloads.cs`, `Workloads/ListWorkloadsQueryHandler.cs`, `CountWorkloadsQueryHandler.cs`,
`GetScaleInfoQueryHandler.cs`, `GetResourceYamlQueryHandler.cs`, `ScaleWorkloadCommandHandler.cs`, `RestartWorkloadCommandHandler.cs`,
`DeleteWorkloadCommandHandler.cs`, `Client/YamlDescribe.cs`; tests `Workloads/WorkloadHandlerTests.cs`, `Client/YamlDescribeTests.cs`,
`Operations/WorkloadOperationsTests.cs`.

```csharp
ListWorkloadsQueryHandler.HandleAsync(Guid connectionId, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector, CancellationToken ct = default) → PluginResult<IReadOnlyList<K8sWorkload>>
CountWorkloadsQueryHandler.HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) → PluginResult<IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>>
GetScaleInfoQueryHandler.HandleAsync(Guid connectionId, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default) → PluginResult<K8sScaleInfo>
GetResourceYamlQueryHandler.HandleAsync(Guid connectionId, K8sResourceKind kind, string? ns, string name, CancellationToken ct = default) → PluginResult<string?>
ScaleWorkloadCommandHandler.HandleAsync(ScaleCommand cmd, CancellationToken ct = default) → PluginResult      // Mutating; action k8s.{kind lower}.scale; detail "{from} → {to} replicas"; only Deployment/StatefulSet else Fail("Only Deployments and StatefulSets can be scaled.") not audited; replicas < 0 or > 1000 → Fail("Replicas must be between 0 and 1000.") not audited
RestartWorkloadCommandHandler.HandleAsync(WorkloadCommand cmd, CancellationToken ct = default) → PluginResult   // Mutating; Deployment/StatefulSet/DaemonSet; action k8s.{kind}.restart; detail "restartedAt {iso}"
DeleteWorkloadCommandHandler.HandleAsync(WorkloadCommand cmd, CancellationToken ct = default) → PluginResult    // Destructive; all five kinds; action k8s.{kind}.delete
public static class YamlDescribe { public static string Serialize(object k8sObject); }  // KubernetesYaml.Serialize after nulling metadata.managedFields; for V1Secret throws InvalidOperationException (defence in depth)
```
Kind → audit word: `kind.ToString().ToLowerInvariant()` (deployment, statefulset, daemonset, job, cronjob).
Operations:
- `ListWorkloadsAsync`: per kind, all-namespaces list with `labelSelector` (403 → per-namespace fallback, as Task 8); filter to `namespaces`; map via `K8sMappers`; order by namespace, name.
- `CountWorkloadsAsync`: the five lists (no label selector) in parallel → `K8sKindCount(count, any IsUnhealthy)`.
- `GetScaleInfoAsync`: read the workload; `HpaManaged` = any autoscaling/v2 HPA in the namespace whose `scaleTargetRef` kind+name match; per-pod requests = sum of template containers' requests; `Ready` from status.
- `ScaleAsync`: read current `spec.replicas` (previous), then `PatchNamespaced{Deployment|StatefulSet}ScaleAsync(new V1Patch("{\"spec\":{\"replicas\":N}}", V1Patch.PatchType.MergePatch), …)`.
- `RestartAsync`: strategic-merge patch `{"spec":{"template":{"metadata":{"annotations":{"kubectl.kubernetes.io/restartedAt":"<o format>"}}}}}`.
- `DeleteWorkloadAsync`: `Delete…Async(name, ns, propagationPolicy: "Background")`.
- `GetYamlAsync`: read the resource by kind (Deployment … ConfigMap; Pod; Node cluster-scoped); 404 → null; `YamlDescribe.Serialize`.

- [ ] Tests first: handlers with substitutes — scale audits `k8s.deployment.scale` target `aks-prod-eu/checkout/payment-gateway` detail `"4 → 6 replicas"`;
  StatefulSet uses `k8s.statefulset.scale`; DaemonSet scale refused not audited; bad replicas refused; restart/delete audit risk Mutating/Destructive;
  ops failure → audited failed with friendly 403 sentence; list handler passes namespaces & selector through. `YamlDescribe` strips managedFields, refuses V1Secret.
  Docker: create Deployment `web` (nginx, 1 replica) in `sbc-wl`; list finds it with label selector `app=web` and not with `app=other`; counts; scale to 2 returns 1 and
  status reaches 2; restart sets the annotation on the template; HPA detection (create an HPA targeting it → `HpaManaged`); YAML has no `managedFields`; delete removes it.
- [ ] Red → implement → green → commit `feat(kubernetes): workload operations, scale/restart/delete commands`.

### Task 11: Workloads page

**Files:** Create `Pages/Workloads.razor`, `Components/ScaleDialog.razor`; tests `Pages/WorkloadsPageTests.cs`, `Pages/ScaleDialogTests.cs`.

`@page "/p/kubernetes/workloads"` (mockups 3b, 3g):
- Header "Workloads": connection picker, namespace picker, `RbacChip` with `K8sPermissions.WorkloadActions`.
- When `IsReadOnly` or anything in WorkloadActions is missing: info `MudAlert` `k8s-rbac-alert` "Connected as {Identity}. Missing for this page: {missing}. Reads work normally. Ask a cluster admin for more permissions if you need to act from here."
- Toolbar: filter `MudTextField` `k8s-wl-filter` "Filter by name or image…" (client-side, case-insensitive on name and images), labels `MudTextField` `k8s-wl-labels` "Labels" (selector; applied on Enter/blur → reload).
- `MudTabs` `k8s-wl-tabs`: one panel per kind with "{Kind}s {count}" and an error dot `k8s-tab-failure` when `HasFailure`; `?tab=` selects and is kept in sync.
- Tables per spec §6 columns; class `k8s-wl-table`, rows `k8s-wl-row`; Ready column uses `ReadyBar`; "scaled to 0" chip `k8s-scaled-zero` when Desired == 0; Images via `K8sFormat.Images` in mono; Age via `K8sFormat.Age`.
  Deployment row name links to `DeploymentHref`; other kinds' name opens `YamlDialog`.
- First 50 rows then `k8s-wl-more` "{n} more in these namespaces · show all".
- Row inline buttons (only when permitted for that namespace): Scale `k8s-scale` (Deployment/StatefulSet; outlined), Restart `k8s-restart` (Deployment/StatefulSet/DaemonSet; outlined).
  When not permitted they are **not rendered**.
- Row `MudMenu` `k8s-row-menu`: "Open detail" (Deployments), "View YAML", "Copy kubectl command" (copies `kubectl -n {ns} get {kind lower} {name} -o yaml`), then — if delete permitted — `MudDivider` + "Delete…" (`k8s-delete`, `Color.Error`); if not permitted, the menu ends with `k8s-menu-note` "Scale, restart and delete need more permissions".
- Scale → `ScaleDialog` (`MudDialog`): title "Scale {name}"; text "{ns} · currently {replicas} replicas, {ready} ready." + (HPA → warning line `k8s-scale-hpa` "An HPA ({name}) manages replicas — this change will be overwritten." else "HPA is not attached, so this sticks.");
  `MudNumericField` `k8s-scale-replicas` with Min 0 Max 1000 and − / + buttons; hint `k8s-scale-hint` "+{d} · needs {Cpu(d*cpu)} CPU, {Bytes(d*mem)} more requests" (or "−{d} · frees …"); caption "Audited as k8s.{kind}.scale"; buttons Cancel / "Scale to {n}" (disabled when unchanged). On confirm → handler → snackbar → reload.
- Restart → `Confirmation.ConfirmAsync("Restart", name, isProd: false)` (Mutating = plain) → handler.
- Delete → `ConfirmAsync(new ConfirmationRequest("Delete {kind}", name, connection.IsProd, Consequence: "Deletes the {kind} and, in the background, everything it owns (ReplicaSets and Pods).", Details: [("Namespace", ns), ("Ready", "r/d"), ("Images", images)], AuditAction: $"k8s.{kind}.delete", Destructive: true))` → handler.
- Poller 10 s; paused while any dialog is open.
- Footnote `k8s-wl-footnote` "Scale and Restart are Mutating: a short dialog, no typing. Delete is Destructive and only in the menu. All actions audited · Audit log" (Audit log links `/audit`).

- [ ] Tests first: Deployments tab renders rows with ReadyBar text "2/4", scaled-to-zero chip, filter by image substring, label selector reloads with the selector,
  tab counts + failure dot, `?tab=jobs` opens Jobs; admin sees Scale/Restart and Delete; viewer (`TestData.ViewerAccess()`) sees neither, sees the RBAC alert and the
  menu note; Scale dialog shows HPA warning when `HpaManaged`, hint math (+2 × 500m/512Mi → "needs 1 CPU, 1 Gi more requests"), confirm calls handler with 6;
  Delete sends a `ConfirmationRequest` with `Destructive` and `IsProd` true, declined → no handler call; 51 rows → "1 more…" then show all.
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): Workloads page with scale, restart and delete`.

---

# Phase 4 — Deployment detail

### Task 12: Deployment detail, rollout history, rollback

**Files:** Create `Client/KubernetesOperations.Deployments.cs`, `Deployments/RolloutHistory.cs`, `Deployments/GetDeploymentDetailQueryHandler.cs`,
`Deployments/RollbackDeploymentCommandHandler.cs`, `Pages/DeploymentDetail.razor`; tests `Deployments/RolloutHistoryTests.cs`,
`Deployments/DeploymentHandlerTests.cs`, `Operations/DeploymentOperationsTests.cs`, `Pages/DeploymentDetailPageTests.cs`.

```csharp
public sealed record RolloutRow(long Revision, string Image, DateTimeOffset CreatedAt, string? ChangeCause, bool IsCurrent, bool IsStuck, bool CanRollback, int ReadyPods);
public sealed record StuckRollout(long NewRevision, TimeSpan ProgressingFor, int CrashLoopingPods, string? Reason /*e.g. "OOM"*/, long? ServingRevision, int ServingPods);
public static class RolloutHistory
{
    public static IReadOnlyList<RolloutRow> Build(K8sDeploymentDetail d);          // newest first; CanRollback = !IsCurrent && HasTemplate
    public static StuckRollout? Stuck(K8sDeploymentDetail d, DateTimeOffset now);  // Progressing=False & reason ProgressDeadlineExceeded; ServingRevision = newest non-current RS with Ready > 0
    public static long? RollbackTarget(K8sDeploymentDetail d);                    // ServingRevision ?? previous revision with template
}
GetDeploymentDetailQueryHandler.HandleAsync(Guid connectionId, string ns, string name, CancellationToken ct = default) → PluginResult<K8sDeploymentDetail?>
public sealed record RollbackCommand(K8sTarget Target, long ToRevision);
RollbackDeploymentCommandHandler.HandleAsync(RollbackCommand cmd, CancellationToken ct = default) → PluginResult   // Mutating; k8s.deployment.rollback; detail "rev {from} → rev {to}"
```
Operations:
- `GetDeploymentDetailAsync`: read Deployment (404 → null); ReplicaSets in ns filtered by ownerReference uid; pods by the Deployment selector
  (`labelSelector` built from `matchLabels`), mapped; Services in ns whose selector ⊆ template labels, ready endpoints from EndpointSlices
  (`kubernetes.io/service-name` label) counting `conditions.ready == true`; events for the Deployment, its RSs and pods (events.k8s.io in ns, regarding name in set, newest 20);
  revision = RS annotation `deployment.kubernetes.io/revision`; `IsCurrent` = revision equals Deployment's annotation; change-cause annotation; `HpaManaged` as Task 10.
- `RollbackDeploymentAsync`: find RS with that revision (missing → throw `InvalidOperationException("Revision {n} no longer exists.")` mapped by FriendlyKubernetesError default);
  copy its `spec.template`, remove label `pod-template-hash`, JSON-patch `[{"op":"replace","path":"/spec/template","value":<template>}]`; return previous current revision.

Page `@page "/p/kubernetes/workloads/deployments/{Namespace}/{Name}"` (mockup 3c):
- Header: `MudBreadcrumbs` `k8s-breadcrumbs` (connection name → Workloads, namespace → Workloads?ns=, name), chip "Deployment", PROD chip when prod, picker hidden (connection from `?connectionId=`; missing → message with link back);
  actions: YAML (`YamlDialog`), Scale (ScaleDialog, gated), "Rollout restart" (gated), menu → Delete… (gated, as Task 11, navigates back to Workloads after success).
- Stuck alert `k8s-stuck-alert` (`MudAlert` Warning) text: "Rollout stuck. Revision {new} has been progressing for {Age}; {k} of the new pods are crash-looping{ on OOM when reason}. Revision {serving} is still serving {n} pods." + button `k8s-rollback-stuck` "Rollback to {target}" (gated on deployments patch).
- Facts grid `k8s-facts`: Ready (ReadyBar "r / d desired"), Strategy ("RollingUpdate maxSurge 25% · maxUnavailable 0"), Image (current; "was {prev}" when the previous revision's image differs),
  Resources / pod ("{cpu} · {mem}  limit {memLimit}" + " — OOMKilled at {limit}" when any pod's `LastTerminationReason == "OOMKilled"`), Selector, Service ("{name} · {n} endpoints" or "none"), Age + "{revisions} revisions".
- Relationship strip `k8s-rs-strip`: legend (running / pending / crash loop / terminating); a row per RS with Desired > 0 or pods: card (`k8s-rs-card`: name, "rev {n} · current|old {image}", "desired {d} · ready {r} · {age}") and its pods as `PhaseChip` links to `PodHref` with `?connectionId=`;
  RSs with Desired 0 and no pods collapse into one card `k8s-rs-older` "{n} older · rev {min}–{max} · desired 0 · no pods · kept by revisionHistoryLimit {limit}".
- Conditions table `k8s-conditions` (status chip True=Success/False=Error, type, reason · age).
- Events list `k8s-events` (age, reason, short object, message, ×count).
- Rollout history `k8s-history`: rows from `RolloutHistory.Build`: revision, image tag, "{age} · {change-cause}", chip "stuck" / "current", Rollback button `k8s-rollback` (gated, plain confirm `ConfirmAsync("Roll back to revision", n.ToString(), false)`).
- Poller 10 s.

- [ ] Tests first: history ordering/current/canRollback; stuck detection (true for ProgressDeadlineExceeded, false otherwise) and serving revision; rollback target;
  handler audit detail "rev 23 → rev 22"; Docker: deployment with two revisions (change image), detail lists both RSs with revisions 1 and 2 and pods; rollback to 1 sets the template image back;
  page (`TestData.PaymentGatewayDetail()` modelled on mockup 3c): stuck alert copy, rollback button calls handler with 22, strip shows two RS cards + older card text, pod chips link to pod page,
  "was 2.13.4", "OOMKilled at 512 Mi", viewer sees no Scale/Restart/Rollback, not-found shows "Deployment checkout/x no longer exists."
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): Deployment detail with rollout history and rollback`.

---

# Phase 5 — Pods

### Task 13: Pod detail operations, env resolution, delete pod

**Files:** Create `Client/KubernetesOperations.Pods.cs`, `Pods/GetPodDetailQueryHandler.cs`, `Pods/DeletePodCommandHandler.cs`;
extend `K8sMappers` (`Containers(V1Pod)`, `Env(V1Pod, IReadOnlyDictionary<string, V1ConfigMap> configMaps)`, `OwnerChain`);
tests `Client/PodMappersTests.cs`, `Pods/PodHandlerTests.cs`, `Operations/PodOperationsTests.cs`.

```csharp
GetPodDetailQueryHandler.HandleAsync(Guid connectionId, string ns, string name, CancellationToken ct = default) → PluginResult<K8sPodDetail?>
public sealed record DeletePodCommand(K8sTarget Target);
DeletePodCommandHandler.HandleAsync(DeletePodCommand cmd, CancellationToken ct = default) → PluginResult   // Destructive; k8s.pod.delete; detail "owner {kind}/{name}" when known
```
- Containers: init containers first (`IsInit`), state from `containerStatuses` / `initContainerStatuses` matched by name; `CurrentTermination` = `state.terminated`; `LastTermination` = `lastState.terminated`; ports "{port}/{name ?? protocol lower}".
- Env (spec §8): for each container's `env`: `value` → Literal; `valueFrom.configMapKeyRef` → ConfigMap (value from the map, missing key/map → Unresolved with SourceName); `valueFrom.secretKeyRef` → **Secret, Value null**;
  `fieldRef` → FieldRef resolved for `metadata.name`, `metadata.namespace`, `spec.nodeName`, `status.podIP`, `spec.serviceAccountName`, else value null; `resourceFieldRef` → ResourceFieldRef with the container's quantity string;
  `envFrom.configMapRef` → one row per key (Source ConfigMap); `envFrom.secretRef` → one row `Name = "{prefix}*"`, Source Secret, SourceName the secret, Value null.
  ConfigMaps read = distinct names referenced; 403/404 on a map → its rows Unresolved. **Never** read a Secret.
- Owner chain: pod owner (ReplicaSet with revision annotation / StatefulSet / DaemonSet / Job) → its owner (Deployment / CronJob), max 2 hops.
- Node health: read the node (403 → null) with Ready status, pressures and memory request fraction from `ListPodsOnNodeAsync`-equivalent sums (reuse the node mapper).
- Events: events.k8s.io in ns, regarding kind Pod name = pod, newest first, max 50.
- `DescribeYaml` = `YamlDescribe.Serialize(pod)`.
- `DeletePodAsync`: `DeleteNamespacedPodAsync(name, ns, gracePeriodSeconds: grace)`.

- [ ] Tests first: env resolution table (each source kind; secret rows have null value and SourceName; envFrom secret prefix row; missing configmap → Unresolved);
  containers (init first, OOM last termination, ports); owner chain RS→Deployment with revision; handler delete audit (Destructive, detail); Docker: pod with env from a ConfigMap
  and a Secret → detail env shows configmap value and secret row value null; **`Secret_value_never_appears_in_pod_detail`** (serialize the whole `K8sPodDetail` with System.Text.Json and assert the
  secret's plaintext is absent); delete pod removes it.
- [ ] Red → implement → green → commit `feat(kubernetes): pod detail operations, env resolution, delete pod`.

### Task 14: Logs and the Pod detail page

**Files:** Create `Client/KubernetesOperations.Logs.cs`, `Pods/ReadPodLogsQueryHandler.cs`, `Pods/FollowPodLogsQueryHandler.cs`, `Pods/DownloadPodLogsQueryHandler.cs`,
`Components/LogViewer.razor`, `Pages/PodDetail.razor`; tests `Operations/LogOperationsTests.cs`, `Pods/LogHandlerTests.cs`, `Pages/LogViewerTests.cs`, `Pages/PodDetailPageTests.cs`.

```csharp
ReadPodLogsQueryHandler.HandleAsync(Guid connectionId, string ns, string pod, K8sLogRequest request, CancellationToken ct = default) → PluginResult<IReadOnlyList<string>>
FollowPodLogsQueryHandler.StreamAsync(Guid connectionId, string ns, string pod, K8sLogRequest request, CancellationToken ct) → IAsyncEnumerable<string>
   // resolves the secret first (missing → yields nothing and throws InvalidOperationException(HandlerRunner.ConnectionNotFound)); exceptions from the stream propagate — the viewer turns them into FriendlyKubernetesError text.
DownloadPodLogsQueryHandler.HandleAsync(Guid connectionId, string ns, string pod, CancellationToken ct = default) → PluginResult<byte[]>
```
Operations: `ReadLogsAsync` → `ReadNamespacedPodLogAsync(pod, ns, container, follow: false, previous, tailLines, timestamps)` → read stream as UTF-8 lines (cap at TailLines);
a 400 whose Status message contains "previous terminated container" → return `[]` (no previous instance). `FollowLogsAsync` → `follow: true`, `tailLines`, yields lines via `StreamReader.ReadLineAsync(ct)` until end or cancellation.
`DownloadAllLogsAsync` → for every container (init + app): header line `==> {container} <==`, current logs tail 10,000 (errors become a line `(logs unavailable: {friendly})`), UTF-8 bytes.

`LogViewer` component parameters: `ConnectionId`, `Namespace`, `Pod`, `Containers` (names + `IsCrashLooping` + `HasPrevious` + `LastTermination` + memory limit), `InitialContainer`, `InitialPrevious`.
- Toolbar: container `MudSelect` `k8s-log-container`; `MudToggleGroup` `k8s-log-instance` Current / "Previous (crashed)" (Previous disabled when `!HasPrevious`); switch `k8s-log-follow` (disabled for Previous);
  switch `k8s-log-timestamps`; tail select `k8s-log-tail` (100/500/2000); search `k8s-log-search` with match count `k8s-log-matches` "{i} / {n}" and highlight `<mark>`.
- Default: selected container = `InitialContainer` ?? first crash-looping app container ?? first app container; instance = `InitialPrevious` ?? (container crash-looping && HasPrevious → Previous).
- Body `<pre class="k8s-log" id=…>` lines; level colouring by a token match (` error `, ` warn `, ` info ` → classes `k8s-log-error|warn|info`).
- Follow: starts `FollowPodLogsQueryHandler.StreamAsync` with its own CTS; buffer = ring of TailLines; repaint at most every 250 ms (`TimeProvider` timer flag) and `scrollToBottom`. Toggling off, changing container/instance/tail, or disposing cancels.
- After a Previous load (or Current when the container is Terminated) with a termination: footer `k8s-log-exit` `ExitCodeInterpreter.Explain(term, memLimit)`.
- Errors → inline `K8sErrorAlert`.

Page `@page "/p/kubernetes/pods/{Namespace}/{Name}"` (mockup 3d):
- Header breadcrumbs (connection → Workloads, namespace, owner → Deployment page when Deployment, pod), chip "Pod", PROD chip; actions YAML; menu `k8s-pod-menu`: "Copy pod name", "Download logs (all containers)" (`DownloadPodLogsQueryHandler` → `kubernetes.js downloadFile("{pod}.log", …)`), then — gated on pods delete — divider + "Delete pod…" `k8s-delete-pod` → `ConfirmationRequest("Delete pod", name, isProd, Consequence: owner is not null ? "Its {owner kind} will create a replacement." : "Nothing owns this pod; it won't come back.", Details: [("Node", node), ("Status", status)], AuditAction: "k8s.pod.delete", Destructive: true)` → navigate back to the owner page or Workloads.
- Facts grid `k8s-facts`: Phase (status chip + "↻ {restarts}" + "Running / not ready" when ready < total), Node (name + Ready + "memory {pct}" when fraction known), IP ("{podIp} host {hostIp}"), QoS ("{class} req {cpu} / {mem} · lim {cpuLim ?? —} / {memLim}"), Owner ("{kind} {name} rev {n} · started {Age} ago"), Service account ("{sa} {volumes} volumes · {secretMounts} secret mount(s)").
- Containers table `k8s-containers`: Name ("init · {name}" for init), Image (mono), State ("Running" / "Waiting · {reason}" / "Terminated · {reason}"), Last termination (**error `MudChip` `k8s-term-chip`** "exit {code} · {reason}" + "{Ago} · ran {Age}" when Reason is OOMKilled/Error or exit ≠ 0; grey chip for exit 0; "—" none), Restarts, Ready (yes/no/—), Ports.
- `MudTabs`: Logs (`LogViewer`, `?container=`/`?previous=` honoured), "Events {n}" (table), Describe (`YamlView` with header "read-only · managedFields hidden"), "Env {n}" (per-container `MudSelect`; table Name / Value / Source; Secret rows show `••••••••` and chip "secret {name}"; caption "values from Secrets are never shown"; first 20 then "show all").
- Poller 10 s for the detail (not the log viewer).

- [ ] Tests first: Docker — pod `busybox sh -c "echo one; echo two; exit 3"` restartPolicy Always: current/previous logs (`previous` contains "two" after first restart), tail limits, timestamps prefix, follow yields lines of a
  `sh -c 'i=0; while true; do echo tick $i; i=$((i+1)); sleep 1; done'` pod and stops on cancellation, download contains `==> busybox <==`, previous for a never-restarted pod → empty.
  Viewer: defaults to Previous for crash-looping container, Follow disabled on Previous, tail change reloads with new TailLines, search highlights and counts, exit footer text for OOM, follow cancels on dispose (substituted async enumerable observes cancellation).
  Page: facts from `TestData.CrashLoopPodDetail()` (mockup 3d), termination chip "exit 137 · OOMKilled", env masking (`••••••••`, no secret value anywhere in markup), Describe tab shows YAML, delete pod sends Destructive request with IsProd, viewer (no delete) has no Delete item.
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): Pod detail page with logs, describe and env`.

---

# Phase 6 — Nodes

### Task 15: Nodes list, cordon / uncordon

**Files:** Create `Client/KubernetesOperations.Nodes.cs`, `Nodes/ListNodesQueryHandler.cs`, `Nodes/ListNodePodsQueryHandler.cs`, `Nodes/SetNodeSchedulableCommandHandler.cs`,
`Pages/Nodes.razor`; tests `Nodes/NodeHandlerTests.cs`, `Operations/NodeOperationsTests.cs`, `Pages/NodesPageTests.cs`.

```csharp
ListNodesQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) → PluginResult<IReadOnlyList<K8sNodeInfo>>
ListNodePodsQueryHandler.HandleAsync(Guid connectionId, string node, CancellationToken ct = default) → PluginResult<IReadOnlyList<K8sPodSummary>>
public sealed record SetSchedulableCommand(K8sTarget Target /*Namespace null*/, bool Unschedulable);
SetNodeSchedulableCommandHandler.HandleAsync(SetSchedulableCommand cmd, CancellationToken ct = default) → PluginResult   // Mutating; k8s.node.cordon / k8s.node.uncordon
```
Ops: `ListNodesAsync` = nodes + one `ListPodForAllNamespacesAsync(fieldSelector: "status.phase!=Succeeded,status.phase!=Failed")` grouped by `spec.nodeName` → `K8sMappers.Node`; ordered NotReady first, then name.
`ListPodsOnNodeAsync` = `fieldSelector: $"spec.nodeName={node}"`. `SetUnschedulableAsync` = merge patch `{"spec":{"unschedulable":true|false}}`.

Page `@page "/p/kubernetes/nodes"` (mockup 3e): header "Nodes", connection picker (no namespace picker — nodes are cluster-scoped), meta `k8s-nodes-summary` "{n} nodes · {p} pools · {r} ready · {c} cordoned", RBAC chip with needs (("", nodes, patch)), Pool `MudSelect` `k8s-pool-filter` (All + distinct pools), Refresh.
Table `k8s-nodes-table`, rows `k8s-node-row` (+ `k8s-node-notready` when ReadyStatus ≠ True → bars get `k8s-stale`): Node (name; "{instanceType} · {ip}" caption), Conditions (chip Ready/NotReady/Unknown + age for not ready; pressure chips `k8s-pressure` only when present), Roles (+ taints text), Kubelet, CPU req/alloc (bar + "{Cpu(req)} / {Cpu(alloc)}"), Mem req/alloc (bar + "{Bytes} / {Bytes}"), Pods, Schedulable ("yes" / chip "cordoned"), Age.
Inline: `k8s-cordon` "Cordon" or `k8s-uncordon` "Uncordon" (gated on nodes patch; plain confirm `ConfirmAsync("Cordon", name, false)`). Menu `k8s-node-menu`: "View YAML", "Show {n} pods on this node" (dialog listing `ListNodePodsQueryHandler` rows with PhaseChip links), "Copy kubectl command" (`kubectl describe node {name}`), and — added in Task 16, not here — a divider + "Drain node…" item. In this task the menu ends after "Copy kubectl command".
First 50 rows then "N more nodes · show all". Poller 10 s. Footnote "Cordon/Uncordon flip one field and are Mutating. Drain evicts every pod and is Destructive — it previews the eviction list first. Audited as k8s.node.cordon / .drain".

- [ ] Tests first: handler audits cordon vs uncordon with target `aks-prod-eu/{node}`; Docker: list nodes shows the k3s node Ready with allocatable > 0 and requested > 0; cordon sets Unschedulable, uncordon clears; pods on node non-empty.
  Page: summary text, NotReady row dims bars and sorts first, pressure chip only when true, cordoned row shows Uncordon, pool filter, viewer has no Cordon/Drain, "Show pods" dialog lists pods.
- [ ] Red → implement → green → commit `feat(kubernetes): Nodes page with cordon and uncordon`.

### Task 16: Drain — planner, preview, command, dialog

**Files:** Create `Nodes/DrainPlanner.cs`, `Client/KubernetesOperations.Drain.cs`, `Nodes/PreviewDrainQueryHandler.cs`, `Nodes/DrainNodeCommandHandler.cs`,
`Components/DrainDialog.razor`; modify `Pages/Nodes.razor` (add divider + "Drain node…" `k8s-drain` to the node menu, gated on the `pods/eviction/create` cluster check and nodes patch, opening `DrainDialog`); tests `Nodes/DrainPlannerTests.cs`, `Nodes/DrainHandlerTests.cs`,
`Operations/DrainOperationsTests.cs`, `Pages/DrainDialogTests.cs`.

```csharp
public enum DrainGroup { BlockedByPdb, Unmanaged, WillEvict, DaemonSet }
public sealed record DrainPodPlan(K8sPodSummary Pod, DrainGroup Group, string Note /*"PDB ledger minAvailable 3 · now 3" | "1 of 6 reschedules" | "brief outage" | "ignored" | "no controller"*/, bool HasEmptyDir);
public sealed record DrainPlan(string Node, IReadOnlyList<DrainPodPlan> Pods, int WillEvict, int DaemonSets, int Blocked, TimeSpan Estimate);
public static class DrainPlanner
{   // order: BlockedByPdb, Unmanaged, WillEvict, DaemonSet; within group by namespace, name. Mirror pods excluded entirely.
    // Blocked = a PDB in the pod's namespace whose Selector ⊆ pod Labels and DisruptionsAllowed == 0.
    // Estimate = ceil(WillEvict / 5) * grace, at least grace when WillEvict > 0.
    public static DrainPlan Plan(K8sDrainInputs inputs, int gracePeriodSeconds);
}
PreviewDrainQueryHandler.HandleAsync(Guid connectionId, string node, int gracePeriodSeconds, CancellationToken ct = default) → PluginResult<DrainPlan>
public sealed record DrainOptions(bool DeleteEmptyDirData, bool Force, int GracePeriodSeconds);
public sealed record DrainCommand(K8sTarget Target, DrainOptions Options);
public enum DrainPodOutcome { Evicted, Deleted, Blocked, Skipped, Failed }
public sealed record DrainPodResult(string Namespace, string Name, DrainPodOutcome Outcome, string? Reason);
public sealed record DrainProgress(int Done, int Total, DrainPodResult? Last);
public sealed record DrainResult(IReadOnlyList<DrainPodResult> Pods, bool Cancelled, bool TimedOut);
DrainNodeCommandHandler(ops, connections, audit, TimeProvider clock, logger)
    .HandleAsync(DrainCommand cmd, IProgress<DrainProgress>? progress, CancellationToken ct = default) → PluginResult<DrainResult>
```
Drain algorithm (spec §10.1): resolve secret; `SetUnschedulableAsync(true)`; `GetDrainInputsAsync` → `DrainPlanner.Plan`; for each non-DaemonSet pod (5 concurrent, `SemaphoreSlim`):
pod with emptyDir and `!DeleteEmptyDirData` → Skipped "has emptyDir data"; Unmanaged and `!Force` → Skipped "no controller — use force"; else `EvictPodAsync` →
Evicted / NotFound (→ Evicted, "already gone") / BlockedByPdb → if Force: `DeletePodAsync(grace)` → Deleted; else retry every 5 s (clock delay via `Task.Delay(TimeSpan, clock, ct)`) until the overall 5 min deadline → Blocked.
Report each result through `progress`. Overall deadline → `TimedOut`. Caller cancellation → stop scheduling, `Cancelled = true` (node stays cordoned), still audited.
Audit once at the end: `k8s.node.drain`, Destructive, succeeded = no Blocked/Failed/Skipped and not cancelled/timed out, detail
`"grace {g}s · force {f} · emptyDir {e} · evicted {a} · deleted {b} · blocked {c} · skipped {d}{ · cancelled after N of M}"` + `" · pods: ns/name, …"`
(pod list capped at 50 names, then "…").
`GetDrainInputsAsync`: node (`K8sMappers.Node`), pods on node, PDBs in the pods' namespaces (policy/v1), owner replicas for ReplicaSet/StatefulSet owners (read each distinct owner's `spec.replicas`; for RS owners report the Deployment's replicas).
`EvictPodAsync`: `CreateNamespacedPodEvictionAsync(new V1Eviction { Metadata = new() { Name, NamespaceProperty = ns }, DeleteOptions = new() { GracePeriodSeconds = g } }, name, ns)`; 429 → BlockedByPdb; 404 → NotFound.

`DrainDialog` (mockup 3h) parameters: `ConnectionId`, `ConnectionName`, `IsProd`, `Node`:
- Title "Drain node {name}" + PROD chip. Intro: "Cordons the node, then evicts every pod below through the Eviction API, honouring PodDisruptionBudgets. {WillEvict + Blocked + Unmanaged} pods will be rescheduled; {Blocked} can't be and will block the drain unless you force." + (node NotReady → " This node is already NotReady, so the pods aren't serving anyway.").
- Tiles `k8s-drain-evict` "Will evict", `k8s-drain-ds` "DaemonSet (ignored)", `k8s-drain-blocked` "Blocked by PDB", `k8s-drain-time` "Est. time ~{Age(estimate)}".
- Pod list `k8s-drain-pods` (filter box) rows: namespace, name, owner kind, group note chip; blocked rows first (warning tint). 30 rows then "{n} more…".
- Options: `k8s-drain-emptydir` "Delete emptyDir data", `k8s-drain-force` "Force past PDBs (evicts {names of blocked})", `k8s-drain-grace` grace seconds (numeric, default 30, re-plans the estimate).
- When `IsProd`: `MudTextField` `k8s-drain-confirm` "Type {node} to confirm"; Drain button disabled (and rendered at 45% opacity via class `k8s-disabled-soft`) until the exact name is typed. Not prod → no typing.
- Caption "Audited as k8s.node.drain with your name, the node, the options and the list of evicted pods."
- Buttons: Cancel, "Cordon only" `k8s-cordon-only` (runs `SetNodeSchedulableCommandHandler` with Unschedulable true; Mutating), "Drain node" `k8s-drain-run` (Error colour).
- While running: progress bar `k8s-drain-progress` "{done} / {total}" and a results list (outcome chips); the dialog's Cancel becomes "Stop" (cancels the CTS); on completion a summary line and Close. The page reloads after close.
- This dialog does its own typed confirmation (the mockup's design) — it does **not** additionally call `IConfirmationService`.

- [ ] Tests first: planner (PDB blocked first with note "PDB ledger minAvailable 3 · now 3", DaemonSet ignored, unmanaged group, mirror excluded, replica notes "1 of 6 reschedules" / "brief outage" for 1 replica, estimate math);
  handler with substitutes + `FakeTimeProvider` (cordons first; evicts 5 at a time; blocked pod retried every 5 s then Blocked at 5 min; Force deletes blocked; emptyDir skipped unless opted; cancellation → Cancelled and audit "cancelled after"; audit detail format; progress reported per pod);
  Docker: single-node k3s — Deployment `drainme` 2 replicas + PDB `minAvailable: 2` → preview shows both Blocked; drain without force on a 20 s overall deadline override (add `internal TimeSpan Deadline` settable for tests) → Blocked; with force → Deleted; node uncordoned in teardown.
  Dialog: tiles and copy from a canned plan (mockup 3h numbers), typed confirm gating on PROD (button disabled until exact name), not-prod has no text field, Cordon only calls the cordon handler, run shows progress then summary.
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): node drain with eviction preview and PDB handling`.

---

# Phase 7 — Config

### Task 17: ConfigMaps, Secrets metadata, Config page

**Files:** Create `Client/KubernetesOperations.Config.cs`, `Config/ConfigMapUsage.cs`, `Config/ListConfigMapsQueryHandler.cs`, `Config/ListSecretsQueryHandler.cs`,
`Config/UpdateConfigMapCommandHandler.cs`, `Components/ConfigMapEditDialog.razor`, `Pages/Config.razor`; tests `Config/ConfigMapUsageTests.cs`, `Config/ConfigHandlerTests.cs`,
`Operations/ConfigOperationsTests.cs`, `Pages/ConfigPageTests.cs`, `Client/SecretSummaryShapeTests.cs`.

```csharp
public sealed record ConfigMapUser(string Name, IReadOnlyList<string> Via);   // "payment-gateway", ["env","volume"]
public sealed record ConfigMapRow(K8sConfigMap Map, IReadOnlyList<ConfigMapUser> UsedBy) { public bool Unused => UsedBy.Count == 0; }
public static class ConfigMapUsage
{   // kube-root-ca.crt is "all pods · projected" when unreferenced explicitly.
    public static IReadOnlyList<ConfigMapRow> Resolve(K8sConfigData data);
}
ListConfigMapsQueryHandler.HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) → PluginResult<IReadOnlyList<ConfigMapRow>>
ListSecretsQueryHandler.HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) → PluginResult<IReadOnlyList<SecretSummary>>
public sealed record UpdateConfigMapCommand(K8sTarget Target, IReadOnlyDictionary<string, string> Original, IReadOnlyDictionary<string, string> Data, string ResourceVersion);
UpdateConfigMapCommandHandler.HandleAsync(UpdateConfigMapCommand cmd, CancellationToken ct = default) → PluginResult
   // Mutating; k8s.configmap.update; detail "added a, b · changed c · removed d" (key names only — a test asserts no value appears); no changes → Fail("Nothing changed.") not audited
```
Ops:
- `ListConfigMapsAsync`: config maps (all-ns or per-ns fallback, filtered) + pods in the same namespaces → `K8sConfigRef`s (env `configMapKeyRef`, `envFrom.configMapRef` → "env"; `volumes[].configMap` → "volume";
  `volumes[].projected.sources[].configMap` → "projected"); `User` = the pod's top-level owner name (via `K8sMappers.Pod(...).Owner?.Name`) else pod name. `BinarySizes` from `binaryData` lengths.
- `UpdateConfigMapAsync`: read, set `Data = data`, `Metadata.ResourceVersion = resourceVersion`, `ReplaceNamespacedConfigMapAsync` (409 surfaces through FriendlyKubernetesError as the "changed since" sentence).
- `ListSecretsAsync` (spec §9.2 — the only Secret-touching code): list `V1Secret` (all-ns or per-ns); **inside the method** map each to `SecretSummary`
  (`KeyNames` = `Data` keys sorted; `TlsNotAfter` for type `kubernetes.io/tls` = `X509Certificate2.CreateFromPem` / `new X509Certificate2(bytes)` on `tls.crt` → `NotAfter` (UTC), parse failure → null, certificate disposed);
  then null out the local list reference. No logging of the list, no caching. Wrap in try/catch that rethrows a **new** exception for non-HTTP failures so no Secret content can ride in an exception message (`throw new InvalidOperationException("Listing secrets failed.", ex is HttpOperationException ? ex : null)`).

Page `@page "/p/kubernetes/config"` (mockup 3f): header "Config", pickers, filter `k8s-config-filter`, RBAC chip with `ConfigActions`.
- ConfigMaps card `k8s-configmaps` "ConfigMaps {n} in {ns label}": `MudTable` with `ChildRowContent` expand (click row toggles `k8s-cm-row-expanded`); columns Name, Keys, "Used by" (`"{name} · {via joined ' + '}"`, multiple users comma-joined; `unused` chip `k8s-unused`), Age.
  Expanded: key/value grid `k8s-cm-kv` — value shown inline when ≤ 200 chars and single-line, else collapsed "{n} lines · expand" or "{…} {Bytes} · expand" for JSON-looking values (`k8s-cm-expand` toggles a `<pre>`); binary keys "{Bytes} binary".
  Actions in the expanded area: "Copy as YAML" (YAML of the map via `GetResourceYamlQueryHandler`) and "Edit…" `k8s-cm-edit` (gated on configmaps update).
- `ConfigMapEditDialog`: rows of key `MudTextField` + value `MudTextField Lines=3` + remove button; "Add key"; Save → `ConfirmAsync("Update ConfigMap", name, false)` → handler; 409 error shown inline in the dialog ("It changed since you loaded it — reload and try again.") with a Reload button.
- Secrets card `k8s-secrets` "Secrets {n} in {ns label}" + chip "metadata only"; lock callout `k8s-secrets-callout`: "Secret values are never shown — the console reads key names, type and age only. Rotate or read values with your cluster's secret manager.";
  table Name, Type, Keys (chips `k8s-secret-key`), Age; TLS rows add chip `k8s-tls-expiry` "expires in {d}d" (Warning < 30, Error < 7, "expired" Error when past).
- No auto-refresh; Refresh button.

- [ ] Tests first: **`SecretSummary_has_no_value_bearing_members`** (reflection: property names exactly Namespace, Name, Type, KeyNames, CreatedAt, TlsNotAfter); usage resolution (env/volume/projected grouping by owner, unused, kube-root-ca.crt);
  update handler detail lists key names only (assert a distinctive value string is absent from the audit detail), "Nothing changed" not audited, 409 → friendly sentence;
  Docker: secret with value `s3cr3t-value-xyz` → `ListSecretsAsync` result serialized with System.Text.Json does not contain it; TLS secret with a 9-day cert (generate in-test with `CertificateRequest.CreateSelfSigned`) → TlsNotAfter ≈ now+9d;
  configmap used by a pod via env and volume → refs; update with stale resourceVersion → 409 sentence; update applies.
  Page: rows, expand shows kv grid with collapsed long values, unused chip, edit dialog save calls handler with Original + new Data, viewer has no Edit, secrets table key chips and expiry chip colours, callout text, page markup never contains a secret value from test data.
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): Config page — ConfigMaps with edit, Secrets as metadata`.

---

# Phase 8 — Events

### Task 18: Events page

**Files:** Create `Client/KubernetesOperations.Events.cs`, `Events/EventGrouping.cs`, `Events/ListEventsQueryHandler.cs`, `Pages/Events.razor`;
tests `Events/EventGroupingTests.cs`, `Events/ListEventsQueryHandlerTests.cs`, `Operations/EventOperationsTests.cs`, `Pages/EventsPageTests.cs`.

```csharp
public enum EventGroupBy { InvolvedObject, Reason, None }
public sealed record EventGroup(string Key, K8sRef? Object, string? Reason, IReadOnlyList<K8sEvent> Events, int WarningCount, DateTimeOffset Newest, IReadOnlyList<string> TopReasons);
public static class EventGrouping
{   // groups ordered by Newest desc; events in a group newest first; None → one group with Key "" holding all events newest first. TopReasons: up to 2 distinct reasons by count.
    public static IReadOnlyList<EventGroup> Group(IReadOnlyList<K8sEvent> events, EventGroupBy by);
}
public sealed record EventQuery(IReadOnlyList<string> Namespaces, TimeSpan Window, string? Type /*null|Warning|Normal*/, string? Kind, string? Search);
public sealed record EventList(IReadOnlyList<K8sEvent> Events, int Total, int Warnings, int Normals, IReadOnlyList<string> Kinds);
ListEventsQueryHandler.HandleAsync(Guid connectionId, EventQuery query, CancellationToken ct = default) → PluginResult<EventList>
   // fetch window once, counts over the unfiltered window, then filter by type/kind/search (case-insensitive over reason + message).
```
Ops `ListEventsAsync`: events.k8s.io all-ns (fallback per-ns), keep `LastSeen ≥ now - window`, map; `Count` = `Series?.Count ?? DeprecatedCount ?? 1`; `LastSeen` = `Series?.LastObservedTime ?? EventTime ?? DeprecatedLastTimestamp ?? creationTimestamp`; `Reporter` = `ReportingController ?? DeprecatedSource?.Component`.

Page `@page "/p/kubernetes/events"` (mockup 3i): header "Events", pickers; toolbar: `MudToggleGroup` `k8s-ev-type` "All {total}" / "Warning {w}" / "Normal {n}" (default Warning), Kind select `k8s-ev-kind` (Any + Kinds), window select `k8s-ev-window` (1h/6h/24h; caption "events usually expire after 1 h"), search `k8s-ev-search` (debounced 300 ms), switch `k8s-ev-live` (default on → poller 5 s);
group-by toggle `k8s-ev-groupby` "Involved object" / "Reason" / "None"; summary `k8s-ev-summary` "{warnings} warnings on {groups} objects · newest first · counts are count / series over the window".
`MudExpansionPanels` `k8s-ev-groups` (first 3 expanded): header kind chip, namespace, name (link to pod/deployment page when Pod/Deployment), top reasons, "{WarningCount} warnings", "last {Age}"; body rows `k8s-ev-row`: age, reason (Warning in error colour), message, count chip "×N" when > 1, reporter right-aligned.
Group by None renders a flat table. Footer `k8s-ev-footnote`: "Events are the cluster's own, retained ~1h by the API server. The console doesn't store them; the audit log is for console actions. Read-only page · nothing here is audited".

- [ ] Tests first: grouping (by object/reason/none, order, warning counts, top reasons); handler filters + counts over the unfiltered set; Docker: a failing pod produces a Warning event listed with Count ≥ 1 and reporter kubelet;
  page: default Warning filter, counts in toggle labels, groups ordered with first 3 expanded, link to pod page, Live off stops polling (advance time, ops not called again), search filters.
- [ ] Red → implement → green → full gate → commit `feat(kubernetes): Events page`.

---

# Phase 9 — Docs and final verification

### Task 19: Docs, final gate, browser check

**Files:** Modify `docs/design.md` (append `## 19. Kubernetes plugin (SbConsole.Plugins.Kubernetes) (2026-10-14)`), `CLAUDE.md`
(plugin list sentence + `src/Plugins/Infrastructure/` in the layout tree + compose line `docker compose --profile kubernetes up -d  # + k3s (single node, seeded namespaces, demo crash loop / stuck rollout / PDB) — host port 6443`
+ testing bullet "Third exception: Kubernetes plugin operations are tested against a real k3s Testcontainer, skipped when Docker is unavailable (see docs/design.md §19)"),
`README.md` (plugin list), `docker/README.md` (already written in Task 5 — re-check against what shipped).

design.md §19 records the decisions worth keeping: static-credential-only kubeconfig and why (exec/auth-provider/file paths run or read things on the host);
secret-handling rule (typed list mapped to `SecretSummary` inside one method; TLS expiry is the single value read; env never fetches Secrets); polling over watches;
RBAC via SelfSubjectRulesReview cached 5 min, controls removed not disabled, 403 as backstop, "all namespaces" probed as `default`; drain in-request with progress,
5-minute deadline, cordon kept on cancel; ConfigMap change history dropped (Kubernetes has none); static demo tokens for k3s; k3s Testcontainer exception;
out-of-scope Exec / Port-forward and why (needs a bidirectional stream through Blazor Server).

- [ ] `dotnet build -warnaserror` → 0 warnings, 0 errors.
- [ ] `dotnet test` → all green (Docker tests ran, not skipped — confirm the k3s test count in the output is > 0 skipped = 0 for the Kubernetes project).
- [ ] Browser check: `docker compose --profile kubernetes up -d`; run the app with `SBC_DEMO_CONNECTIONS` pointing at a copy of the demo file with `https://localhost:6443`
  (`dotnet run --project src/SbConsole.Web --launch-profile http`); with `k3s-local`: Overview shows the crash loops, failed job and unschedulable pod; Workloads scale `checkout-api`
  shows the HPA warning; `payment-gateway` detail shows the stuck alert and rollback works; its crashing pod opens Logs on Previous with the OOM footer; Nodes cordon/uncordon; drain dialog
  preview shows `ledger-*` blocked by PDB (cancel without draining); Config shows `reports-config` unused, Secrets without values and the TLS expiry chip; Events grouped.
  With `k3s-local-viewer`: RBAC chip, no Scale/Restart/Delete/Cordon/Drain/Edit controls.
- [ ] Commit `docs(kubernetes): design §19, CLAUDE.md, README`.

---

## Self-review notes

- Spec coverage: §1 pages → T9, T11, T12, T14, T15–16, T17, T18; §2 decisions → Global Constraints, T4 (client/factory), T13/T17 (secret rule);
  §3.1–3.3 → T1, T2, T4; §4 → T7; §5 → T8, T9; §6 → T10, T11; §7 → T12; §8 → T13, T14; §9.1 → T15; §9.2 → T17; §9.3 → T18;
  §10 actions → T10, T12, T13, T15, T16, T17 (table rows each map to one handler); §10.1 → T16; §11 RBAC → T6, T7, gating in every page task; refresh → T7 poller + each page;
  §12 → T3; §13 → T4 (fixture), T5 (stack), tests per task; §14 phases → plan phases 1–9.
- Spec §5 header "strongest ClusterRole binding when readable" is reduced to identity only — listing ClusterRoleBindings needs extra permissions most accounts lack; recorded in §19.
- "All namespaces" RBAC probing uses `default` (rules review requires a namespace); the 403 backstop covers the gap — recorded in §19.
- Drain's typed confirmation lives in `DrainDialog` itself (its preview is the confirmation surface), not in `IConfirmationService`; every other Destructive action uses `ConfirmationRequest`.
