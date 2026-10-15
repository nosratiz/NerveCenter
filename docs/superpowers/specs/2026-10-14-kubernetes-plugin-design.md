# Kubernetes plugin — Design

Status: approved, 2026-10-14.

## 1. Context

SbConsole's eighth plugin, built from mockup `SbConsole Kubernetes.dc.html` (frames 3a–3i):

1. **Overview** (3a) — cluster header, KPI tiles, worst-first "Needs attention", recent Warning events.
2. **Workloads** (3b, 3g) — Deployments / StatefulSets / DaemonSets / Jobs / CronJobs tabs, scoped by
   a multi-namespace picker; Scale and Restart inline, Delete in the row menu.
3. **Deployment detail** (3c) — Deployment → ReplicaSets → Pods strip, conditions, events, rollout
   history with Rollback, stuck-rollout alert.
4. **Pod detail** (3d) — container table, Logs (current / previous, follow, tail), Events, Describe
   (YAML), Env (Secret-sourced values masked), Delete pod.
5. **Nodes** (3e, 3h) — conditions, requests vs allocatable, Cordon / Uncordon, Drain with an
   eviction preview and typed confirm on PROD.
6. **Config** (3f) — ConfigMaps (expand inline, Edit) and Secrets as metadata only.
7. **Events** (3i) — the cluster's own events, Warning/Normal filter, grouped by involved object.

3g (read-only service account) is a cross-cutting RBAC state, not a page.

All of 3a–3i is in scope for one spec and one plan, built in ordered phases (§14). **Out of scope**:
Exec, Port-forward, "Add label…", "Edit taints…" — their controls are not rendered. Exec and
Port-forward need a bidirectional stream through Blazor Server and get their own spec later.

A connection is **one kube context**. The header carries a namespace picker next to the connection
picker, and every namespaced list is scoped by it.

## 2. Decisions

- **Client: `KubernetesClient` 21.0.8** (the official C# client), referenced by the plugin only — as
  Npgsql is by Postgres. It gives kubeconfig loading, typed models, Eviction, access reviews and log
  streams. Hand-rolled REST was rejected (re-implementing kubeconfig/TLS/paging/streaming for no
  gain); shelling to `kubectl` was rejected (binary in the container, fragile parsing, same problem as
  exec auth).
- **Operations wrapper**: `IKubernetesOperations` (`ISingletonService`) is the only thing that touches
  `KubernetesClient`. Handlers and pages depend on the interface; unit tests substitute it.
- **Static credentials only.** The secret is a pasted kubeconfig plus a chosen context; `exec` and
  `auth-provider` users are rejected (§3.2). Nothing runs a program on the host.
- **Polling, not watches.** Pages re-query on a timer (§11). The one stream is log follow.
- **Testing exception, like Postgres**: `KubernetesOperations` is tested against a real k3s
  Testcontainer (`Testcontainers.K3s` 4.16.0), skipped when Docker is unavailable (`[DockerFact]`,
  reused from the Postgres tests).
- **Secret values never leave `KubernetesOperations`** (§9.2). This is a review rule: no
  `V1Secret` (or its `Data`) appears in any model, handler, page or log line.
- **No SDK changes.** Destructive confirms use the detailed `ConfirmationRequest` overload Postgres
  added.

## 3. Plugin shape and connection

```
src/Plugins/Infrastructure/SbConsole.Plugins.Kubernetes/   (new category folder beside Databases/ and Messaging/)
  KubernetesPlugin.cs          Id/Kind "kubernetes", DisplayName "Kubernetes", Monogram "K8",
                               5 nav items, dashboard hooks
  HandlerRunner.cs, PluginResult.cs     (same pattern as Postgres)
  Client/   IKubernetesOperations, KubernetesOperations.*.cs (partials: Overview, Workloads, Pods,
            Logs, Nodes, Drain, Config, Events, Access), models, KubeconfigSettings,
            KubeconfigValidator, KubernetesConnectionFields.razor, FriendlyKubernetesError,
            K8sFormat (ages, quantities), ExitCodeInterpreter, YamlDescribe
  Overview/ Workloads/ Pods/ Nodes/ Config/ Events/ Access/       handlers, pure logic, stores
  Components/  K8sConnectionPicker, K8sNamespacePicker, K8sPageHeader, K8sStatTile, ReadyBar,
               PhaseChip, LogViewer, YamlView, RbacChip, K8sErrorAlert
  Pages/       Overview, Workloads, DeploymentDetail, PodDetail, Nodes, Config, Events (.razor)
  wwwroot/     kubernetes.css, kubernetes.js (log download, copy)
tests/SbConsole.Plugins.Kubernetes.Tests/
```

Routes (all take `?connectionId=…`; list pages also `&ns=a,b`, absent = the stored selection):

- `/p/kubernetes/overview`, `/workloads?tab=deployments|statefulsets|daemonsets|jobs|cronjobs`,
  `/nodes`, `/config`, `/events`
- `/p/kubernetes/workloads/deployments/{ns}/{name}`
- `/p/kubernetes/pods/{ns}/{name}?container=…&previous=true`

Registration: `AddSbConsolePlugin<KubernetesPlugin>()` in `Program.cs`, solution and test project
entries. `Contribution => new(PageCount: 7, ActionCount: 9)`.

### 3.1 Secret format

JSON, encrypted like every other secret:

```json
{ "kubeconfig": "<the pasted YAML>", "context": "aks-prod-eu-admin" }
```

`KubeconfigSettings.Parse(secret)` deserializes, runs `KubeconfigValidator`, and exposes
`BuildClientConfiguration()` → `KubernetesClientConfiguration` via
`KubernetesClientConfiguration.BuildConfigFromConfigFile(stream, currentContext: context)`, with
client-side timeout 30 s. `ToSecretString()` round-trips.

### 3.2 `KubeconfigValidator`

Parses the YAML (`KubernetesYaml` / `K8SConfiguration`) and returns field-level errors:

- The named context exists, and its cluster and user entries exist.
- Cluster `server` is an absolute `https://` URL (`http://` allowed only for `localhost`/`127.0.0.1`).
- CA: `certificate-authority-data` or `insecure-skip-tls-verify: true` or neither (system trust).
  **`certificate-authority:` (a file path) is rejected.**
- User has exactly one static credential: `token`, `client-certificate-data` + `client-key-data`, or
  `username` + `password`. **Rejected with an explanatory message**: `exec`, `auth-provider`,
  `tokenFile`, `client-certificate:` / `client-key:` paths. File paths would read the SbConsole
  host's filesystem; exec would run a program on it. The message suggests a ServiceAccount token
  and links to the README section that shows how to mint one.
- Size cap 256 KB.

`insecure-skip-tls-verify` is accepted (local clusters need it) and shows in the summary.

### 3.3 Connection form and test

`KubernetesConnectionFields.razor` (the three-parameter contract from `IPlugin`): a monospace
textarea for the kubeconfig, a Context select filled from the pasted YAML's contexts (re-parsed on
change; the kubeconfig's `current-context` preselected), validator errors under the field, and help
text on static credentials.

Summary chips: `Server = host`, `Context = name`, `TLS = verified|not verified`. Never the user or
token.

`TestConnectionAsync`: `GET /version` then `SelfSubjectReview` (falls back to the kubeconfig user
name on clusters < 1.28). OK message: `v1.30.4 · as system:serviceaccount:platform:sbconsole-admin`.

## 4. Namespace selection — `K8sNamespacePicker`

`MudSelect` multi with a search field, an "All namespaces (N)" row above a divider, and each
namespace with its pod count. The trigger shows the selected names as chips. Namespaces come from
`ListNamespacesQueryHandler` (names + pod counts from the same pod list the page already makes, or a
cheap `limit`ed list when not).

The selection is stored per user and per connection in `IPluginStore` key
`ns:<connectionId>:<userId>` ("Selection is kept per cluster"). Empty selection = all namespaces.
Multiple namespaces are fetched as one cluster-wide list filtered client-side when the account can
list cluster-wide, else as one call per namespace in parallel (max 8 concurrent).

## 5. Overview (3a) — `/p/kubernetes/overview`

`GetOverviewQueryHandler` → `IKubernetesOperations.GetOverviewAsync(secret, namespaces)`, parallel
calls for version, nodes, pods, Deployments, Jobs and Warning events (last 1 h):

| Tile | Source |
|---|---|
| Nodes ready | `Ready=True` count / total; worst not-ready node and its age. **Warning outline** when any not ready |
| Pods | counts by phase: running, pending, failed, succeeded |
| CrashLoopBackOff | containers in `waiting.reason == CrashLoopBackOff`; first two as `ns/owner`; link to the pod. **Error outline** when > 0 |
| CPU requests | Σ container requests of non-terminal pods / Σ node allocatable; Σ limits; usage % from `metrics.k8s.io` when available, else the line is omitted |
| Memory requests | as CPU; subtitle "N nodes left with > 8 GiB free" |

Header: provider chip (`AKS` / `EKS` / `GKE` / `k3s` / `kind` / `Kubernetes`, from node
`providerID` and well-known labels), server URL, context, server version, node count and pools,
namespace count, connected identity and its strongest ClusterRole binding when readable (else just
the identity), "refreshed N s ago", Refresh.

**Needs attention** (`AttentionRules`, pure, unit-tested), worst first, each linking to the page
that fixes it: crash-looping pods (by restart count) → Pod; NotReady nodes (with pods affected) →
Nodes; Deployments with `Available=False` or `Progressing=False/ProgressDeadlineExceeded` →
Deployment; failed Jobs (`Failed` condition, reason) → Workloads Jobs tab; unschedulable pods grouped
per namespace with the scheduler message → Events. Capped at 10 with "N more".

**Recent Warning events**: newest 6 with age, reason, kind, object, message (one line), count chip;
"All events" → Events.

**Unreachable**: the last good overview is kept in `IPluginStore` key `overview:<connectionId>`
(stamped). On failure: error `MudAlert` with the friendly sentence, Retry / Edit connection, tiles
render dimmed with "stale · HH:mm"; never-loaded sections show `MudSkeleton` rows. Writing the
snapshot is not audited (same rule as Postgres).

**Dashboard hooks**: `GetDashboardMetricsAsync` → Nodes ready, CrashLoop count; 
`GetDashboardProblemsAsync` → the top Needs-attention items; `GetNavBadgeAsync` on Overview → the
Needs-attention count.

## 6. Workloads (3b, 3g) — `/p/kubernetes/workloads`

`ListWorkloadsQueryHandler(kind, namespaces, labelSelector?)`. Tabs with counts; a tab whose list
contains a failure (failed Job, unavailable Deployment) shows a small error dot.

| Tab | Columns |
|---|---|
| Deployments | Name, Namespace, Ready (text + `ReadyBar` tinted by health), Up-to-date, Available, Image(s), Age |
| StatefulSets | Name, Namespace, Ready, Image(s), Age |
| DaemonSets | Name, Namespace, Desired, Ready, Up-to-date, Image(s), Age |
| Jobs | Name, Namespace, Completions, Status (Complete / Failed + reason / Running), Duration, Age |
| CronJobs | Name, Namespace, Schedule, Suspended, Last schedule, Active, Age |

Filter by name or image (client-side), Labels (a selector sent server-side). Rows scaled to 0 show
"scaled to 0". First 50 rows render, then "N more in these namespaces · show all".

Row actions (Deployments / StatefulSets): **Scale** and **Restart** inline (Mutating, gated by
RBAC); menu: Open detail, View YAML, Copy kubectl command (Safe), divider, **Delete…**
(Destructive). DaemonSets: Restart, Delete. Jobs / CronJobs: Delete only. Deployment rows open
Deployment detail; other kinds open View YAML in a dialog (no detail page in this spec).

**Scale dialog**: `MudNumericField` with steppers; current / ready replicas; warns when an HPA
targets the workload ("an HPA manages replicas — this change will be overwritten"); a resource hint
"+2 · needs 1 GiB more requests" (per-pod requests × delta); "Audited as k8s.deployment.scale".

## 7. Deployment detail (3c)

`GetDeploymentDetailQueryHandler(ns, name)`: the Deployment, its ReplicaSets (ownerReference), their
pods (ownerReference), Services whose selector matches the pod template labels with endpoint counts
(`EndpointSlice`), and events for the Deployment, its ReplicaSets and its pods (last 1 h, newest 20).

- Header: breadcrumbs (cluster / namespace / name), kind chip, PROD chip; YAML (Safe), Scale,
  Rollout restart (Mutating); menu → Delete… (Destructive).
- **Stuck rollout** alert when `Progressing=False` with reason `ProgressDeadlineExceeded`: names
  the new revision, how long it has been progressing, how many of its pods are crash-looping and
  why, which revision still serves pods, and a **Rollback to N** button (N = the newest older
  revision with ready pods).
- Facts grid: Ready / desired, strategy (maxSurge / maxUnavailable), image (and previous revision's
  image), resources per pod (requests / limits; "OOMKilled at <limit>" when a pod's last
  termination was OOMKilled), selector, Service and endpoint count, age, revision count.
- **Relationship strip**: per ReplicaSet (newest first) a card — revision, current/old, image,
  desired, ready, age — and its pods as `PhaseChip`s (running / pending / crash loop / terminating)
  with restart counts, each linking to Pod detail. ReplicaSets with 0 desired collapse into one
  "N older · rev a–b · kept by revisionHistoryLimit" card.
- Conditions table; Events list.
- **Rollout history** (`RolloutHistory`, pure, unit-tested): one row per ReplicaSet revision
  (`deployment.kubernetes.io/revision`), image, age, `kubernetes.io/change-cause` when present;
  current revision marked (and "stuck" when the alert applies); older revisions with a template get
  **Rollback**.

## 8. Pod detail (3d)

`GetPodDetailQueryHandler(ns, name)`: the pod, its node (Ready, memory pressure), owner chain (pod →
ReplicaSet → Deployment, with revision), events (involved object = the pod).

- Header: breadcrumbs (cluster / namespace / owner / pod), PROD chip; YAML; menu → Copy pod name,
  Download logs (all containers) (Safe), divider, **Delete pod…** (Destructive).
- Facts grid: phase (CrashLoopBackOff overrides phase when any container is in it) with restart
  count and back-off hint, node, pod IP / host IP, QoS class with requests / limits, owner, service
  account, volume and secret-mount counts.
- **Containers table** (init containers first, labelled): name, image, state (Running / Waiting ·
  reason / Terminated · reason), **last termination as an error chip with exit code + reason** and
  "ran N s" when known, restarts, ready, ports.
- Tabs:
  - **Logs** (`LogViewer`): container select; Current / Previous (crashed) toggle — **defaults to
    Previous when the selected container is crash-looping and has a previous instance**; Follow;
    Timestamps; Tail (100 / 500 / 2000, default 500); client-side search with highlight. Reads via
    `ReadNamespacedPodLogAsync(container, previous, tailLines, timestamps)`. Follow opens
    `follow=true`, reads lines into a ring buffer capped at Tail, renders at most every 250 ms, and
    is cancelled on toggle off, container change and dispose. Follow is unavailable for Previous.
    When the shown instance terminated, the viewer ends with the `ExitCodeInterpreter` line (pure,
    unit-tested): 137 + OOMKilled → "killed by the kernel at the memory limit <limit>"; 137 otherwise
    → SIGKILL; 143 → SIGTERM; 0 → completed; 1/2 → application error; 126/127 → command not
    executable / not found; else "exited with N".
  - **Events** (count in the tab).
  - **Describe** (`YamlDescribe`, unit-tested): the typed pod serialized to YAML with
    `metadata.managedFields` removed, sections foldable, Copy YAML, Expand all. Read-only.
  - **Env** (count): per container, resolved env: literal → value; `configMapKeyRef` / `envFrom
    configMapRef` → value read from the ConfigMap, source chip `cm <name>`; `fieldRef` /
    `resourceFieldRef` → resolved from the pod; **`secretKeyRef` / `envFrom secretRef` → always
    `••••••••` with source chip `secret <name>`; the Secret is never read.** First 20, then "show all".
- **Download logs (all containers)** (`DownloadPodLogsQueryHandler`): current logs of every
  container concatenated with headers, tail 10,000 lines each, via `DotNetStreamReference` +
  `kubernetes.js`. Not audited (a read).

## 9. Nodes, Config, Events

### 9.1 Nodes (3e) — `/p/kubernetes/nodes`

`ListNodesQueryHandler`: nodes plus one cluster-wide pod list (non-terminal pods) summed per node —
never one call per node. Columns: Node (name; instance type · internal IP), Conditions (`Ready` /
`NotReady` / `Unknown` + age; `MemoryPressure` / `DiskPressure` / `PIDPressure` chips only when
true), Roles (from `node-role.kubernetes.io/*`, else `agent`; workload taints as extra text),
Kubelet version, CPU req / alloc and Mem req / alloc as bars (**dimmed for NotReady** — last known
values), Pods, Schedulable (`yes` / `cordoned`), Age. Header summary "12 nodes · 2 pools · 11 ready ·
1 cordoned"; Pool filter from `agentpool` / `eks.amazonaws.com/nodegroup` /
`cloud.google.com/gke-nodepool`, else none.

Row: **Cordon** or **Uncordon** inline (Mutating); menu: View YAML, Show N pods on this node (→ a
dialog listing them, linking to Pod detail), Copy kubectl command (Safe), divider, **Drain node…**
(Destructive).

### 9.2 Config (3f) — `/p/kubernetes/config`

**ConfigMaps** (`ListConfigMapsQueryHandler`): Name, Keys, Used by, Age. "Used by" is computed
(`ConfigMapUsage`, pure, unit-tested) from the pod specs in the namespace — env `configMapKeyRef`,
`envFrom`, volumes and projected volumes — grouped by owning workload, with "env", "volume",
"projected"; no users → `unused` chip. Rows expand (`MudTable` row detail) into a key/value grid:
values over 200 chars or multi-line collapse to "N lines · expand" / "{…} 1.8 KB · expand". Row
actions: Copy as YAML (Safe), **Edit…** (Mutating). Binary data keys show size only.

**Edit ConfigMap** dialog: a key/value editor (add / rename / remove keys, multi-line values). Saves
with `ReplaceNamespacedConfigMapAsync` carrying the `resourceVersion` it was opened with; a 409
returns "changed since you opened it — reload and re-apply". Audit records key names added /
changed / removed, never values. The mockup's "changed 2d ago by a.hassan (was 5000)" is **dropped**:
Kubernetes keeps no such history.

**Secrets** (`ListSecretsQueryHandler`), with a lock callout: "Secret values are never shown — the
console reads key names, type and age only." Columns: Name, Type, Keys (chips), Age; `kubernetes.io/tls`
rows add "expires in N d" (warning < 30 d, error < 7 d or expired).

`KubernetesOperations.ListSecretsAsync` is the only code that touches Secret data: it lists
`V1Secret` (a list returns data even without `get`), and **inside the method** maps each to
`SecretSummary(Name, Namespace, Type, KeyNames, CreatedAt, TlsNotAfter?)`. For `kubernetes.io/tls`
it parses `tls.crt` with `X509Certificate2` to read `NotAfter` and disposes it; no other value is
read. The raw list is not cached, logged or returned. A unit test asserts `SecretSummary` has no
member that can carry a value; the k3s test asserts a known secret value appears nowhere in the
returned object's serialized form.

### 9.3 Events (3i) — `/p/kubernetes/events`

`ListEventsQueryHandler(namespaces, window, type, kind?, search?)` over `events.k8s.io/v1`
(`deprecatedCount` / `series.count` folded into one count; `regarding` = involved object; reporting
controller on the right). Toolbar: All / Warning / Normal toggle with live counts, Kind select,
window (1 h / 6 h / 24 h — events usually expire after 1 h, the page says so), search over reason
and message, Live toggle. Group by **Involved object** (default) / **Reason** / **None**
(`EventGrouping`, pure, unit-tested): `MudExpansionPanels`, newest group first, header shows kind,
namespace, name, the top reasons, warning count and "last N m"; first 3 groups expanded. Rows: age,
reason, message, count chip, reporter. Footer: "Events are the cluster's own… The console doesn't
store them. Read-only page · nothing here is audited".

## 10. Actions

Every action is a `…CommandHandler` (`IScopedService`) that writes one audit row
`k8s.<kind>.<verb>` with connection, namespace, name and options, on success and on failure
(`(failed)` + the friendly reason). `IsProd` comes from a server-side `IConnectionProvider` lookup by
connection id. Destructive actions go through `IConfirmationService.ConfirmAsync(ConfirmationRequest)`
— typed name on PROD, two-button otherwise; Mutating actions use a plain dialog (no typing).

| Action | Risk | Mechanism | Audit |
|---|---|---|---|
| Scale (Deployment / StatefulSet) | Mutating | patch `…/scale` `spec.replicas` | `k8s.deployment.scale` / `k8s.statefulset.scale` (from → to) |
| Rollout restart (Deployment / StatefulSet / DaemonSet) | Mutating | strategic-merge patch of template annotation `kubectl.kubernetes.io/restartedAt` | `k8s.<kind>.restart` |
| Rollback to revision N | Mutating | patch the Deployment's `spec.template` to the revision's ReplicaSet template (minus `pod-template-hash`), as `kubectl rollout undo` | `k8s.deployment.rollback` (from → to) |
| Cordon / Uncordon | Mutating | patch `spec.unschedulable` | `k8s.node.cordon` / `k8s.node.uncordon` |
| Edit ConfigMap | Mutating | replace with `resourceVersion` | `k8s.configmap.update` (key names only) |
| Delete pod | Destructive | delete, default grace period | `k8s.pod.delete` |
| Delete workload | Destructive | delete, `propagationPolicy=Background` | `k8s.<kind>.delete` |
| Drain node | Destructive | §10.1 | `k8s.node.drain` (options, evicted / failed pod list) |

### 10.1 Drain (3h)

- **Preview** (`PreviewDrainQueryHandler`, not audited): pods on the node grouped by `DrainPlanner`
  (pure, unit-tested) into **Blocked by PDB** (a matching PDB with `status.disruptionsAllowed == 0`;
  listed first, with the PDB's rule and current status), **Will evict** (with owner and "1 of N
  reschedules" / "brief outage" when the owner has 1 replica), **DaemonSet (ignored)**, and
  **unmanaged pods** (no owner — blocked unless force). Also flags pods with `emptyDir` volumes.
  Summary tiles: Will evict, DaemonSet (ignored), Blocked by PDB, Est. time (evictable pods × grace
  period, capped by parallelism 5, rounded).
- **Dialog**: the preview, options (Delete emptyDir data; Force past PDBs — names the pods; Grace
  period 30 s default), typed node name on PROD, buttons **Cancel / Cordon only / Drain node**.
  "Cordon only" runs the Mutating cordon command.
- **Run** (`DrainNodeCommandHandler`): cordon, then for each non-DaemonSet pod create an Eviction
  (`policy/v1`) with the grace period, 5 at a time; a 429 (PDB) is retried every 5 s until the
  overall timeout (5 min) unless Force, in which case blocked pods are deleted directly. Refuses pods
  with emptyDir unless that option is set (reported, not evicted). Returns a per-pod result
  (evicted / deleted / blocked / failed + reason) shown in the dialog as it streams
  (`IProgress<DrainProgress>`). Runs inside the request; no background job; cancelled on dispose
  (the node stays cordoned and the audit row says "cancelled after N of M").

## 11. RBAC gating (3g) and refresh

**`AccessProbe`** (`Access/`): on page load, `SelfSubjectRulesReview` per selected namespace plus
`SelfSubjectAccessReview` for cluster-scoped verbs (`nodes` patch, `pods/eviction` create, `nodes`
list). Results cached in memory (`IMemoryCache`) per connection + namespace for 5 min; Refresh
clears it. Produces `K8sPermissions` with `Can(resource, verb, ns?)`. Pure evaluation of rules
(wildcards, resourceNames ignored → treated as not granted for list-level actions) is unit-tested.

- A control whose verb is missing is **not rendered** (not disabled).
- The page header shows `RbacChip` "read-only (RBAC)" (or "limited (RBAC)") with a tooltip naming the
  missing verbs for this page, and an info `MudAlert` once per page: connected as `<identity>`,
  missing verbs, "Reads work normally."
- A row menu whose destructive group is empty ends with a one-line note
  ("Scale, restart and delete need more permissions").
- `SelfSubjectRulesReview` can be incomplete (`incomplete: true`, e.g. webhook authorizers): then
  controls render, and the server's 403 is the backstop.
- Command handlers never trust the UI: a 403 becomes "your account can't <verb> <resource> in
  <ns>" from `FriendlyKubernetesError`.

**Refresh**: Overview and Events (when Live) re-query every 5 s; Workloads, Nodes and Deployment /
Pod detail every 10 s; Config does not auto-refresh. Timers pause while a dialog is open or the tab
is hidden (`visibilitychange` via `kubernetes.js`) and stop on dispose. A failed poll keeps the last
data and shows the stale state instead of clearing the page.

## 12. Errors

`FriendlyKubernetesError` maps exceptions to one sentence; the full exception is logged
server-side through `FriendlyError`:

- `HttpOperationException`: 401 → "credentials were rejected or have expired"; 403 → "your account
  can't <verb> <resource>" (parsed from the `Status` message); 404 → "<kind> <name> no longer
  exists — refresh"; 409 → "it changed since you loaded it"; 422 → the `Status` message's field
  causes; 429 on eviction → "blocked by PodDisruptionBudget <name>"; 5xx → "the API server returned
  an error".
- TLS validation failure → "the server's certificate isn't trusted — add the CA to the kubeconfig".
- DNS / connection refused / timeout → "can't reach <host>".
- `KubeConfigException` and validator errors surface at save time, never at page load.

Errors show in `K8sErrorAlert` (sentence + collapsible capped detail line), as Postgres's
`PgErrorAlert`.

## 13. Testing and local stack

**Unit tests** (no network): `KubeconfigValidator` (each accepted / rejected shape),
`KubeconfigSettings` round-trip, `AttentionRules`, `K8sFormat` (quantities `500m`, `512Mi`, `1.5Gi`;
ages), `ExitCodeInterpreter`, `RolloutHistory`, `DrainPlanner`, `ConfigMapUsage`, `EventGrouping`,
`K8sPermissions` rule evaluation, `YamlDescribe` (managedFields removed), `SecretSummary` shape,
`FriendlyKubernetesError`. Handlers with `IKubernetesOperations` substituted: audit rows on success
and failure, typed confirm on PROD sourced server-side, refusal when confirm is declined. bUnit:
RBAC-hidden controls, stale Overview, Logs defaulting to Previous for a crash-looping container, Env
masking, drain dialog grouping and typed-confirm gating.

**Operations tests** against `Testcontainers.K3s` behind `[DockerFact]`, one shared container per
test class: list workloads across namespaces, scale, restart annotation, rollback to a previous
revision, cordon / uncordon, eviction with a PDB that blocks then allows, log tail and `previous`
after a forced crash, ConfigMap replace with a stale `resourceVersion` (409), secret listing returns
no values, rules review with a ServiceAccount bound to `view`.

**Compose profile `kubernetes`**:

- `k3s` service (`rancher/k3s:v1.30.x-k3s1`, privileged, port 6443) started with
  `--kube-apiserver-arg=token-auth-file=/seed/tokens.csv` so the demo credentials are **static and
  checked in** (two local-only tokens for users `sbconsole-admin` and `sbconsole-viewer`), and
  `--tls-san k3s`.
- `k3s-seed` one-shot (`bitnami/kubectl`) waits for the API, then applies `docker/kubernetes/seed/*.yaml`:
  namespaces checkout, payments, platform, monitoring; ClusterRoleBindings (`sbconsole-admin` →
  cluster-admin, `sbconsole-viewer` → view); `payment-gateway` with a stuck rollout (revision 1
  healthy, revision 2 a memory hog with a small limit that OOM-crash-loops, `progressDeadlineSeconds:
  60`), healthy Deployments, a StatefulSet with a PDB `maxUnavailable: 0`, a DaemonSet, a failing Job
  (`backoffLimit: 1`), a CronJob, an unschedulable pod (requests 999Gi), ConfigMaps (one unused),
  Opaque / TLS / dockerconfigjson Secrets, an HPA.
- Demo connections: two entries in `docker/sqlite/demo-connections.json` (the one file for every
  plugin) — `k3s-local (admin)` and `k3s-local (viewer)`, kind `kubernetes`, secret = a kubeconfig
  with `server: https://k3s:6443`, `insecure-skip-tls-verify: true` and the static token.
- `docker/README.md`: profile, what the seed demonstrates, what single-node k3s can't (NotReady node,
  PDB-blocked drain across nodes — covered by handler tests), and how to mint a ServiceAccount
  token for a real cluster.

## 14. Build phases (one plan, in order)

1. Project scaffold, registration, `KubeconfigSettings` / `KubeconfigValidator`, connection form,
   `TestConnectionAsync`, `FriendlyKubernetesError`, k3s Testcontainer fixture, compose profile + seed.
2. Namespace picker, `AccessProbe` / `K8sPermissions` / `RbacChip`, shared components, Overview,
   dashboard hooks.
3. Workloads list (all tabs), Scale, Restart, Delete.
4. Deployment detail, rollout history, Rollback.
5. Pod detail: containers, Logs (current / previous / follow / download), Events, Describe, Env,
   Delete pod.
6. Nodes, Cordon / Uncordon, drain preview and Drain.
7. Config: ConfigMaps (expand, Edit), Secrets metadata and TLS expiry.
8. Events page.
9. Docs: `docs/design.md` §19, `docker/README.md`, `README.md`, `CLAUDE.md` plugin list and testing
   exception.

Gate per phase: `dotnet build -warnaserror` and `dotnet test` green.
