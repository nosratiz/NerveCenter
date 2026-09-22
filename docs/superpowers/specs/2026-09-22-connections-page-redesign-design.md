# Connections page redesign — structured per-plugin fields — Design

Status: draft, 2026-09-22.

## 1. Context

Every plugin's connection today is one opaque secret string, hand-typed into
a single flat textbox in `AddEditConnectionDialog.razor`
(`src/SbConsole.Web/Components/Connections/`) — a deliberate scope cut made
explicitly for both Kafka and, most recently, AWS (`docs/superpowers/specs/
2026-09-21-aws-sqs-plugin-design.md` §2, §9). That spec called out the
mockup's connection-form drawer (region dropdown, auth-mode segmented
control, Advanced disclosure, inline rich Test-result panel) as "a
materially larger, separately-scoped change, not an AWS-plugin-internal
one." This spec is that change.

Source design material: `~/Desktop/UI mockups for NerveCenter/SbConsole
AWS.dc.html`, screen `1a` (connection form drawer, all three auth-mode
variants, and the three Test-connection outcomes).

This spec is host/SDK-scoped, not AWS-scoped: it introduces optional SDK
contracts any plugin can adopt, and AWS is the first (and, for now, only)
adopter. Service Bus and Kafka are unaffected — every new `IPlugin` member
has a no-op default, so their code does not change at all. SNS/Topics is out
of scope entirely (a separate, later plan) and does not touch anything here.

## 2. Current state (what this replaces)

- `Connections.razor` (`/connections`): a `MudTable` list plus row actions
  (Test, Edit, Delete); "Edit"/"+ New connection" opens
  `AddEditConnectionDialog.razor` as a **modal** (`MudDialog`).
- The dialog: `Name`, `Kind` (disabled on edit), one flat password textbox
  labelled "Connection string" (create) / "Replace connection string (leave
  blank to keep)" (edit), a tag editor. No per-plugin field, no
  Test-connection call inside the dialog.
- `IPlugin.TestConnectionAsync(string secret, ...)` returns
  `ConnectionTestResult(bool Success, string? ErrorMessage)` — a single pass/
  fail plus one optional string. Test only runs against an already-saved
  connection, from the list page's row action; the dialog itself never tests.
- `ConnectionInfo`/`Connection` carry `Name`, `Kind`, `Tags` (a freeform
  string list — `"prod"` is a magic-string tag, not a dedicated field), and
  the persisted last-test result. No region, auth-mode, or other structured
  field exists anywhere at the host level.

## 3. SDK contract additions (`SbConsole.Sdk`)

Three new `IPlugin` members, each defaulted so existing plugins compile and
behave unchanged:

```csharp
// Default: no custom form -- host falls back to today's flat textbox.
Type? ConnectionFormComponentType => null;

// Default: nothing to show -- host renders no summary chips for this plugin's rows.
IReadOnlyDictionary<string, string> GetConnectionSummary(string secret) =>
    ImmutableDictionary<string, string>.Empty;
```

A new base class plugins implement their custom form component against:

```csharp
public abstract class ConnectionFormComponentBase : ComponentBase
{
    [Parameter] public string? InitialSecret { get; set; }
    [Parameter] public EventCallback<string> SecretChanged { get; set; }
}
```

The component is solely responsible for turning `InitialSecret` into fields
on `OnParametersSet`/`OnInitialized` and serializing any edit straight back
to a secret string via `SecretChanged.InvokeAsync(...)`. **The host never
parses or builds the `key=value;` format** — it treats the secret as an
opaque string exactly as it does today, whether it came from the fallback
textbox or a plugin's own component. This mirrors the existing rule that no
plugin type is referenced by name anywhere in the host: `Connections.razor`
only ever sees `IPlugin.ConnectionFormComponentType` (a `Type`, resolved via
Blazor's `DynamicComponent`), never `AwsConnectionFields` by name.

`ConnectionTestResult` gains optional fields, all defaulted so every
existing two-argument call site (`new ConnectionTestResult(success,
error)`) keeps compiling and rendering exactly as before:

```csharp
public sealed record ConnectionTestResult(
    bool Success,
    string? ErrorMessage = null,
    string? Identity = null,
    IReadOnlyList<ConnectionCheck>? Checks = null);

public sealed record ConnectionCheck(string Label, ConnectionCheckStatus Status, string? Detail = null);

public enum ConnectionCheckStatus { Passed, Failed }
```

The mockup's three Test-connection outcomes fall out of this one shape with
no extra outcome enum:

- **Success** — `Success: true`, `Identity` set (e.g. account id or caller
  ARN), `Checks` all `Passed` (e.g. "Queues visible" / Passed / "12").
- **Invalid credentials** — `Success: false`, `ErrorMessage` set (the raw
  friendly-mapped AWS error), `Identity`/`Checks` null.
- **Valid, under-permissioned** — `Success: true`, `Identity` set, `Checks`
  containing at least one `Failed` entry (e.g. "Topics visible" / Failed /
  "sns:ListTopics denied").

**No prediction of denied mutating actions.** `Checks` only ever reflects
safe, already-necessary read probes (the same calls a plugin needs to make
anyway, e.g. `ListQueues`) run at Test time. Detecting whether a specific
mutating action (`sqs:SendMessage`, `sqs:PurgeQueue`, `sns:Publish`, ...)
will be denied would require either performing it (unacceptable side
effects) or IAM Policy Simulator (`iam:SimulatePrincipalPolicy`), which
needs an extra permission most connections won't have and still can't
account for SCPs or resource policies. Rejected — see §7.

Host rendering rule: when `Identity` and `Checks` are both null, render the
plain single-line message exactly as today (Service Bus, Kafka). When
either is populated, render the richer identity-and-checks panel.

## 4. Host changes (`SbConsole.Web`)

- `Connections.razor` becomes a two-pane layout: the existing table on the
  left, a `MudDrawer` (`Anchor.End`, `DrawerVariant.Persistent`) as the
  editor on the right, replacing `AddEditConnectionDialog` as a modal. Opens
  on "+ New connection" or a row's "Edit"; closes on Save/Cancel. The table
  itself is otherwise unchanged (same columns, same Test/Delete row
  actions).
- Inside the drawer: `Name`/`Tag` row and `Kind` selector stay exactly as
  they are today (`Kind` still disabled on edit). Below them, either:
  - the plugin's `ConnectionFormComponentType`, hosted via
    `<DynamicComponent Type="..." Parameters="...">` with `InitialSecret`/
    `SecretChanged` wired to the drawer's local secret string; or
  - the existing flat secret textbox, when the selected plugin's
    `ConnectionFormComponentType` is null.
- **Test connection moves into the drawer** and works before saving:
  - Editing an **existing** connection: still routes through
    `TestConnectionCommandHandler.HandleAsync` exactly as today (preserves
    `LastTestSucceeded`/`LastTestedAt`/`LastTestError` persistence and the
    `connection.test` audit row).
  - A **new, unsaved** connection: calls `plugin.TestConnectionAsync(secret,
    ct)` directly. Nothing is persisted and no audit row is written — there
    is no connection row yet to attach either to. This is a new code path in
    the drawer, not a new handler; it calls the same `IPlugin` method the
    persisted path already calls.
- The list table gains one more column: each row renders its connection's
  `Summary` (see below) as small chips, generically — the host has no
  knowledge that a chip labelled "Region" is AWS-specific; it renders
  whatever key/value pairs the owning plugin chose to surface. Service
  Bus/Kafka rows render no chips (empty `Summary`).
- `Connection` entity (`SbConsole.Core.Data.Entities`) gains one new
  nullable `string? SummaryJson` column (EF Core migration, additive/
  nullable — no backfill needed, existing rows just show no chips until
  next save). `ConnectionInfo` gains `IReadOnlyDictionary<string, string>
  Summary` (empty when `SummaryJson` is null), deserialized by
  `EfConnectionProvider`/`ListConnectionsQueryHandler`.
- `CreateConnectionCommandHandler`/`UpdateConnectionCommandHandler` each
  gain one line: after resolving the target `IPlugin`, call
  `plugin.GetConnectionSummary(secret)` with the plaintext secret they
  already have in hand before encryption (no extra decrypt, no extra AWS
  call — this is a pure, synchronous, already-in-memory-data operation) and
  store the serialized result on `SummaryJson`.

## 5. AWS plugin changes

- New `Client/AwsConnectionFields.razor` (inherits
  `ConnectionFormComponentBase`), replacing the flat textbox for AWS
  connections only:
  - Region: a searchable, grouped dropdown (static region list grouped by
    geography, matching the mockup) — not a free-text field.
  - Auth mode: a three-way segmented control (Access keys / Assume role /
    Default chain) that swaps the visible field set:
    - Access keys: Access key ID, Secret access key, optional Session token.
    - Assume role: Role ARN, optional External ID, optional Session name
      (with the mockup's note that base credentials for the `sts:AssumeRole`
      call come from the default chain).
    - Default chain: no fields — a short explanation of the three-link
      resolution order, matching the mockup exactly.
  - Advanced (collapsed by default): custom endpoint URL, a path-style
    addressing toggle, and a warning banner when a custom endpoint is set on
    a `prod`-tagged connection.
  - On any field change, serializes the full field set to the existing flat
    `mode=...;region=...;...` string via a new `AwsConfigParser.Serialize
    (IReadOnlyDictionary<string, string>)` — the exact inverse of the
    existing `Parse`, added alongside it — and calls `SecretChanged`. On
    init, calls `AwsConfigParser.Parse(InitialSecret)` to hydrate fields.
    Round-trip (`Parse` then `Serialize` reproduces the same effective
    config) is a direct unit-test target (§6).
- `AwsPlugin.GetConnectionSummary(secret)`: parses the secret and returns
  `{"Region": parsed.GetValueOrDefault("region", "?")}` — the same
  allowlist reasoning as the existing `SafeEcho` (never echoes credential
  fields), just persisted once at save time instead of computed on every
  page load.
- `SqsOperations.TestConnectionAsync` restructured to populate the new
  `ConnectionTestResult` fields instead of folding everything into
  `ErrorMessage`:
  - `Identity` ← the account id from the existing `GetCallerIdentity` call.
  - `Checks` ← a single `ConnectionCheck("Queues visible", ...)` from the
    existing `ListQueues` probe (`Passed` with the count as `Detail`, or
    `Failed` with the friendly-mapped denial reason).
  - The SNS "Topics visible" check is **not** added here — `ISnsOperations`
    doesn't exist yet; it's added in the SNS spec once it does, as a second
    entry in the same `Checks` list.
  - Invalid-credentials mapping (`GetCallerIdentity` throwing) is unchanged
    from the existing `FriendlyAwsError`-routed behavior — still
    `Success: false`, `ErrorMessage` set.

## 6. Testing

- `AwsConfigParser.Serialize`: unit tests mirroring the existing `Parse`
  coverage — each auth mode's field set, optional fields present/absent,
  round-trip with `Parse` (parse→serialize→parse produces an equal
  dictionary), confirms credential fields are never dropped or reordered
  unexpectedly.
- `AwsConnectionFields.razor` (bUnit): auth-mode segmented control swaps the
  correct field set, editing any field raises `SecretChanged` with a
  correctly serialized string, Advanced starts collapsed, the prod+custom-
  endpoint warning appears only when both conditions hold.
- `Connections.razor` (bUnit): drawer opens/closes on New/Edit/Cancel, hosts
  the fallback textbox for a plugin with `ConnectionFormComponentType ==
  null` (Service Bus/Kafka — existing behavior, regression-tested), hosts a
  `DynamicComponent` for AWS, renders `Summary` chips generically from
  whatever `ConnectionInfo.Summary` contains (a fake plugin substitute in
  the test, not AWS specifically, to prove genericity), the two Test-
  connection code paths (pre-save direct call vs. post-save handler call).
- `CreateConnectionCommandHandler`/`UpdateConnectionCommandHandler` handler
  tests: `GetConnectionSummary` is called with the plaintext secret and its
  result is persisted to `SummaryJson`.
- `ConnectionTestResult`/`ConnectionCheck`: existing Service Bus/Kafka
  `TestConnectionAsync` tests need no changes (this is the regression bar —
  their two-argument `new ConnectionTestResult(success, error)` calls must
  keep compiling and their rendering must stay pixel-identical to today).
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every
  commit.

## 7. Rejected alternatives

- **Declarative field-descriptor schema** (`IPlugin` returns typed field
  descriptors; host renders every field generically with no plugin Razor
  code). Rejected: the mockup's conditional auth-mode field groups and
  grouped/searchable region dropdown push a descriptor language to be
  nearly as complex as a real component, for no code-reuse benefit over the
  chosen approach — and it would be new machinery to design, test, and
  maintain, versus reusing the existing "plugins own their own Razor"
  pattern plugin pages already establish.
- **Hybrid** (generic descriptors plus a plugin-owned escape-hatch
  fragment). Rejected for the same reason plus the cost of maintaining two
  mechanisms instead of one.
- **IAM Policy Simulator** for predicting denied mutating actions (§3).
  Rejected: requires `iam:SimulatePrincipalPolicy` on the connection's own
  principal (most won't have it), adds an `AWSSDK.IdentityManagement`
  dependency, and still can't fully model SCPs or resource policies — a
  confident-looking answer that can be wrong is worse than no answer here.

## 8. Out of scope (this plan)

- **SNS/Topics** entirely — separate plan; this spec only lays SDK
  groundwork (the `Checks` list shape) that plan will add a second entry to.
- **Persisted "denied actions" / degraded read-only enforcement** — the
  mockup's "Save read-only" button is equivalent to "Save"; no
  `HiddenActions` concept is added to `Connection`/`ConnectionInfo`, and no
  page conditionally hides a button based on a past Test result. Every
  action stays visible everywhere, failing gracefully via the existing
  `FriendlyError`-wrapped path if IAM denies it at call time — the same
  posture every plugin already has today.
- **IAM Policy Simulator integration** (§7).
- **A host-level, first-class `Region` field** on `ConnectionInfo`/
  `Connection` — `Summary` is a generic string/string dictionary the host
  renders without understanding; there is no dedicated `Region` column or
  property anywhere in the host or SDK.
- **Service Bus/Kafka adopting `ConnectionFormComponentType` or
  `GetConnectionSummary`** — both are viable candidates for later, but
  neither plugin's connection secret has enough field complexity today to
  justify the change; out of scope here.
