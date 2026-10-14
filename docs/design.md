# SbConsole — Design (v1)

Status: approved 2026-09-09. Extended 2026-09-10 (Core UI: §3 SDK v1.1, §4
new handlers, §5 screen inventory). Extended 2026-09-12 (Service Bus plugin,
Queues: §3 SDK v1.2, §4 new handler + schema, §5 routing fix, §6 rewritten,
now §6.1). Extended 2026-09-13 (Service Bus plugin, Topics & Subscriptions:
§3 SDK v1.3, §5 NavMenu badges, §6.2 rewritten against the actual UI
mockups (`~/Desktop/UI mockups for NerveCenter`) after the first pass was
drafted without consulting them, §6.3 new Dead-letter overview, §8 updated).
Extended 2026-09-14 (Glass shell: §10 rewritten for the app-bar/drawer
gradient-glass treatment). Extended 2026-09-21 (AWS plugin, Queues: §6.7 new
plugin section). Extended 2026-09-23 (AWS SNS Topics: §6.7.1; Connections page
redesign: §3 optional `IPlugin` form/summary hooks, new §11). Extended
2026-09-24 (AWS completion pass: new §6.7.2; pointers appended to §6.7's and
§6.7.1's deferred-scope paragraphs; §8 AWS testing bullet updated).
SDK version: `SbConsole.Sdk` 1.3.0 — see §3 for the 2026-09-10 additions
(`IPlugin.ConnectionKind`/`ConnectionKindDisplayName`/`Contribution`,
`IConfirmationService`), the 2026-09-12 additions/removal
(`IPlugin.TestConnectionAsync`, `ConnectionTestResult`; `RootComponent`
removed), and the 2026-09-13 addition (`IPlugin.GetNavBadgeAsync`, default-
implemented so no other plugin is forced to implement it) — see §3.
`IServiceBusOperations`'s Topics & Subscriptions / Dead-letter additions
live entirely inside the plugin project and do not touch the SDK.

## 1. What it is

A team-hosted Blazor web app that acts as a **plugin host platform**. The host
provides the chassis — shell UI, auth, saved connections, encrypted secrets,
settings, audit log, plugin storage. Plugins provide domain features.
Plugin #1: full Azure Service Bus management.

Deployment model: one shared instance per team (Docker/VM), single shared
admin login, API key for automation. Per-user accounts are a later concern;
the audit schema keeps an `Actor` column from day one.

Local development: `docker-compose.yml` at the repo root runs the brokers the
plugins talk to — a single-node KRaft Kafka with seeded topics and a Kafka UI
by default, the Azure Service Bus emulator under `--profile servicebus`, and
the app itself (built from `Dockerfile`) under `--profile app`. See
`docker/README.md` for the connection secrets to paste into the app.

## 2. Solution layout

```
SbConsole.sln
├── src/
│   ├── SbConsole.Sdk/                 # The contract. Zero dependencies beyond BCL + Microsoft.Extensions.*.Abstractions.
│   ├── SbConsole.Core/                # Host services: handlers, EF Core (SQLite/WAL), auth, audit, secrets, settings.
│   ├── SbConsole.Web/                 # Blazor Web App (Interactive Server), MudBlazor shell, Minimal APIs.
│   └── SbConsole.Plugins.ServiceBus/  # Plugin #1. References Sdk ONLY.
└── tests/
    ├── SbConsole.Core.Tests/          # xUnit + FluentAssertions + NSubstitute
    ├── SbConsole.Web.Tests/           # bUnit component tests
    ├── SbConsole.Plugins.ServiceBus.Tests/
    └── SbConsole.IntegrationTests/    # Testcontainers + Service Bus emulator
```

Plugin loading is **compile-time** in v1: `SbConsole.Web` references the
plugin project and registers it at startup. The SDK contract is written as if
host and plugin were compiled by strangers — no plugin type is referenced by
name in Core, everything flows through SDK interfaces — so runtime
`AssemblyLoadContext` loading can be added later without changing plugin code.

Dependency rule: plugins depend only on `SbConsole.Sdk`. Core depends on Sdk.
Nothing depends on plugin assemblies except the Web host's registration line.

## 3. The SDK contract (`SbConsole.Sdk`)

- `IPlugin` — identity (`Id`, `DisplayName`, `Version`),
  `ConfigureServices(IServiceCollection)`, declares nav items. **(v1.1)** also
  declares `ConnectionKind` (matches `Connection.Kind`, e.g.
  `"azure-servicebus"`), `ConnectionKindDisplayName` (shown in the host's Add
  Connection "Kind" dropdown), and `Contribution` (a `PluginContribution(int
  PageCount, int ActionCount)` record, shown on the host's Plugins page). All
  three are static/declarative properties — no plugin method call, no async
  round-trip; the host reads them straight off `PluginRegistry.Plugins`.
  **(v1.2)** `RootComponent` — Plan 1's single "root component per plugin"
  property — is **removed**. It was never actually wired to anything in the
  host (no route ever rendered it), and it can't express a multi-page plugin
  anyway. Superseded by real Blazor routing across assemblies — see §5.
  **(v1.2)** also adds `Task<ConnectionTestResult> TestConnectionAsync(string
  secret, CancellationToken ct = default)` — every plugin that declares a
  `ConnectionKind` can verify a saved connection actually works, using
  whatever protocol that connection kind speaks. `ConnectionTestResult` is
  `(bool Success, string? ErrorMessage)`.
- `IPluginStore` — namespaced key/value + JSON document storage, scoped per
  plugin ID. The only persistence a plugin gets. Plugins never touch the
  DbContext.
- `IConnectionProvider` — read access to saved connections of the plugin's
  declared connection kind (e.g. `"azure-servicebus"`), secrets decrypted
  just-in-time, never exposed in UI models.
- `PluginAction` metadata — `ActionRisk` enum (`Safe`, `Mutating`,
  `Destructive`) the host uses to enforce confirmation rules.
- `IAuditScope` — plugins report what they did; the host writes the audit row.
  **Found while building §6**: like `RootComponent`, this had no
  implementation anywhere and was never registered in DI since Plan 1 — a
  contract with no consumer to prove it out until now. **(v1.2)** ships
  `EfAuditScope : IAuditScope` in Core, bridging to the existing
  `IAuditWriter` (Plan 1 Task 7). Actor resolution differs from the
  Razor-component-facing `ActorResolver` (Plan 2) because `IAuditScope` is
  injected into plain plugin handler classes, not components with an
  `AuthenticationState` cascading parameter: `EfAuditScope` instead injects
  `AuthenticationStateProvider` directly and calls
  `GetAuthenticationStateAsync()` itself when `RecordAsync` is called. Kept
  as a separate, small duplication of the "fall back to admin" logic rather
  than forcing a shared abstraction between two different injection
  contexts.
- **(v1.1)** `IConfirmationService` —
  `Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default)`.
  How host code (delete connection) and plugin code (purge queue) both
  trigger the same typed-confirmation dialog without the plugin ever
  referencing `SbConsole.Web`: the interface lives in Sdk, the MudBlazor
  dialog component lives in Web and is registered in DI, plugins consume it
  by injection like any other Sdk service. Typed confirmation (type the
  target name to proceed) applies only when `isProd` is true and the
  Settings "require typed confirmation on prod-tagged targets" toggle is on;
  otherwise it degrades to a plain two-button confirm.

Connection reachability testing was deferred past v1.1 pending "the plan
that gives a plugin something real to test against" — that plan is the
Service Bus Queues plan (§6), so `TestConnectionAsync` above closes that gap.

**(v1.3)** adds `Task<int?> GetNavBadgeAsync(string navItemHref, string
connectionString, CancellationToken ct = default)` to `IPlugin`, with a
default interface implementation (`=> Task.FromResult<int?>(null)`) so every
plugin written before this addition — and any future plugin with nothing to
badge — needs no change at all. A plugin overrides it to answer for the
`Href`s it cares about (matched by exact string) and returns `null` for
everything else; the host (§5) calls it once per connection of the plugin's
kind and sums non-null results into one badge per nav item. `PluginNavItem`
itself is unchanged — the badge is computed, not stored.

## 4. Host core (`SbConsole.Core`)

- **Handlers**: plain `XxxQueryHandler` / `XxxCommandHandler` classes,
  DI-registered. No MediatR. Commands write audit rows via `IAuditWriter`;
  queries never write. **(v1.1)** adds `UpdateConnectionCommandHandler`
  (rename/re-tag/replace-secret — v1.0 only had Create/List/Delete),
  `ListAuditEntriesQueryHandler` (paged, filterable by date range, actor,
  risk, target text — v1.0 could only write audit rows, never read them
  back), and `ListPluginsQueryHandler` (joins `PluginRegistry`'s static
  plugin metadata with a live "connections in use" count grouped by
  `Connection.Kind`, for the Plugins page). **(v1.2)** adds
  `TestConnectionCommandHandler` — looks up the connection, decrypts its
  secret, finds the matching `IPlugin` by `Connection.Kind`, calls
  `TestConnectionAsync`, persists the result (see below), and writes an
  audit row (`connection.test`, `ActionRisk.Safe`, `Succeeded` = the test's
  own `Success`) whether the test passed or failed — a test result is
  audit-worthy either way, matching the "Safe ✕ Unauthorized (401)" row the
  wireframes show. This is the one place a query-shaped operation (it
  doesn't mutate the connection's own configured fields) is still modeled as
  a command, because it performs live I/O and writes an audit row — the
  Command/Query split in this codebase is about *DB writes*, and this one
  writes both an audit row and (below) two result columns on the connection.
- **Persistence**: EF Core + SQLite (WAL) at `SBC_DB_PATH`. Tables:
  `AppSettings`, `Connections`, `AuditEntries`, `PluginDocuments`. No
  SQLite-only SQL — PostgreSQL stays a viable later option. **(v1.2)** adds
  three nullable columns to `Connections`: `LastTestSucceeded` (bool?),
  `LastTestedAt` (DateTimeOffset?), `LastTestError` (string?) — all null on
  existing rows (no backfill needed, unlike the Plan 2 `AuditEntry.At`
  migration; these are brand-new columns with no prior data to convert).
  Null `LastTestedAt` is exactly the "Never tested" state.
- **Secrets**: `ISecretProtector` (AES-GCM, key from `SBC_DATA_KEY`) encrypts
  connection payloads before any write. Connection values are never logged and
  never round-tripped to the browser.
- **Connections**: name, kind, tags (including `prod`), encrypted payload.
  The `prod` tag drives destructive-action confirmation.
- **Settings**: env vars for bootstrap only (`SBC_DB_PATH`, `SBC_DATA_KEY`,
  `SBC_ADMIN_PASSWORD`, `SBC_API_KEY`, `SBC_BIND`); everything else is an
  `AppSettings` row read through `ISettings`.
- **Auth**: single shared admin login (cookie session) for the UI;
  `SBC_API_KEY` header auth for the API. Audit actor = `"admin"` (UI) or
  `"api"`.

## 5. Web host (`SbConsole.Web`)

- Blazor Web App, Interactive Server render mode only. No WebAssembly.
- MudBlazor shell (single component library): nav drawer with host sections
  (Dashboard, Connections, Audit, Plugins, Settings) plus plugin-contributed
  nav items under each plugin's heading.
- Plugin pages mount at `/p/{pluginId}/...`. **(v1.2)** Each plugin page
  declares its own `@page` route directly, like any host page — made
  possible by scanning plugin assemblies for routable components:
  `Routes.razor`'s `<Router AdditionalAssemblies="...">` and
  `Program.cs`'s `MapRazorComponents<App>().AddAdditionalAssemblies(...)`
  both take the list from `PluginRegistry.Plugins.Select(p =>
  p.GetType().Assembly).Distinct()`, so a newly-registered plugin's pages
  become routable automatically — no per-plugin host change. Supersedes the
  single-`RootComponent`-per-plugin idea from v1.0/v1.1 (removed, §3), which
  couldn't express more than one page per plugin and was never wired up.
- Destructive actions (`ActionRisk.Destructive`) on prod-tagged connections
  require the typed-confirmation dialog (`IConfirmationService`, §3).
- **(v1.3)** `NavMenu.razor` overlays a live badge next to any plugin nav
  item `GetNavBadgeAsync` (§3) answers for. Computed in a fire-and-forget
  background loop started from `OnInitialized` (never awaited there, so the
  very first render of any page is never blocked on a Service Bus call) that
  refreshes every 60 seconds for as long as the circuit lives — `NavMenu` is
  part of the persistent layout, not re-created per page, so the loop and
  its cached badge values live exactly as long as the browser session does.
  Each tick: for every plugin, list its connections
  (`IConnectionProvider.ListAsync`), decrypt each secret, call
  `GetNavBadgeAsync` per connection per nav item, sum the non-null results,
  and `StateHasChanged()` once. A connection whose secret fails or whose
  plugin call throws is skipped for that tick, not fatal to the others.
- Minimal APIs (no controllers) under `/api/v1`: health, list connections
  (no secrets), list entities, peek, send, resubmit-DLQ, purge. Destructive
  endpoints additionally require `?confirm=<name>`. OpenAPI via
  `Microsoft.AspNetCore.OpenApi`. The UI does not use the API — it calls Core
  handlers directly; the API exists for external automation and grows on
  demand.

### 5.1 Host screens (v1.1)

- **Login** — single card, password field only, no username. Matches v1.0.
- **Dashboard** — triage-first layout: a "Needs attention" section (unreachable
  connections, growing dead-letter backlogs, disabled subscriptions — all
  plugin-reported problems) above a recent-activity feed sourced from real
  audit entries. With zero plugins reporting problems (true until a plugin
  exists to report them), "Needs attention" renders a correct, honest empty
  state rather than fake data — the page doesn't need stubbing to be right.
- **Connections** — list/add/edit/delete. The Add/Edit dialog's "Kind"
  dropdown is populated from `PluginRegistry.Plugins` (`ConnectionKind` /
  `ConnectionKindDisplayName`), not free text. Delete on a prod-tagged
  connection goes through `IConfirmationService`. **(v1.2)** the Status
  column reads from the connection's persisted `LastTestSucceeded`/
  `LastTestedAt`/`LastTestError` (§4): "Never tested" when `LastTestedAt` is
  null, "OK" when the last test succeeded, or the stored error text
  otherwise — plus a "Test" row action that runs
  `TestConnectionCommandHandler` and refreshes the row.
- **Audit** — filterable (date range, actor, risk, target text) paginated
  table over `ListAuditEntriesQueryHandler`. CSV export is out of scope for
  v1.1 (cheap to add later; cut to keep this slice tight).
- **Settings** — instance name, theme (Light/Dark/System via MudBlazor's
  theme provider), the "require typed confirmation on prod-tagged targets"
  toggle (read by `IConfirmationService`'s implementation), session timeout,
  and audit retention (days). The latter two are **persisted but not
  enforced** by any running code yet: session timeout changes take effect
  only after the app restarts (cookie options are configured once at
  startup, not re-read per-request), and audit retention has no purge job
  behind it (that needs a background-job decision out of scope here).
- **Plugins** — **read-only** for v1.1: a table of the compile-time-registered
  plugins (`PluginRegistry`) showing version, `Contribution` summary, and
  live connections-in-use count. No install/enable/disable/registry/remove —
  that's the dynamic-plugin-loading feature explicitly deferred past v1 (§2).

Deferred past v1.1: plugin-contributed dashboard widgets, charts/wallboard
views, audit CSV export, audit-retention enforcement, the dynamic plugin
marketplace (install/enable/disable at runtime). Connection reachability
testing shipped in v1.2 (§3, §4) once the Service Bus plugin (§6) gave it
something real to test against.

## 6. Service Bus plugin (`SbConsole.Plugins.ServiceBus`)

Connection-string auth only in v1. Built in two plans: **Queues** (2026-09-12,
§6.1) — shipped — as the full vertical slice that proves out the plugin
architecture end-to-end on the simplest entity type; **Topics &
Subscriptions** (2026-09-13, §6.2) second, reusing everything Queues built
(the wrapper interface, the message-peek/send/DLQ components, the
confirmation flow).

- **`ServiceBusPlugin : IPlugin`** — `Id`/`ConnectionKind` =
  `"azure-servicebus"`, `DisplayName`/`ConnectionKindDisplayName` =
  `"Azure Service Bus"`. Registered via `AddSbConsolePlugin<ServiceBusPlugin>()`
  in `Program.cs` (§2), which is also what makes its pages routable (§5).
- **`IServiceBusOperations`** — a thin interface between the plugin's
  pages/handlers and the real Azure SDK
  (`Azure.Messaging.ServiceBus`'s `ServiceBusClient` +
  `ServiceBusAdministrationClient`), so unit tests substitute it directly —
  no network call, no Docker. Every method takes the connection string as a
  parameter (not pre-configured at construction) since one instance tests
  and operates against whatever connection the caller names. One real
  implementation, `AzureServiceBusOperations`; `ServiceBusPlugin`'s own
  `TestConnectionAsync` constructs one directly and delegates to it (plugins
  are constructed via a parameterless `new()`, per the
  `AddSbConsolePlugin<TPlugin>()` constraint, so there's no DI container to
  pull one from at that layer). This is the seam integration tests
  (Testcontainers + the official emulator, `SbConsole.IntegrationTests`, §8)
  will eventually test against; **not built yet** — unit tests against a
  substitute of `IServiceBusOperations` are the only test strategy across
  both plans, deferred per the same "prove the shape out first" reasoning as
  the plan split. `AzureServiceBusOperations` itself gets light test
  coverage as a result — its methods can't be meaningfully unit-tested
  without a real or emulated broker, so most of its value is in being a
  substitutable seam for everything else, not in its own test count.
- **Plugin handlers** follow Core's naming convention
  (`XxxQueryHandler`/`XxxCommandHandler`, plain classes, DI-registered via
  the plugin's own `ConfigureServices`) even though nothing in the SDK
  requires it — consistency with the rest of the codebase. Each resolves the
  connection's secret via `IConnectionProvider.GetSecretAsync` (§3), calls
  the matching `IServiceBusOperations` method, and — for commands — reports
  via `IAuditScope.RecordAsync` (§3).
- **Connection reachability** (§3, §4): `ServiceBusPlugin.TestConnectionAsync`
  attempts a lightweight administrative call (e.g. listing queues with a
  small page size) against the given connection string and maps
  Azure SDK exceptions to a plain `ConnectionTestResult` — auth failures,
  unreachable namespace, and malformed connection strings each produce a
  distinct, readable `ErrorMessage` rather than a raw exception message
  (each Azure failure shape verified via decompilation of the exact
  installed SDK version, not assumed); raw SDK exception text never reaches
  the UI or the database — every handler catch site and
  `TestConnectionCommandHandler` route through a shared `FriendlyError`
  helper (`SbConsole.Sdk`) that caps and collapses `ex.Message`, logging the
  full exception server-side. Every real Azure call is bounded: tightened
  `RetryOptions`/`TryTimeout` on both the admin and AMQP clients, plus a
  hard wall-clock cap on the two operations that loop over an unbounded
  number of broker round-trips, so a `Peek`/`Queues`-family page never spins
  forever against an unreachable namespace with no feedback — the busy
  operation disables its own triggering control and shows inline progress
  instead.

### 6.1 Queues (shipped)

List with live counts (active/DLQ/scheduled), create, delete
(`Destructive`); peek (non-destructive, paged); send a message; dead-letter
browse, resubmit (single selection and multi-select, matching the
wireframe's "N selected of M · Resubmit selected"), purge (`Destructive`).

Destructive dead-letter purges on a prod-tagged connection go through
`IConfirmationService`'s typed-for-prod gate; the gate reads `IsProd` from a
server-side `IConnectionProvider` lookup by the connection's id, never from
a client-suppliable URL parameter — a purge link that only carried
`connectionId` (no `isProd`) must still demand the typed prompt on a
prod-tagged connection.

### 6.2 Topics & subscriptions (2026-09-13, rewritten same day)

The first pass of this section (still visible in
`docs/superpowers/plans/2026-09-13-servicebus-topics-subscriptions-plugin.md`'s
original commit) was drafted directly from the Queues plan's own shape —
two drill-down pages — without checking the actual UI mockups
(`~/Desktop/UI mockups for NerveCenter/SbConsole Wireframes.html`, screen
"1h Topics"). That mockup shows a materially different structure, which is
what's documented below instead.

Full topic/subscription lifecycle plus the same message-handling trio
Queues has. **Filter rules** are covered by
`docs/superpowers/plans/2026-09-15-servicebus-filter-rules.md`: the
mockup's "Rules" column (filter-count chips) is built, and expanding a
subscription reveals a panel listing its SQL filter rules with add/delete
actions. Correlation filters and rule editing remain out of scope (see
that plan's design doc, §7).

- **One combined page, not a drill-down**: a single "Topics & Subscriptions"
  nav item → `Topics.razor`. **Superseded 2026-09-17 — see §6.2.1**: this
  was one `MudTable` whose rows were either a topic or (only when that topic
  was expanded) one of its subscriptions, distinguished by a discriminated
  view-model and `MudTable`'s `RowClassFunc`, with columns `Name | Active |
  Dead-letter | Scheduled | Actions`. The page is now a master/detail split;
  what follows in this bullet still describes the data each level shows, and
  the rest of §6.2 is unchanged. A topic shows its subscription count and
  **aggregated** Active/Dead-letter counts summed from its own
  subscriptions — visible without opening it, matching the mockup's "a
  collapsed list still shows where the backlog is." Its own Scheduled count
  is real (`TopicRuntimeProperties.ScheduledMessageCount`); a subscription
  row's Scheduled cell is `—`, because `SubscriptionRuntimeProperties` (the
  real Azure type, confirmed by decompiling `Azure.Messaging.ServiceBus`
  7.20.2 the same way earlier work in this file did) has no such field —
  only `ActiveMessageCount`/`DeadLetterMessageCount`/`TotalMessageCount`/the
  two transfer counts. The mockup's per-subscription "Scheduled" number
  doesn't correspond to anything the SDK actually exposes.
- **Eager aggregation, by choice**: since the aggregate counts must be
  visible without expanding, the query handler lists every topic's
  subscriptions up front (one `ListSubscriptionsAsync` call per topic) when
  the page loads, not lazily on first expand. Accepted cost at this
  console's scale (a personal/small-team admin tool, not hundreds of
  topics); a lazy alternative was considered and rejected because it can't
  show real numbers on a collapsed row, which is the whole point of
  aggregating them.
- **Topic-level send**: a topic row's "Send" action reuses `SendMessageDialog`
  unmodified — publishing to a topic fans out to every matching
  subscription, so sending is never a per-subscription action. A
  subscription row's actions are Peek / Dead-letter / Delete.
- **Shared UI, not duplicated UI**: the existing `Peek.razor`'s message
  table and dead-letter action bar (selection checkboxes, resubmit-selected,
  purge, the typed-confirmation flow) are extracted into a shared component
  rendered by both the unchanged `Peek.razor` (queues) and a new
  subscription-peek page. `Peek.razor`'s own route, handlers, and tests are
  untouched.
- **`IServiceBusOperations` — additive only**: new parallel methods
  (`ListTopicsAsync`, `CreateTopicAsync`, `DeleteTopicAsync`,
  `ListSubscriptionsAsync`, `CreateSubscriptionAsync`,
  `DeleteSubscriptionAsync`, `PeekSubscriptionMessagesAsync`,
  `ResubmitSubscriptionDeadLetterMessagesAsync`,
  `PurgeSubscriptionDeadLetterMessagesAsync`) sit alongside the existing
  queue methods, none of which change signature or behavior. Sending reuses
  the existing `SendMessageAsync` unmodified — a topic name is just another
  entity path to `ServiceBusClient.CreateSender`, identical under the hood
  to a queue name, so no new send method exists. `AzureServiceBusOperations`
  shares implementation internally via private helpers (the SDK's
  `CreateReceiver` already has a topic+subscription overload beside the
  queue one). The page's own aggregation (above) lives in a handler, not in
  `IServiceBusOperations` — the interface stays at "one entity at a time."
- **Handlers, audit, and safety mirror Queues exactly**: new plugin handlers
  follow the identical try/catch → `FriendlyError`-wrapped `PluginResult.Fail`
  → `ISnackbar` shape, registered in `ServiceBusPlugin.ConfigureServices` in
  the same task that creates them. Delete topic (cascades to its
  subscriptions — the confirmation dialog states the subscription count
  that will be removed), delete subscription, and purge subscription
  dead-letter are all `ActionRisk.Destructive`, gated the same
  `IConnectionProvider`-sourced typed-for-prod way as Queues' purge — applied
  to the new subscription-peek page **from the start**, not retrofitted
  after a finding, since Queues' final review already established exactly
  why that matters (§6.1).
- **No schema changes**: topics and subscriptions live in Azure, not
  SbConsole's own database, exactly as queues do today.

Out of this plan: filter rules (view/add/delete); deferred-message tooling,
sessions tooling beyond basic display, metrics dashboards/history, ARM/
namespace creation, Entra ID auth (all still out of v1 generally, per the
original scope).

#### 6.2.1 Master/detail layout (2026-09-17)

The three-tier flat table above (topic row → subscription row → rules panel
row) was re-drawn against a later mockup revision — "4a Master / detail" in
`SbConsole Wireframes.dc.html`, chosen over that revision's "4b", which kept
the table. The page is now a 286px master list of topics beside a detail
pane for the selected one:

- **The topology is navigation, not nesting.** Selecting a topic in the left
  list replaces the detail pane; nothing expands in place. The first topic
  is selected on load, and a selection that survives a reload is kept. This
  is why the rules tier is worth drawing at all: one topic at a time means a
  rule gets a full-width line, so a SQL expression or a correlation match is
  readable instead of truncated into a chip — the thing the flat table could
  not do at any column width.
- **The left list keeps the aggregate roll-up**, so "where is the backlog" is
  still answerable without clicking: name, subscription count, active count,
  size, and a dead-letter badge **only when non-zero** (amber, or red past
  `DeadLetterBadThreshold`) so the eye lands on the topics that have one.
- **Status is a first-class chip** on each subscription card. The data was
  always there (`SubscriptionSummary.Status`) and was never rendered: a
  `ReceiveDisabled` subscription sitting on 92 active messages is the bug
  this screen exists to catch, and it was invisible before. Such a
  subscription also carries a "not draining" note.
- **Presentation is a scoped stylesheet**, `Topics.razor.css`, not MudBlazor
  table chrome — the mockup's own measurements, with every colour derived
  from the `--mud-palette-*` tokens `NocturneTheme.cs` defines (the same hex
  values the mockup's Nocturne stylesheet uses), so light/dark still follows
  the theme and no brand colour is hard-coded. Counts and sizes format
  through `CultureInfo.InvariantCulture`; a comma-decimal server locale
  otherwise rendered "1,2 MB".
- **Unchanged**: eager aggregation, the handler/audit/safety shape, the
  dialogs, the peek routes, and `IServiceBusOperations`. This pass moved
  markup, not behaviour.

### 6.3 Dead-letter overview (2026-09-13)

A cross-cutting page the mockups show as a third nav item alongside Queues
and Topics & Subscriptions, with a live badge (e.g. "312") — not scoped to
one connection, but a single number and a single page spanning every
`azure-servicebus` connection at once.

- **`IServiceBusOperations.ListDeadLetterEntriesAsync(connectionString, ct)`**
  returns every queue and subscription with a non-zero dead-letter count for
  one connection, as `DeadLetterEntry(string EntityType, string? TopicName,
  string EntityName, long Count)` (`EntityType` is `"Queue"` or
  `"Subscription"`; `TopicName` is null for a queue). One seam, reused by
  both the nav badge and the overview page below, so "enumerate everything
  with a non-zero DLQ" exists exactly once — not duplicated between a
  per-connection badge computation and a per-connection page computation.
- **`ServiceBusPlugin.GetNavBadgeAsync`** (§3) answers only for
  `/p/azure-servicebus/dead-letter`, by summing `ListDeadLetterEntriesAsync`'s
  counts; every other href it's asked about returns `null`.
- **`DeadLetterOverview.razor`** at `/p/azure-servicebus/dead-letter` — no
  namespace picker, one flat table (Namespace | Entity | Type | Count |
  Peek) across every connection at once, each row's Peek link reusing the
  existing per-entity queue/subscription peek pages in dead-letter mode. Its
  handler lists every `azure-servicebus` connection, decrypts each secret,
  calls the same `ListDeadLetterEntriesAsync`, and merges the results; a
  connection whose secret is missing or whose Azure call fails is skipped
  (logged) for that page load rather than failing the whole page.

Out of this plan: per-entity notification/alerting on DLQ growth, historical
DLQ trend charts — this is a live snapshot only, matching the mockup.

## 6.5 Kafka plugin (`SbConsole.Plugins.Kafka`) — Topics (2026-09-17)

SbConsole's second plugin, built the same way Service Bus's Queues plan proved the
architecture out for the first: `KafkaPlugin : IPlugin`, `Id`/`ConnectionKind` =
`"kafka"`, `DisplayName`/`ConnectionKindDisplayName` = `"Apache Kafka"`. Registered via
`AddSbConsolePlugin<KafkaPlugin>()` in `Program.cs`, right after Service Bus — no host routing
change was needed (§5's `AdditionalAssemblies` wiring already scans every registered plugin's
assembly generically).

Connection-string auth model differs from Service Bus's single Azure connection string: since
every plugin gets exactly one opaque secret string end-to-end (§3), the Kafka connection secret
is a librdkafka config string (`key=value` pairs separated by `;`), parsed straight into
`Confluent.Kafka`'s `ClientConfig`-derived types — covers plaintext, SASL/PLAIN, SASL/SCRAM, and
mTLS without SbConsole inventing its own schema. Full design:
`docs/superpowers/specs/2026-09-16-kafka-topics-plugin-design.md`.

**Topics (shipped):** list (with per-partition-summed approximate message count — see the design
spec §4 for why this means "currently retained," not "unprocessed backlog," unlike Service Bus's
ActiveMessageCount), create, delete (`Destructive`); peek (non-destructive, partition-scoped, not
merged across partitions — Kafka only orders within a partition); produce. The Topics page adds,
after a UI wireframe review, the same conventions already established on Service Bus's
`Queues.razor`: a filter box, a topic/partition-count summary line, a safe echo of the connection's
non-credential fields (`bootstrap.servers`/`security.protocol`/`sasl.mechanism`) under the cluster
picker, an "internal" badge with read-only actions for `__`-prefixed topics, and a
low-replication-factor warning badge. The Peek page shows the selected partition's low/high
watermark next to the Fetch action, computed from the same watermark query
`ConfluentKafkaOperations` already needs internally, via a `PeekResult` record.

**A pre-existing bug this second plugin exposed and fixed:** `AddSbConsolePlugin<TPlugin>()`
registered `IPluginStore` as a single unkeyed scoped service (a "single-plugin simplification" its
own comment flagged). Registering Kafka alongside Service Bus would have made every unkeyed
`IPluginStore` resolution in the app resolve to whichever plugin registered last — silently
redirecting Service Bus's `Queues.razor` metric-history sparkline storage into Kafka's namespace.
Fixed by making the registration keyed by plugin `Id` (`AddKeyedScoped`), with `Queues.razor`'s
injection moved from `@inject IPluginStore Store` to a keyed `[Inject, FromKeyedServices(...)]`
property — the one place in the codebase that used the unkeyed convenience registration.

Deferred past this plan, each its own future plan: consumer group management (list, lag,
offset reset); dead-letter handling via the DLQ-topic convention (Kafka has no native DLQ);
Schema Registry integration; topic configuration beyond partition count/replication factor;
integration tests against a real/emulated broker.

## 6.7 AWS plugin (`SbConsole.Plugins.Aws`) — Queues (2026-09-21)

SbConsole's third plugin, built the same way Service Bus's Queues plan and Kafka's Topics plan
proved the architecture out for their systems: `AwsPlugin : IPlugin`, `Id`/`ConnectionKind` =
`"aws"`, `DisplayName`/`ConnectionKindDisplayName` = `"AWS SQS/SNS"` (named for the plugin's full
eventual scope from the start, so SNS can join later with no rename). Registered via
`AddSbConsolePlugin<AwsPlugin>()` in `Program.cs`, right after Kafka. Full design:
`docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md`.

Connection secret model differs from both existing plugins in field *variance* (though not in
convention — it's still one flat `key=value;` string, `AwsConfigParser`, mirroring
`KafkaConfigParser`'s parse/`SafeEcho` shape exactly, including skipping malformed segments rather
than throwing): three structurally different auth modes (`access-keys`, `assume-role`,
`default-chain`), each with its own field set, plus mode-independent `region`/`endpoint`/
`pathStyle` fields for LocalStack. `AwsConfigParser.SafeEcho` allowlists only `region`/`mode`/
`endpoint` for display, excluding every credential-shaped key so decrypted secrets never reach the
browser even indirectly. **No custom connection-form UI was built** — verified against
`AddEditConnectionDialog.razor` that no per-plugin custom-field hook exists for any plugin; AWS
follows Kafka's own precedent exactly, typing the flat secret by hand into the existing generic
textbox.

**Queues (shipped):** list (prefix-only filter, matching `ListQueues`' real `QueueNamePrefix`
constraint — labelled "Starts with," not "Search"), create (Standard and FIFO, with FIFO's forced
`.fifo` suffix and content-based-dedup/high-throughput options), delete (`Destructive`), purge
(`Destructive`, typed-confirm-on-prod, async/eventually-consistent completion caveat surfaced in
the dialog copy), receive (modelled as a **command**, not a query — unlike Service Bus/Kafka's
non-destructive peek, SQS receiving has a real broker side effect: messages become invisible to
other consumers for the visibility timeout), delete/release a received message (release =
`ChangeMessageVisibility` to 0), send (FIFO-conditional Message Group ID / Deduplication ID
fields), and DLQ redrive via AWS's native `StartMessageMoveTask` API (not a hand-rolled
receive-then-send loop — see the design spec §1's decision record). Every operation sits
behind `ISqsOperations`, the plugin's thin wrapper interface between pages/handlers and the real
`AWSSDK.SQS`/`AWSSDK.SecurityToken` clients (`SqsOperations`); handler and plugin unit tests
substitute it directly, no network or LocalStack involved. `FriendlyAwsError.From` maps the small
set of exceptions these operations can actually raise (`AmazonSecurityTokenServiceException` and
`AmazonSQSException`, pattern-matched on `ErrorCode`, plus a few typed SQS exceptions like
`QueueDoesNotExistException`) to short fixed messages, falling back to `SbConsole.Sdk`'s shared
`FriendlyError.From` for anything unmapped — every mapped exception type was confirmed to exist
against the installed `AWSSDK.SQS` 3.7.400.62 / `AWSSDK.SecurityToken` 3.7.401.13 packages rather
than assumed from the AWS SDK's general shape (a few plan-suggested names, e.g. an
`AttributeNames` property on `ReceiveMessageRequest` and a `AssumeRoleAWSCredentials` type under
`Amazon.SecurityToken`, didn't actually exist in these package versions and were corrected during
implementation). Every command handler logs the caught exception via `ILogger<T>` before reducing
it to a friendly message, matching `SbConsole.Sdk.FriendlyError`'s documented "never a diagnostics
loss" contract and Kafka's own handlers — a gap the per-task reviews missed (none of the eight
command handlers originally took a logger) and only the final whole-branch review caught.

**A DLQ-semantics bug caught only by the final whole-branch review, not any single task's
review:** a queue's own `RedrivePolicy` attribute means "I dead-letter *to* somewhere" (I am a
source queue), not "I *am* a dead-letter queue" — but the first-shipped `Queues.razor` used exactly
that flag to show the "DLQ" chip and to gate the Redrive button, which is backwards: AWS's
`StartMessageMoveTask` requires `SourceArn` to actually be a queue other queues redrive *into*.
Fixed by computing `QueueSummary.DeadLetterSourceCount` — how many other queues' `RedrivePolicy`
targets this queue's ARN — purely by parsing every already-fetched queue's `RedrivePolicy` JSON in
`ListQueuesAsync` and counting matches per target ARN (no extra AWS call), then gating the chip and
Redrive button on `DeadLetterSourceCount > 0` instead. Regression tests pin the exact input shape
the original bug got backwards. **Known follow-up, not yet fixed:** this count is computed only
over the queues the current "Starts with" prefix filter returned, so filtering directly to a DLQ's
name prefix (excluding its source queues) undercounts it to zero and hides both the chip and the
Redrive button for the one queue you filtered to find — a `ListDeadLetterSourceQueuesAsync` call
(or computing the count over an unfiltered list regardless of the display filter) would fix this
properly; not done here since it wasn't the shape of the original bug. *(2026-09-25: fixed with
per-queue `ListDeadLetterSourceQueues` under a prefix — see §6.7.2's Known limitations.)* Also not yet covered: a
page-level test for Purge's confirm-then-dialog wiring (Delete has one, Purge — also `Destructive`
and prod-gated — doesn't).

**One correction to the source UI mockup** (`~/Desktop/UI mockups for NerveCenter/SbConsole
AWS.dc.html`) carried through from the design spec: its refresh-cost caption states `1 + 3n` API
calls per refresh; the real API is `1 + n` (`GetQueueAttributes` with `AttributeNames=[All]`
returns every attribute in one call per queue). `Queues.razor`'s shipped caption states the real
number.

Deferred past this plan, matching the design spec's explicit scope cuts: SNS entirely (topics,
subscriptions, publish — separate plan); Dashboard/nav-badge/dead-letter-overview integration
(`AwsPlugin` explicitly overrides `GetNavBadgeAsync` with a trivial no-op returning `null` —
required because C#'s default-interface-member dispatch isn't reachable through a concrete-typed
reference — and relies on the SDK default for `GetDashboardMetricsAsync`/
`GetDashboardProblemsAsync`/`GetOldestDeadLetterAsync`/`GetResourceMetricsAsync`); a persisted
read-only/degraded-connection capability set (`TestConnectionAsync` reports richer diagnostic text
only — nothing hides a button); live redrive-progress polling (`ISqsOperations` has no
`ListMessageMoveTasks`-backed status method — the move task is started and its start/failure
result reported, with no polling method added since nothing in this plan's UI calls one yet);
per-message manual "redrive to source" from inside Receive. *(2026-09-24: the dashboard/nav-badge
integration, redrive-progress polling, and per-message "Move to source" have since shipped — see
§6.7.2. `AwsPlugin` no longer has a trivial `GetNavBadgeAsync` no-op; it now overrides the badge,
dashboard-metric, dashboard-problem and resource-metric hooks and leaves only
`GetOldestDeadLetterAsync` at the SDK default. 2026-09-25: `GetOldestDeadLetterAsync` is now implemented too, via
CloudWatch — see §6.7.2's Non-goals note. The persisted read-only capability set remains
deliberately unbuilt — see §6.7.2's Non-goals.)*

**Also not built, cut at planning time rather than during implementation** (the plan's own task
breakdown never included these, so `PageCount` in `AwsPlugin.Contribution` is `2` — Queues and
Receive — not the design spec's originally-stated `3`): a dedicated `QueueDetail.razor` page (ARN
with copy, the four approximate-metric tiles, a redrive-out panel showing the queue's own
`RedrivePolicy`, the full attribute list, tags, and — once SNS exists — the "subscribed to N SNS
topics" cross-reference panel the design spec described for it); the auto-refresh picker
(Off/15s/30s/60s) and its "counts read HH:MM" caption, so `Queues.razor` only refreshes on an
explicit user action, never on a timer; a rendered `Created` column (`QueueSummary.CreatedAt` is
fetched and stored on every row but never displayed); and an overflow menu for Delete/Purge — both
sit inline on the row instead. None of these block the shipped functionality; they're straight
scope cuts a future pass can pick up alongside the SNS plan or on their own. *(2026-09-24: all of
these — `QueueDetail.razor`, the auto-refresh picker and its caption, the `Created` column, and the
Delete/Purge overflow menu — have since shipped; see §6.7.2.)*

**Local dev**: `docker compose --profile aws up -d` runs LocalStack (`SERVICES=sqs,sns`, so the
SNS plan needs no compose change) gated by a real `healthcheck` (`curl` against
`/_localstack/health`, `condition: service_healthy`) rather than a fixed startup delay, plus a
one-shot `aws-init` seed script — depending on that healthcheck — that creates sample queues with
a redrive policy already attached, mirroring Kafka's `kafka-init` pattern. See `docker/README.md`.
*(2026-09-25: LocalStack now runs with `SERVICES=sqs,sns,sts,cloudwatch,logs`. `sts` because
`TestConnectionAsync` calls STS `GetCallerIdentity` first, so with only `sqs,sns` every Test
connection against the documented emulator failed; `cloudwatch` and `logs` back the SNS
delivery-failure count, the oldest-dead-letter tile and the delivery-logs tab (§6.7.2). LocalStack
publishes no SQS metrics and writes no SNS delivery-status logs, so those two stay empty locally,
but the calls succeed instead of failing with "service not enabled".)*

### 6.7.1 Topics & Subscriptions (2026-09-23)

The AWS plugin's SNS Topics section, shipped as an eight-task plan following Queues' own
pattern: `ISnsOperations`/`SnsOperations` built the same way `ISqsOperations` is (every method
takes the connection secret as a parameter, one real implementation constructed via the existing
`AwsConfigParser`/`AwsCredentialsFactory` pipeline), two new routed pages (`Topics.razor` and
`TopicDetail.razor`), three dialogs (Create, Subscribe, Publish), and handler folders mirroring
`Queues`/`Messages` naming. Topics use the same connection kind (`"aws"`) as Queues — no new
secret format or parser changes.

- **`ISnsOperations`** — the seam between handlers and the real `AmazonSimpleNotificationServiceClient`
  and `AmazonCloudWatchClient` (needed for the delivery-failure metric below). Methods: `ListTopicsAsync`,
  `CreateTopicAsync`, `DeleteTopicAsync`, `ListSubscriptionsAsync`, `SubscribeAsync`, `UnsubscribeAsync`,
  `PublishAsync`, `GetDeliveryFailureCountAsync`. `SnsOperations` constructs both clients the same way
  `SqsOperations` does, forwarding `ServiceURL`/`UseHttp` from the parsed config so LocalStack
  emulation works for SNS and CloudWatch too.

- **Topics list** (`/p/aws/topics`) — a new nav item registered in `AwsPlugin.NavItems`, second after
  Queues. Table columns: Topic name, Type (`Standard`/`FIFO`), Subscription count, Pending confirmation
  count, Failed-24h (CloudWatch delivery-failure sum, see below), Flags (KMS encryption, zero-subscribers
  warning), Actions. The Subscription and Pending counts are populated via a per-topic `GetTopicAttributes`
  call (`SubscriptionsConfirmed`/`SubscriptionsPending` attributes; `KmsMasterKeyId`'s presence drives the
  KMS flag), and Failed-24h via `GetMetricStatistics` (see below) — matching the Queues page's own per-row
  partial-failure pattern. When a topic's `GetTopicAttributes` call fails, `TopicSummary.AttributesUnavailable`
  is set and the row renders "—" for Subs/Pending (and never shows the KMS flag or the "no subscriptions"
  warning chip, since neither is known) instead of a misleading real-looking zero; Failed-24h degrades the
  same way, independently, when the CloudWatch call fails. `PluginContribution` page count increases from
  2 to 4 (`Queues`, `Receive`, `Topics`, `TopicDetail`); action count increases from 8 to 13
  (`CreateQueue`/`DeleteQueue`/`PurgeQueue`/`Receive`/`DeleteMessage`/`ReleaseMessage`/`SendMessage`/
  `StartRedrive`, plus `CreateTopic`/`DeleteTopic`/`Subscribe`/`Unsubscribe`/`Publish`).

- **Topic detail** (`/p/aws/topics/{topicArn}`) — two-tab design mirroring `Queues.razor` only with tabs,
  not a single-page layout. **Subscriptions tab** (default): ARN header (no copy button — noted as a UI
  polish item, not built); table showing Protocol, Endpoint, Filter policy (read-only, no editing), Raw
  message delivery flag, State, and Actions. State is one of only two values, `Confirmed`/`Pending` — there
  is no "Failing" subscription state and no elapsed-time display; a subscription's `SubscriptionArn` is
  either the literal string `"PendingConfirmation"` (pending) or a real ARN (confirmed), nothing else.
  Pending subscriptions show only a **Resend** action (calls `SubscribeAsync` again with the same
  parameters, triggering SNS's idempotent re-delivery of the confirmation) — never Remove, since
  `UnsubscribeAsync` has nothing to unsubscribe from until the subscription is confirmed and always fails
  on real AWS. Confirmed subscriptions show only **Remove** (`UnsubscribeAsync`, `ActionRisk.Mutating`),
  gated by a plain two-button `IConfirmationService.ConfirmAsync` confirm — not typed/Destructive, since
  re-subscribing fully reverses it. "+ Subscribe" opens `SubscribeDialog.razor` with Protocol selector
  (sqs, https, email, lambda), Endpoint field, and Raw message delivery toggle. **Attributes tab**: shows
  only the topic ARN today — it does not render encryption, FIFO, or created-at (a possible future
  addition, not built in this plan).

- **Publish dialog** (`src/SbConsole.Plugins.Aws/Pages/PublishDialog.razor`) — Topic (pre-filled from the
  row/detail page), Subject (caption: "email only"), Message attributes (key/value rows; a repeated key
  across two rows is tolerated by keeping the last row's value, not an unhandled `ArgumentException` from
  a naive `ToDictionary`), Message body. For FIFO topics: a required Message Group ID field and a plain
  (optional-looking but not validated) Deduplication ID field — there is no "content-based deduplication
  enabled" toggle/note mirroring `SendMessageDialog`'s fuller FIFO field set; a blank Deduplication ID is
  sent to `SnsPublishRequest.MessageDeduplicationId` as `null` (relying on the topic's own content-based
  deduplication setting if enabled), never as an empty string, which AWS would otherwise reject.
  **Fan-out preview**: on dialog open, calls `GetSubscriptionAttributes` once per subscription to fetch
  filter policies, then evaluates the current message attributes against each policy client-side (pure
  function, no network) and displays a "Matches N of M subscriptions" panel with ✓ for matched, ✕ for
  filtered, — for pending. Re-evaluated on every attribute edit. Publish is `ActionRisk.Mutating`, audited
  as `aws.topic.publish`.

- **CloudWatch delivery-failure metric** — `ISnsOperations.GetDeliveryFailureCountAsync` calls CloudWatch
  `GetMetricStatistics` with `Namespace: "AWS/SNS"`, `MetricName: "NumberOfNotificationsFailed"`,
  `Dimensions: [{Name: "TopicName", Value: topicName}]`, `StartTime: now-24h`, `EndTime: now`,
  `Period: 86400`, `Statistics: ["Sum"]` — a zero-datapoint response (no failures or metric has no data)
  returns 0, not an error. A denied call degrades that topic's Failed-24h cell (renders unavailable,
  not zero) rather than failing the whole page, matching the partial-failure pattern everywhere else
  in this plugin. **This requires a new IAM permission: `cloudwatch:GetMetricStatistics`**, documented
  in `docker/README.md`'s least-privilege policy example alongside the existing SQS permissions.

- **Test-connection integration note** — `SbConsole.Sdk.ConnectionCheck`/`ConnectionCheckStatus` and
  the richer `ConnectionTestResult.Identity`/`Checks` fields were introduced by the connections-page
  redesign (§11). `SqsOperations.TestConnectionAsync` now populates them: a `GetCallerIdentity` check
  (credentials valid; its account id becomes `Identity`), then two cheap permission probes reported as
  checks — "Queues visible" (`ListQueues`, `MaxResults = 1`) and "Topics visible" (one `ListTopics`
  page). Each probe goes through `ConnectionProbe.RunAsync`, which turns a denial into a `Failed` check
  with a `FriendlyAwsError` message — never a failed connection test — and is unit-tested directly,
  since the probes themselves need a real or emulated AWS account.

- **Error handling** — `FriendlyAwsError` gains pattern-matched arms for SNS and CloudWatch: it does not
  catch distinct exception subtypes (there is no `NotFoundException`/`InvalidParameterException`/
  `AuthorizationErrorException` in the installed SDK) but instead matches `ErrorCode` string values on
  the base `AmazonSimpleNotificationServiceException` and `AmazonCloudWatchException` types, the same way
  `AmazonSQSException { ErrorCode: "AccessDenied" }` is already matched above it: `AmazonSimpleNotificationServiceException
  { ErrorCode: "AuthorizationError" }` → "Access denied — check IAM permissions",
  `AmazonSimpleNotificationServiceException { ErrorCode: "NotFound" }` → "Topic or subscription not found",
  and `AmazonCloudWatchException { ErrorCode: "AccessDenied" }` → "Access denied — check IAM permissions
  for cloudwatch:GetMetricStatistics". Verified against the installed `AWSSDK.SimpleNotificationService`
  3.7.400.62 and `AWSSDK.CloudWatch` 3.7.401 packages at implementation time, not assumed.

- **Handlers, audit, and safety** — new plugin handlers (`ListTopicsQueryHandler`, `CreateTopicCommandHandler`,
  `DeleteTopicCommandHandler`, `ListSubscriptionsQueryHandler`, `SubscribeCommandHandler`,
  `UnsubscribeCommandHandler`, `PublishCommandHandler`, `GetTopicDeliveryFailureCountQueryHandler`,
  `GetSubscriptionFilterPoliciesQueryHandler`) follow Core's naming convention, are DI-registered in
  `AwsPlugin.ConfigureServices`, and follow the identical try/catch → `FriendlyAwsError`-wrapped result
  shape as Queues. Delete topic is `ActionRisk.Destructive` (typed-confirm-on-prod, same
  `IConfirmationService`/`IConnectionProvider.IsProd` gate every other destructive action in this plugin
  uses — a plain `ConfirmAsync("Delete", topicName, connection.IsProd)`, with no subscription count shown).
  Create topic, Subscribe, Unsubscribe, and Publish are `ActionRisk.Mutating` — Unsubscribe deliberately
  stays Mutating rather than Destructive, since re-subscribing fully reverses it, unlike deleting the
  topic itself. `IsProd` is always resolved server-side via `IConnectionProvider`, never a client-supplied
  parameter. **No schema changes**:
  topics and subscriptions live in AWS, not SbConsole's own database, exactly as queues do today.

Out of this plan: filter-policy authoring/editing (Subscribe/Edit have no filter-policy field; policies
are read-only and set via the AWS console), Access Policy tab on `TopicDetail.razor` (Subscriptions and
Attributes only, matching the deferred `QueueDetail` scope), delivery logs (needs CloudWatch Logs, a
separate service/permission), cross-link to "Subscribed to N SNS topics" on `QueueDetail.razor` (that page
was never built), nav-badge/dashboard integration (stays at SDK defaults), per-message manual redrive from
any SNS-side view (redrive remains queue-level only, already shipped with SQS). *(2026-09-24:
filter-policy authoring/editing, the Access policy tab (read-only), a fuller Attributes tab, the
"Subscribed to N SNS topics" panel on the now-built `QueueDetail.razor`, and nav-badge/dashboard
integration have since shipped — see §6.7.2. Delivery logs remain unbuilt; see §6.7.2's Non-goals.)* *(2026-09-25: delivery logs have
since shipped too — see §6.7.2's Non-goals.)*

### 6.7.2 Completion pass (2026-09-24)

A seven-task pass (plan: `docs/superpowers/plans/2026-09-24-aws-completion.md`) closing out the
scope §6.7 ("Deferred past this plan", "Also not built") and §6.7.1 ("Out of this plan") left open,
apart from the Non-goals below. Same conventions as before: every AWS call goes through
`ISqsOperations`/`ISnsOperations`, handlers are plain query/command classes, commands audit success
*and* failure via `IAuditScope` and reduce exceptions through `FriendlyAwsError`, and `IsProd` always
comes from the server-side `IConnectionProvider` lookup. **No SDK or schema changes.**
`AwsPlugin.Contribution` goes from 4 pages / 13 actions to **5 pages / 16 actions** (new page:
`QueueDetail`; new actions: Cancel redrive, Move message to source, Set subscription filter policy).

- **Dashboard, nav badge, resource metrics** — `AwsPlugin` now overrides `GetNavBadgeAsync`,
  `GetDashboardMetricsAsync`, `GetResourceMetricsAsync` and `GetDashboardProblemsAsync` (replacing
  §6.7's trivial badge no-op). All four read one **60-second static per-connection-string cache** of
  an unfiltered `ListQueuesAsync(secret, null)`, built exactly like
  `KafkaPlugin.GetCachedConsumerGroupsAsync` (an internal overload takes `now` and the fetch delegate
  so reuse/expiry is unit-tested without AWS; a failed fetch throws before the cache is written, so
  failures are never cached). A queue counts as a DLQ when `DeadLetterSourceCount > 0` and its
  attributes were readable. The badge (only for `/p/aws/queues`) is the total `ApproxVisible` across
  DLQs, `null` — never `0` — when nothing is dead-lettered. Dashboard metrics are `Queues` (count) and
  `Dead-lettered` (DLQ visible total — the label `WallboardSnapshotLoader` sums). Resource metrics are
  one row per readable queue (`ActiveCount = ApproxVisible`, `DeadLetterCount = ApproxVisible` for a
  DLQ, else 0). Problems are one `Warning` per DLQ with visible messages, title = queue name, detail
  `"{n} dead-lettered"`, linking to `/p/aws/queues/{escaped queue URL}?connectionId={id}`. **Failures
  propagate** rather than being swallowed, matching Kafka: every host call site (NavMenu, Home,
  `WallboardSnapshotLoader`, `MetricsCollectorService`) already catches and logs, and Home/Wallboard
  use the exception to mark the connection unchecked — returning empty would show a broken
  connection as healthy.

- **Queue detail page** (`/p/aws/queues/{queue URL, escaped into one segment}?connectionId=`, the same
  approach as `TopicDetail`'s ARN route) — the queue name on `Queues.razor` now links here. Backed by
  `ISqsOperations.GetQueueDetailAsync` (`GetQueueDetailQueryHandler`): `GetQueueAttributes(All)` plus
  `ListQueueTags` plus a paged `ListDeadLetterSourceQueues`. Tags and dead-letter sources each degrade
  to `null` on failure (rendered "unavailable", never a misleading empty), while a failed attributes
  call fails the page. Renders: DLQ/FIFO/KMS chips; Receive (link), Send, Redrive (DLQs only), Purge
  and Delete buttons reusing the existing dialogs/handlers and confirmation flows; the ARN with a copy
  button (`navigator.clipboard.writeText`); created/last-modified timestamps; ~Visible / ~In flight /
  ~Delayed tiles plus a Source-queues tile on DLQs; a **Redrive out** panel from the queue's own
  `RedrivePolicy` (`SqsOperations.ParseRedrivePolicy`, which accepts `maxReceiveCount` as a number or
  numeric string and never throws — a missing/malformed policy is "No DLQ configured"); the full
  sorted attribute table; and the tags table. On this page "is a DLQ" comes from
  `ListDeadLetterSourceQueues`, so it is correct regardless of any prefix filter.
  **"Subscribed to N SNS topics" panel**: `ISnsOperations.ListSubscriptionsForEndpointAsync` pages
  through the account-wide `ListSubscriptions` and filters client-side on `Endpoint == queue ARN`
  (ordinal); each row links to that topic's detail page and shows protocol and a Pending chip. A
  failure (e.g. denied `sns:ListSubscriptions`) is an inline warning in the panel, not a page failure.
  The scan is **capped at 20 pages (the first 2,000 subscriptions)** — past that the result
  (`EndpointSubscriptions.IsTruncated`) makes the panel say it is partial and how many were scanned.
  It runs **once per queue**, concurrently with (not blocking) the rest of the load, and is not
  repeated by the reloads after Send/Purge/Redrive. The page loads in `OnParametersSetAsync`, so
  navigating to another queue (or connection) on the same component instance resets everything —
  detail, SNS panel, redrive tasks and their poll — and discards any load still in flight for the
  previous queue.

- **Redrive progress + cancel** — `ISqsOperations.ListMessageMoveTasksAsync` (`ListMessageMoveTasks`,
  `MaxResults = 10`) and `CancelMessageMoveTaskAsync`. On DLQs only, `QueueDetail` shows a **Redrive
  tasks** panel: per task status, a progress bar (indeterminate when AWS doesn't report a total),
  moved/to-move counts, start time and failure reason. A 5-second poll runs **only while some task is
  active — `RUNNING` or `CANCELLING`** (`MessageMoveTaskSummary.IsActive`), stops on its own otherwise,
  never overlaps an in-flight load, and is cancelled when the component is disposed. A refresh
  requested while one is in flight (e.g. right after Cancel) is queued and re-run when the in-flight
  one finishes, so a stale `RUNNING` can't stick. Only `RUNNING` tasks get a **Cancel** button (`CancelRedriveCommandHandler`,
  `ActionRisk.Mutating`, audited `aws.queue.redrive.cancel`) with **no confirmation dialog** —
  cancelling only stops further moves; messages already moved stay moved (the snackbar says so).
  A listing failure is an inline warning that keeps the last-known tasks.

- **Queues list polish** — a `Created` column (UTC date, `yyyy-MM-dd`; "—" for rows whose attributes
  were unreadable); an **auto-refresh** picker Off/15s/30s/60s, default Off, persisted per plugin in
  `IPluginStore` under `queues.autoRefreshSeconds` (an unreadable or unrecognised stored value means
  Off; a failed save still applies the choice for the visit); a tick that lands mid-load is skipped,
  never overlapped; the loop is disposed with the page. A **"counts read HH:mm:ss"** caption
  (server-local time via `TimeProvider`, labelled `UTC` / `UTC±hh:mm`) follows each successful load;
  a failed load clears it and shows an inline error, and a failing auto-refresh streak raises one
  snackbar, not one per tick. Loads carry a generation number: a load that completes after the
  connection or prefix changed is discarded, so one account's queues never render under another. Purge and Delete moved from inline buttons
  into a per-row overflow `MudMenu`; their confirmation flows are unchanged (§6.7's missing Purge
  page-level test now exists, exercising it through the menu).

- **Topic attributes + access policy** — `ISnsOperations.GetTopicAttributesAsync`
  (`GetTopicAttributesQueryHandler`). `TopicDetail`'s Attributes tab (previously ARN only, §6.7.1)
  now shows a summary table — ARN, display name, owner, FIFO, content-based dedup, KMS key,
  confirmed/pending/deleted subscription counts — then every remaining raw attribute. A new
  **Access policy** tab shows `Policy`, and `DeliveryPolicy`/`EffectiveDeliveryPolicy` when present,
  pretty-printed (non-JSON shown raw). **Read-only** — no policy editing. A denied
  `sns:GetTopicAttributes` is an inline warning on both tabs, not a page failure.

- **Filter-policy authoring** — `ISnsOperations.SetSubscriptionFilterPolicyAsync` via
  `SetSubscriptionAttributes`. Setting a policy first reads the subscription's current
  `FilterPolicy`/`FilterPolicyScope` (`GetSubscriptionAttributes`) because SNS sets one attribute per
  call and validates the policy against the scope in force at that moment: `PlanFilterPolicyUpdates`
  (pure, unit-tested) writes the scope first only when switching to `MessageBody` with a policy already
  present, and policy-then-scope otherwise. If the second of those two calls fails after the first
  succeeded, `ApplyFilterPolicyUpdatesAsync` throws `FilterPolicyPartiallyAppliedException` and the
  handler returns (and audits) an explicit "left partially updated" error naming which half landed
  — like Move to source's "possible duplicate" error. **Clearing** is a single `FilterPolicy = ""` call (scope
  left alone — SNS rejects a scope without a policy). `FilterPolicyValidator` runs client-side before
  any AWS call and live in both editors: scope must be `MessageAttributes`/`MessageBody`, and the
  policy must be a top-level JSON object (empty = clear); SNS's own operator rules are left to SNS.
  `SetFilterPolicyCommandHandler` is `ActionRisk.Mutating` (fully reversible), audited
  `aws.subscription.filterpolicy.set`; validation rejections aren't audited since nothing was
  attempted. UI: `SubscribeDialog` gains an optional filter-policy toggle, scope selector and JSON
  editor (sent as Subscribe attributes, only when a policy is given); confirmed subscription rows on
  `TopicDetail` gain **Edit filter**, opening `FilterPolicyDialog` pre-filled from the already-loaded
  row (pending rows can't be targeted — their ARN is the literal `PendingConfirmation`).
  *(2026-09-25 correction: "scope first only when a policy is already present" was wrong. With no
  current policy (never set, or cleared) the policy-first order made SNS validate a nested
  `MessageBody` policy under the default `MessageAttributes` scope and reject it — reproduced on
  LocalStack. `PlanFilterPolicyUpdates` now writes the scope first whenever the new scope is
  `MessageBody` and differs from the current one, and policy-then-scope otherwise. The claim that
  SNS rejects a scope without a policy was also wrong on LocalStack, which accepts a scope-only set;
  that is unverified on real AWS, but if real SNS rejects it, the scope call is the first call and
  fails before anything changed, so it is no worse than before. The partial-update error no longer
  mentions a "previous filter policy" when there was none.)*

- **Per-message "Move to source" on Receive** — on a DLQ, each received message gets **Move to
  source**. Sources come from `GetQueueDetailAsync`'s `ListDeadLetterSourceQueues` result (one call,
  not a scan of every queue's `RedrivePolicy` as the plan suggested); one source is used directly,
  several open `MoveToSourceDialog` to pick one; if the sources lookup fails the button is hidden and
  a caption says why. `MoveMessageToSourceCommandHandler` (`ActionRisk.Mutating`, audited
  `aws.message.move`) is **send-then-delete**: `SendMessage` to the source with the same body and
  message attributes (types and binary values preserved), then `DeleteMessage` on the DLQ with the receipt handle. A failed send deletes
  nothing; a failed delete after a successful send returns an explicit "copied … but is still in the
  DLQ — possible duplicate" error. FIFO-ness is read from the destination URL's `.fifo` suffix: FIFO
  resends reuse the original `MessageGroupId` (now carried on `ReceivedMessage`, from the
  `MessageGroupId` system attribute) with the original message id as deduplication id; standard
  queues get neither. No confirmation dialog.

**New IAM permissions** (added to `docker/README.md`'s least-privilege list and JSON example):
`sqs:ListQueueTags`, `sqs:ListDeadLetterSourceQueues`, `sqs:ListMessageMoveTasks`,
`sqs:CancelMessageMoveTask`, `sns:ListSubscriptions`, `sns:SetSubscriptionAttributes`
(`sns:GetSubscriptionAttributes` was already listed). Every one except `sns:SetSubscriptionAttributes`
and `sqs:CancelMessageMoveTask` degrades to an inline "unavailable" state when denied.

**Deviations from the plan**: failures in the dashboard hooks propagate (the plan said "return
null/empty — check Kafka and match"; Kafka propagates, and the host depends on it). Move-to-source
resolves sources via `ListDeadLetterSourceQueues`, not `ListQueuesAsync`. The detail page's query
parameter is `?connectionId=` (the plan said `?connection=`), matching every other AWS page.

**Known limitations**: *(The original "Move to source loses Binary attributes and resends Number as
String" limitation is fixed: `ReceivedMessage.MessageAttributes` now keeps each attribute's
`DataType`, `StringValue` and `BinaryValue` as an `SqsMessageAttribute`, and the resend passes them
through unchanged via `SendMessageRequest.TypedMessageAttributes`; the Send dialog's plain string
attributes still go out as `String`. Receive shows each attribute's type, and binary values as a
byte count.)* *(The §6.7 prefix-filter
undercount is fixed (2026-09-25): `QueueDetail`, Receive's Move to source and the dashboard hooks
(unfiltered list) were already correct, and `Queues.razor`'s DLQ chip and inline Redrive button now
are too. `SqsOperations.ListQueuesAsync` keeps the zero-extra-call local `RedrivePolicy` count when no
prefix is applied (every queue is listed, so it is complete); under a prefix, the pure
`BuildQueueSummariesAsync` calls `ListDeadLetterSourceQueues` (`MaxResults = 1000`, paged) once per
returned queue with readable attributes and uses that as `DeadLetterSourceCount` — the full count,
since the chip shows "DLQ ×N". That is +n calls, cheaper than listing and reading attributes for the
whole account. A failed lookup (e.g. `sqs:ListDeadLetterSourceQueues` denied) falls back to the local
count for that row only. The refresh-cost caption says `1 + 2 × n` under a prefix, `1 + n` without.)*

**Non-goals** (deliberately not built):
- A persisted read-only / denied-actions capability set and IAM Policy Simulator integration —
  rejected host-wide in the connections-page redesign (§11, "Out of scope"); test results stay
  diagnostic text only and no button is hidden from them.
- SNS delivery logs — needs CloudWatch Logs, a separate service and permission set.
  *(2026-09-25: resolved — no longer a non-goal. `TopicDetail.razor` has a read-only **Delivery logs**
  tab backed by `GetTopicDeliveryLogsQueryHandler` (a query: no audit row) →
  `ISnsOperations.GetDeliveryLogsAsync(secret, topicArn, window, limit)`, using the new
  `AWSSDK.CloudWatchLogs` 3.7.404 package (client built by `CloudWatchLogsClientFactory`, same
  ServiceURL/UseHttp forwarding as `CloudWatchClientFactory`). SNS writes delivery-status logs, for
  topics with a `<Protocol>SuccessFeedbackRoleArn`/`<Protocol>FailureFeedbackRoleArn` set, to
  `sns/{region}/{account}/{topicName}` and `.../{topicName}/Failure`, both derived from the topic ARN.
  `SnsDeliveryLogs.ScanAsync` (pure, page fetch injected) runs `FilterLogEvents` on both groups over
  non-overlapping time slices walked newest-first (1h, 6h, 24h, 7d edges clipped to the window),
  because `FilterLogEvents` only pages oldest-first; it stops once `limit` (200) events are in hand or
  after 10 calls in total (1,000 events per page), and says so via `IsTruncated`. Results are merged
  newest-first. `ResourceNotFoundException` on a group skips it; both missing is reported as
  `LoggingNotConfigured` (an info alert naming the topic attributes / console setting, reworded when
  the topic already has feedback roles, since SNS creates each group on its first write), not an error.
  Each event is parsed by a pure helper (`notification.messageId`, `delivery.destination`/
  `statusCode`/`providerResponse`/`dwellTimeMs`/`attempts`, `status`); fields are optional because the
  shape varies by protocol (SMS logs have no `statusCode`/`attempts`), and an unparseable event is kept
  with its raw text and a status from its group, never dropped. The tab loads only when first opened,
  offers 1h/24h/7d windows (default 24h) and an All/Failures filter (client-side, no refetch), and a
  failure is an inline warning on that tab only. `FriendlyAwsError` maps CloudWatch Logs
  `AccessDeniedException` to "check IAM permissions for logs:FilterLogEvents". **New IAM permission**:
  `logs:FilterLogEvents`, scopable to `arn:aws:logs:*:*:log-group:sns/*` (added to
  `docker/README.md`). No page/action count change: it is a read-only tab on an existing page.)*
- `GetOldestDeadLetterAsync` — stays at the SDK default (`null`): SQS exposes no enqueue timestamp
  without *receiving* a message, and a receive has a real side effect (the message goes invisible
  for the visibility timeout), so the wallboard's oldest-message tile is not fed by this plugin.
  *(2026-09-25: resolved — no longer a non-goal. `AwsPlugin.GetOldestDeadLetterAsync` now feeds the
  tile without receiving anything: for each dead-letter queue (`IsDeadLetterQueue`, over the same
  60s queue-list cache as the other dashboard hooks) with `ApproxVisible > 0` it reads CloudWatch's
  `AWS/SQS` `ApproximateAgeOfOldestMessage` (`ISqsOperations.GetOldestMessageAgeAsync`: statistic
  `Maximum`, dimension `QueueName`, last 15 minutes at a 60s period, latest-timestamp datapoint).
  That is a pure metrics read, so the side-effect objection above no longer applies. The queue with
  the largest age wins; `EnqueuedTime = now − age` (approximate: CloudWatch's 1-minute resolution
  plus a few minutes' publishing lag); `DeadLetterCount` is that queue's own `ApproxVisible`,
  matching the Kafka/Service Bus implementations (the oldest resource's count, not a total). A
  queue with no recent datapoint — metric lag, or LocalStack, which publishes no SQS metrics — is
  skipped, so against the local emulator the tile stays empty. Calls are bounded to DLQs with
  messages, at most 8 in flight. Failure policy: if every metric call fails (e.g.
  `cloudwatch:GetMetricStatistics` denied) the first failure propagates to the host like the other
  dashboard hooks; if only some fail, those queues are skipped so one throttled call doesn't blank
  the tile or mark an otherwise-healthy connection unchecked. No new IAM permission —
  `cloudwatch:GetMetricStatistics` was already required for SNS delivery-failure counts.)*

**LocalStack verification (2026-09-25).** The real `SqsOperations`, `SnsOperations` and
`MoveMessageToSourceCommandHandler` (not substitutes) were run by a throwaway harness against
LocalStack 3.8 (`SERVICES=sqs,sns,sts,cloudwatch,logs`). Covered: Test connection (STS); queues
including FIFO; typed and binary message attributes round-tripping; Move to source for standard
and FIFO queues; redrive start/list/cancel; topics; Subscribe with and without a filter policy;
filter-policy scope transitions, including the no-policy → nested `MessageBody` and cleared →
nested `MessageBody` cases (both failed before the `PlanFilterPolicyUpdates` fix above); publish
filtering matching the configured policy; the SNS delivery-failure metric read; delivery-logs
against a topic with no logging (reports `LoggingNotConfigured`, no exception); and the oldest-DLQ
age read (no datapoint, returns `null`). All pass after the fix. One LocalStack quirk worth knowing:
it accepts a scope-only `FilterPolicyScope` set on a policy-less subscription, but
`GetSubscriptionAttributes` doesn't report the scope until a policy is also set. The scope still
takes effect: the follow-up nested policy is accepted and body filtering works. Two small fixes came out of the
same run: `GetTopicAttributesAsync` now drops null-valued attributes (LocalStack returns
`DisplayName`/`DeliveryPolicy` as null), and `AwsPlugin`'s secret-keyed queue-list cache now
sweeps expired entries on every write, so rotated credentials don't stay in memory for the process
lifetime. **Still unverified on real AWS**: a scope-only `FilterPolicyScope` set on a policy-less
subscription; CloudWatch `AWS/SQS` metrics (LocalStack doesn't publish them, so the oldest-DLQ tile
has only been exercised on its empty path); and SNS delivery-status logs (LocalStack doesn't write
them, so the delivery-logs tab has only been exercised on its not-configured path).

## 6.8 RabbitMQ plugin (`SbConsole.Plugins.RabbitMq`) (2026-09-25)

SbConsole's fourth plugin: `RabbitMqPlugin : IPlugin`, `Id`/`ConnectionKind` = `"rabbitmq"`,
registered after AWS. Unlike AWS (three plans), the whole mockup surface (`SbConsole
RabbitMQ.dc.html`, frames 1a–1h) was built in one plan, because every screen shares one
connection/vhost picker and one management-API error model. Full design:
`docs/superpowers/specs/2026-09-25-rabbitmq-plugin-design.md`; plan:
`docs/superpowers/plans/2026-09-25-rabbitmq-plugin.md`.

**Two endpoints, reported separately.** AMQP (RabbitMQ.Client 7) moves messages; the management
HTTP API (`HttpClient` + System.Text.Json, no third-party client) is the only source of rates,
depths, bindings, nodes, shovels and policies. Test connection runs both probes concurrently and
returns two `ConnectionCheck`s, so the most common self-hosted misconfiguration (AMQP open, the
management plugin off or unreachable) reads as "partly configured", not as broken. No SDK change was
needed: `Success=true` with one failed check is the existing "valid but under-permissioned" shape.
Get and Publish deliberately use AMQP, not the management API's `/get` and `/publish`, so they keep
working when only AMQP is reachable.

**Connection secret.** The house `key=value;` string, but with every value percent-encoded
(`RabbitConfigParser`): a RabbitMQ password or vhost can legally contain `;` or `=`, which Kafka's and
AWS's unencoded convention would split. `RabbitConnectionSettings.From` applies the defaults (ports
and management URL follow `tls`, vhost `/`). Structured form: `RabbitConnectionFields`.

**Shape.** `IRabbitOperations` / `RabbitOperations` composes an internal `ManagementApiClient`
(unit-tested against real captured 3.13.7 JSON through a fake `HttpMessageHandler`) and an
`AmqpClient`. Handlers are thin and share one body (`HandlerRunner`) instead of repeating AWS's
try/log/friendly/audit block ~20 times. Pure logic lives in `Routing/`: `RoutingMatcher` (the
Publish dialog's live routing preview — AMQP topic semantics as a word-level matcher, headers
`x-match` variants, closest bindings), `DeadLetterTopology` (dead-lettering is a routing convention,
not a broker object: a DLQ is a queue bound to some queue's DLX — refined so a queue that itself has a
DLX is a work/retry queue, not a DLQ), `PolicyMatcher`, `NameSuggester`.

**Decisions worth knowing:**
- *Get has no true peek.* `basic.get` takes the message; Peek hands everything back with one
  `basic.nack(multiple, requeue)`, Consume acks. Cancellation or failure before that closes the
  channel, which requeues what was held. Peek is a query (no audit row); its side effect (the
  redelivered flag) is stated in the page byline. Consume is `Destructive`, confirmed and audited,
  and consumed messages exist only in page memory until requeued/republished — the page warns while
  any are held.
- *Publish is mandatory + publisher confirms.* The preview can block an unroutable publish before
  send ("caught before send"), but the broker's `basic.return` stays the authority.
- *Ready and Unacked are never summed*, anywhere.
- *Stale data stays visible* (dimmed, timestamped) when a refresh fails, rather than blanking — a
  blank table during a management-API blip looks like an emptied broker.

**Deviations from the mockup** (each because the data doesn't exist, not for effort): the Queues
"Nack /s" column is "Redeliver /s" (no per-queue nack rate in the API); no per-binding rates on queue
detail (no per-binding statistics); no shovel transfer rate or Pause/Resume (not in the shovel status
API / `rabbitmqctl`-only on 3.13); the purge dialog's "Oldest"/"Distinct routing keys" facts are not
shown (they'd need a side-effecting read first); Overview "churn" is since node start, not "last hour".

**Host hooks:** nav badges (Overview = nodes in memory/disk alarm, Queues = DLQ ready total), the
`Queues`/`Dead-lettered` dashboard metrics, per-queue resource metrics and needs-attention problems,
all from one 60 s cached broker snapshot (the AWS pattern). `GetOldestDeadLetterAsync` stays at the
SDK default: reading a message's age needs a `basic.get`.

**Local stack:** `docker compose --profile rabbitmq up -d` — RabbitMQ 3.13 with management + shovel,
seeded from `docker/rabbitmq/definitions.json` with the mockup's topology in vhost `/orders`; AMQP on
host port 5673 (the Service Bus emulator holds 5672). See `docker/README.md`.

## 6.9 Redis plugin (`SbConsole.Plugins.Redis`) (2026-09-28)

SbConsole's fifth plugin: `RedisPlugin : IPlugin`, `Id`/`ConnectionKind` = `"redis"`. Redis is not a
broker — no queue/topic model, no vhost — so the plan anchors on what Redis actually is: a keyspace
and a server whose memory pressure is the number that pages someone at 3am. Full design (all phases):
`docs/superpowers/specs/2026-09-28-redis-plugin-design.md`; this plan (Phase 1, tasks 1–10):
`docs/superpowers/plans/2026-09-28-redis-plugin.md`. It ships client library `StackExchange.Redis`,
Standalone/Sentinel connection handling, the Overview page, and these dashboard hooks — the Keys
browser, Streams (the closest thing Redis has to a dead-letter queue, via the pending-entries list),
Pub/Sub and Cluster topology are separate, later plans.

**Connection secret.** `"{topology}|{preferReplica}|{configurationOptionsString}"`
(`RedisConnectionSettings`). Unlike Rabbit/Kafka/AWS's house `key=value;` convention, Redis's own
client already has a full-featured connection-string format, so the configuration-options portion is
produced and consumed entirely by `ConfigurationOptions.Parse`/`.ToString()` — endpoints, TLS,
`ServiceName` and `DefaultDatabase` all round-trip through the real library, not a hand-rolled parser.
The one gap: `ConfigurationOptions` has no escape syntax for a comma inside a value (comma is its own
field separator — StackExchange/StackExchange.Redis#680, #883), so `User`/`Password` alone are
percent-encoded around that round trip, the same shape as RabbitMQ's percent-encoding precedent
(§6.8) for a value its format can't otherwise carry safely. This is a corrected design: the first
pass replaced the library round trip with a hand-rolled parser entirely, which a review caught before
merge as a regression against the spec's own comma-safety goal — `ConfigurationOptions` genuinely
can't escape a comma, so the fix was to percent-encode only the two credential fields, not to
abandon the library for everything else.

**Shape.** `IRedisOperations`/`RedisOperations` is the one thin wrapper every handler goes through
(one shared instance per docs/design.md §6.8's convention — every method takes the raw secret as its
first parameter). All the actual logic is pure and separately testable: `RedisInfoParser` turns
parsed `INFO` sections into `ServerOverview`, `RedisMemoryByPrefixSampler` groups a `SCAN`+`MEMORY
USAGE` sample into top-N prefixes (with an "other" bucket), and `RedisSlowLogParser` turns a raw
`SLOWLOG GET` `RedisResult` into `SlowLogEntry` rows — none of the three touch the network, so they're
unit-tested directly with no broker. `RedisOperations` itself stays thin: connect, call the
StackExchange.Redis API, hand the raw result to the matching parser.

**Overview page** (`Pages/Overview.razor`, `Overview/GetOverviewQueryHandler`,
`GetMemoryByPrefixQueryHandler`, `GetSlowLogQueryHandler`): a connection picker plus a tile row for
used memory (with % of maxmemory), hit ratio, ops/sec, and total evicted keys, followed by a
memory-by-prefix sample table and a slow log table. `ServerOverview` itself carries more fields
than the page renders (version, mode, connected replicas, max-memory policy, keyspace per DB) —
those are parsed and available but not yet surfaced in this page's UI. A failed memory-by-prefix or
slow-log sub-call shows an inline warning next to that table (including ACL/NOPERM text) rather than
silently rendering an empty table. Byte formatting uses binary (1024) scaling throughout, matching
Redis's own `INFO`/`MEMORY` convention rather than decimal (1000) SI units.

**Host hooks:** `GetDashboardMetricsAsync` reports no metrics (`[]`) — the host's dashboard sums
same-label metrics across every connection of a plugin, which doesn't work for a percentage (two
Redis connections at 60% and 90% memory would render as "150"); the real memory-pressure signal is
already surfaced correctly, per-connection, via `GetDashboardProblemsAsync` below, since problems
are listed individually rather than summed.
`GetDashboardProblemsAsync` raises one "Warning" when used memory crosses 90% of `maxmemory` (no
problem when `maxmemory` is unbounded — a `0`/`null` cap means eviction can't trigger, so there's
nothing to warn about), linking to `/p/redis/overview`. It calls `RedisOperations` directly rather
than through DI, matching the SDK-hook convention elsewhere in this codebase (the host resolves no
scoped services when calling these). The public method constructs the real `RedisOperations`;
`GetDashboardProblemsAsyncForTesting` is an internal seam taking `IRedisOperations` so tests substitute
it directly — the same testability shape as RabbitMQ's `GetCachedSnapshotAsync` (§6.8).
`GetNavBadgeAsync`, `GetResourceMetricsAsync` and `GetOldestDeadLetterAsync` stay at the SDK default:
nothing in Phase 1's surface (a single Overview page, no per-resource concept yet) has a badge, a
named resource to track history for, or a dead-letter analogue — those wait for the Streams plan.

**Local stack:** `docker compose --profile redis up -d` — Redis 7 (`redis:7-alpine`), password-
protected (`--requirepass`), standalone; `redis-init` seeds a handful of sample keys (two
`session:u:*` hashes, a list, a sorted set, a set) idempotently on every `up`. See `docker/README.md`.

## 7. Error handling

- Handlers return typed results (`Result<T>` with error category: `NotFound`,
  `Conflict`, `AuthFailure`, `Transient`) — no exceptions across the UI
  boundary.
- Service Bus transient failures: rely on the Azure SDK's built-in retry;
  surface a clear "namespace unreachable" state instead of indefinite
  spinners.
- Audit rows are written for attempted destructive commands even on failure,
  with the outcome recorded.

## 8. Testing

- TDD: failing test first, per task.
- Core handlers: xUnit + FluentAssertions + NSubstitute (substitute
  `IAuditWriter`, stores, clock).
- Components: bUnit (confirmation dialog, connection forms, peek grid).
- Service Bus plugin (Queues, Topics & Subscriptions, Dead-letter overview —
  §6): unit tests only against a substitute of `IServiceBusOperations` — no
  Testcontainers, no real AMQP traffic, for any of the three.
- Integration: Testcontainers running the official Azure Service Bus emulator
  — real peek/send/DLQ flows over AMQP. Deferred past all three (§6); picked
  up once the plugin's shape has proven out across queues, topics, and the
  cross-connection overview alike.
- Kafka plugin (Topics, §6.5): same deferral, same reasoning — unit tests only against a
  substitute of `IKafkaOperations`, no Testcontainers, no real broker traffic.
- AWS plugin (Queues, §6.7; Topics, §6.7.1): same deferral, same reasoning — unit tests only
  against substitutes of `ISqsOperations`/`ISnsOperations`, no Testcontainers, no real
  AWS/LocalStack traffic. LocalStack in `docker-compose.yml` is for manual local dev only.
  The completion pass (§6.7.2) keeps the same rule; its two timer loops (Queues auto-refresh,
  `QueueDetail`'s redrive-progress poll) are tested by calling their single step directly
  (`AutoRefreshTickAsync`/`PollRedriveTasksAsync`), the NavMenu precedent below, and the dashboard
  cache via its injectable-clock/fetcher overload.
- Connections page redesign (§11): `ConnectionEditor`, `AwsConnectionFields`, and the two-pane
  `Connections.razor` are covered by bUnit only — the same pattern as every other Blazor component
  in this codebase, not a new deferral.
- `NavMenu`'s badge-refresh loop (§5, §6.3) is tested by calling its refresh
  method directly, not by waiting out its real 60-second timer — the loop
  itself is a thin wrapper (`while` + `Task.Delay` + the same call) around a
  single testable step.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every
  commit.

## 9. Stack (non-negotiable)

.NET 10, C# latest, nullable enabled, warnings as errors. Blazor Interactive
Server. EF Core + SQLite (WAL). Minimal APIs. MudBlazor. xUnit +
FluentAssertions + NSubstitute + bUnit + Testcontainers.

## 10. Theming (2026-09-11)

The app's visual theme is derived from **Nocturne**, a dark-first design
system produced during UI exploration (tokens and rationale in
`styles.css`/`readme.md` of the Nocturne kit). Scope is **theme tokens only**:
colors, typography, and corner radius through MudBlazor's own `MudTheme` API
(`PaletteDark`/`PaletteLight`, `Typography`, `LayoutProperties`). Shadow
customization was left as an explicit scope cut — see below. No custom CSS is
added to force MudBlazor
components into Nocturne's specific shapes (e.g. outlined-only buttons, the
fading-gradient table rule) — components keep their native MudBlazor shape,
just recolored and retyped.

- **`src/SbConsole.Web/Theming/NocturneTheme.cs`** — two static `MudTheme`
  instances, `Dark` and `Light`.
- **Dark** maps Nocturne's tokens directly: `Background` #161826, `Surface`/
  `DrawerBackground`/`AppbarBackground` #232532/#161826 (flush shell, no
  separate nav treatment, matching Nocturne's borderless `.nav`), `Primary`
  #9184d9 with `PrimaryDarken`/`PrimaryLighten` at the accent ramp's 600/400
  steps (#796cbf/#b5abfc), `TextPrimary` #e9e9ed. `Divider` and
  `TextSecondary` use 8-digit alpha hex (`#e9e9ed29`, `#e9e9ed8c`) rather than
  a manually flattened opaque color, mirroring Nocturne's own
  `color-mix(..., transparent)` approach so the mix stays correct if the
  underlying color is ever retuned.
- **Light** is a derived counterpart — Nocturne itself is dark-only, but its
  own wireframe notes for a light variant say exactly how to invert it:
  surfaces lift toward white instead of receding, and the accent steps down a
  ramp level for contrast on a light ground. Concretely: `Background`
  --color-neutral-100 #f3f5fe, `Surface` white (lifting above the page),
  `Primary` --color-accent-700 #5d5294 (not the raw #9184d9, which under-
  contrasts on a light ground), `TextPrimary` --color-neutral-900 #292b31,
  `Divider`/`TextSecondary` as alpha-hex over the dark text color.
- **Semantic colors** (`Error`/`Warning`/`Success`/`Info`) have no source in
  Nocturne — it is deliberately a single-accent system, but the app already
  needs these for the audit log's risk chips, prod-tag warnings, and
  unreachable-connection states. Chosen here, muted to match Nocturne's
  low-chroma-outside-the-accent rule rather than pulled from any token file.
- **Typography**: Inter (loaded the same way Nocturne loads it — a Google
  Fonts `<link>`) replaces MudBlazor's default Roboto, with heading weight
  500 per Nocturne's `--font-heading-weight`. MudBlazor's own type-scale
  sizes are kept (tokens-only scope; not pixel-matching Nocturne's scale).
- **Shape/elevation**: `LayoutProperties.DefaultBorderRadius` = 8px
  (`--radius-md`). Shadow customization (`MudTheme.Shadows`/`Shadow.Elevation`)
  was **not** done — it was an explicit, plan-permitted scope cut, not an
  oversight. `Shadow.Elevation` is 26 raw CSS `box-shadow` strings (one per
  elevation level, 0-25); hand-tuning all of them to Nocturne's dark/light
  surfaces would have been significant unplanned effort with no spec input
  on what those shadows should look like, so both palettes keep MudBlazor's
  default `Shadow` values.
- **Login page parity**: `EmptyLayout` (Plan 1) originally instantiated its
  own bare `<MudThemeProvider />`, disconnected from `MainLayout`'s
  dark-mode-resolution logic (Plan 2 Task 8) — the pre-login page would
  otherwise have stayed on MudBlazor's stock theme forever. Fixed by
  extracting the dark-mode/system-preference resolution logic out of
  `MainLayout` into a shared `ThemedRoot` component both layouts use, so
  Login renders in whatever theme (light/dark/system) the `theme.mode`
  setting already specifies. (A separate, unrelated bug found in the same
  pass — `Program.cs`'s auth fallback policy was blocking anonymous access to
  static assets, so the themed Login page still failed to render in a
  browser — was also fixed: static asset endpoints are now explicitly
  `AllowAnonymous()`.)
- A handful of tests pin the theme's key token values (background/primary/
  etc. hex codes for both palettes) so an edit to `NocturneTheme.cs` can't
  silently drift from these decisions without a test catching it.
- **Glass shell (2026-09-14):** the app-bar and drawer no longer stay flush with
  `--mud-palette-background` — `wwwroot/app.css` now paints them as a translucent,
  blurred gradient (`.mud-appbar`, `.mud-drawer`) over a new ambient glow added to
  `body`'s background, with `.page-content` also made partially translucent so the
  glow bleeds through the content area too. This supersedes this section's earlier
  "flush shell, no separate nav treatment" line for the app-bar/drawer specifically —
  the underlying palette tokens (`Background`, `Surface`, `Primary`, etc.) are
  unchanged; only a new CSS layer sits on top of them. Every new color is built via
  `color-mix()` over the existing tokens (the same alpha-mixing approach already used
  for `Divider`/`TextSecondary`), so both Light and Dark theme render correctly
  without any `prefers-color-scheme` branching. Full rationale and the approved
  mockups' color/intensity choices: `docs/superpowers/specs/2026-09-14-glass-shell-design.md`.
  Because the glow lives on
  `body`, it also renders behind Login's `EmptyLayout` card — an approved
  consequence of the whole-app scope, not an oversight.

## 11. Connections page redesign and structured connection forms (2026-09-22)

The Connections page evolved from a basic modal-dialog edit flow (§5.1, v1.1) to a
two-pane layout with an inline editor panel and richer test-connection diagnostics,
enabled by three new SDK contracts for structured, per-plugin connection-form UI.
Built in a ten-task plan (`docs/superpowers/plans/2026-09-22-connections-page-redesign.md`)
that added the SDK contracts (Task 1), persisted connection summaries (Tasks 2-3),
implemented the AWS plugin's `AwsConnectionFields.razor` and enriched test
diagnostics as the first adopter (Tasks 4-7), and changed the host's
test-connection flow via `ConnectionEditor.razor` and the two-pane layout
(Tasks 8-9). Full design spec:
`docs/superpowers/specs/2026-09-22-connections-page-redesign-design.md`.

**SDK additions (§3 expansion)** — **(v1.4)**:

- `IPlugin.ConnectionFormComponentType` — optional: a `System.Type?` of a Blazor
  component this plugin wants rendered on the Connections page's editor in place
  of the host's generic flat-secret-string textbox. Returning `null` (the default)
  means "no custom form" — plugins written before this member existed need no change.
  **Important implementation detail:** there is **no SDK-level base class** for this
  component. The host discovers the component's parameters by name via
  `DynamicComponent`, and the component must declare exactly three parameters:
  `InitialSecret` (`string?`, read once in `OnInitialized`), `SecretChanged`
  (`EventCallback<string>`), and `IsProd` (`bool`). This naming convention is
  documented on the property's XML doc comment, matched by name at runtime, not
  enforced by a shared type. Rationale: `SbConsole.Sdk` has zero dependencies
  beyond BCL and `Microsoft.Extensions.*.Abstractions` (CLAUDE.md §2) — adding a
  `ComponentBase`-derived base class would require `Microsoft.AspNetCore.App` in
  the SDK, forcing that dependency on every plugin unnecessarily. A plugin's own
  form component (built in the plugin project, which already references the app
  framework) needs no SDK base type; it just matches the named-parameter contract
  documented in the `IPlugin` doc comment, same as any Blazor component with a
  published interface.
- `IPlugin.GetConnectionSummary(string secret)` — optional: returns a safe,
  non-secret dictionary (e.g. `{"Region": "eu-west-1"}`) of display fields
  extracted from a connection's secret. Returning an empty dictionary (the default)
  means "nothing to show." Stored in plaintext in the database (unlike the secret
  itself) and shown as small chips on the Connections list. A plugin must never
  include credential-shaped keys here — the dictionary is not encrypted.
- `ConnectionTestResult` enrichment (§3 expansion) — now includes two optional
  fields: `Identity` (`string?`) for the authenticated principal (e.g. `"arn:aws:iam::123456789:user/alice"`),
  and `Checks` (`IReadOnlyList<ConnectionCheck>?`) for structured diagnostic probes
  (`ConnectionCheck(string Label, ConnectionCheckStatus Status, string? Detail = null)`
  with `ConnectionCheckStatus` being `Passed` or `Failed`). Together these enable
  richer test results: a simple success with identity, a simple error message, or a
  valid-but-under-permissioned state (Success=true, at least one Failed check). The
  richer diagnostics are rendered in the editor panel; a plugin that never populates
  these (Service Bus, Kafka today) renders exactly the plain single-line message it
  always has.

**Core changes** — Connections table schema adds one new column (§4 expansion):

- `Connection.SummaryJson` (`string?`, nullable) — persisted once at Create/Update
  time by calling the matching `IPlugin.GetConnectionSummary`, then deserialized
  into the connection's `Summary` property (an `IReadOnlyDictionary<string, string>`)
  and displayed as chips on the list.

**Web/UI changes** — Connections page two-pane redesign (§5.1 expansion):

- **Layout**: replaced the modal Add/Edit dialog with an inline editor panel. The
  page is now a flex row: the left side (flex:1) holds the connections table, the
  right side (width:420px, flex:none) conditionally renders a `MudPaper` containing
  `ConnectionEditor.razor` when editing. **Not using `MudDrawer`:** the mockup's
  panel has no slide/overlay animation — it's a plain persistent side-by-side split.
  Implementing this as a `MudDrawer` (Anchor.End, DrawerVariant.Persistent) would
  require the drawer to be a direct child of the single `MudLayout` in MainLayout,
  per MudBlazor's layout-root assumptions; `Connections.razor` renders inside
  `MudMainContent`'s `@Body`, one level further in, and nesting a second `MudDrawer`
  there is untested and potentially fragile. A conditionally-rendered `MudPaper` in
  a flex row achieves the same visual result with no dependency on drawer positioning
  logic.
- **`ConnectionEditor.razor`** (new component, replaces modal dialog) — a
  side-panel form for creating or editing a single connection. When adding: shows
  Kind dropdown (enabled), Name/Secret textbox or custom form, Tag management, and
  a Test button. When editing an existing connection: Kind dropdown is disabled,
  optionally shows an alert warning that "editing any field replaces the entire
  stored secret — fill in every field your chosen auth mode needs" (surfacing the
  pre-existing write-only contract), and Test uses a different code path: for a new
  unsaved connection, or an existing connection whose fields have been edited since
  the panel opened, tests the in-progress secret directly via the plugin (no
  persistence, no audit — there's nothing saved yet that matches the edited value);
  otherwise (an existing connection whose fields are untouched), tests the persisted
  secret via `TestConnectionCommandHandler` (writes
  `LastTestSucceeded`/`LastTestedAt`/`LastTestError` and an audit row). This
  redesign is also where the list's row-level Test action was removed — the old
  modal itself never had a Test button; Test lived as a separate action on each
  list row. Testing now happens only inside the editor panel, whether adding or
  editing, never from the list.
- **Connections list enhancements** — adds a Summary column (showing chips from
  `Connection.SummaryJson`), leveraging `IPlugin.GetConnectionSummary`. The list's
  Status column itself is unchanged (`StatusText`'s plain "Never tested"/"OK"/error-text/"Failed"
  switch, shipped in §4) — the richer `Identity`/`Checks` diagnostics render only inside
  `ConnectionEditor`'s inline test-result panel (success shows "Credentials valid · {identity}"
  plus one icon + label line per check, with a Detail note if present; failure shows the
  plain `ErrorMessage`), not in the list row.
- **`AwsConnectionFields.razor`** — AWS plugin's custom connection form, the first
  adopter of the new `ConnectionFormComponentType` hook. A structured alternative to
  the flat-secret textbox: shows Region autocomplete (searchable by system name or
  display name, e.g. "eu-west-1" or "Europe"), Auth Mode toggle (access-keys /
  assume-role / default-chain), mode-specific fields (access-key ID/secret/session
  token; role ARN/external ID/session name; or a note for default-chain), and an
  Advanced section (custom endpoint URL, path-style addressing toggle, prod-tag
  warning when endpoint is set). Parses the secret once in `OnInitialized` via
  `AwsConfigParser.Parse`, emits structured config via `AwsConfigParser.Serialize`
  when any field changes, and reads the IsProd parameter to conditionally show
  warnings. Same parameter-matching-by-name contract as any other custom form.

**Testing (§8 expansion)**:

- `ConnectionEditor.razor`, `AwsConnectionFields.razor`, and the Connections page
  are covered by bUnit component tests only — matching the existing pattern for
  every other Blazor component in this codebase (confirmation dialog, peek grid,
  etc.). No Testcontainers, no AWS/LocalStack traffic; handlers and operations are
  substituted directly.

**Out of scope, carried over from the design spec (§3, §8)**:

- SNS/Topics in the AWS plugin — out of scope for this redesign specifically (it
  doesn't touch the connection model at all), and shipped separately the same day
  as its own plan; see §6.7.1.
- Persisted denied-action enforcement (a capability-set stored in the connection
  for later use by the UI to e.g. hide a button when `TestConnectionAsync` reports
  permission denied — test results remain text-only today, not used to gate UI
  actions).
- IAM Policy Simulator integration (for pre-flight "will this action succeed" checks
  in the AWS plugin).
- A host-level Region field (plugins can expose it via `GetConnectionSummary`, as
  AWS does, but there's no first-class integration).
- Service Bus and Kafka adopting `ConnectionFormComponentType` or enriched
  `ConnectionTestResult` Checks (today they use the default empty form and simple
  text results). AWS stands alone as the first adopter while the shape proves out.

## 12. Marker-based DI registration (2026-09-29)

Every handler and service used to be registered by a hand-written line — ~130 of them
across `Program.cs` and the five plugins' `ConfigureServices`. Forgetting one compiled fine
and failed only at runtime, as an HTTP 500 when a page injected it (the original
`ProgramDiRegistrationTests` bug). The Redis plugin even carried a comment warning
about it.

**Shape.** `SbConsole.Sdk` now ships three empty lifetime markers —
`IScopedService`, `ISingletonService`, `ITransientService` — and
`IServiceCollection.AddMarkedServices(Assembly)`, which reflects over the assembly and
registers every concrete, non-generic class that implements exactly one marker. They
live in the Sdk (not Core) because plugins may reference only the Sdk.

**What a marked class is registered as.** Its "service interfaces" are every interface
it implements except the markers and framework (`System.*` / `Microsoft.*`) interfaces:
- none (every query/command handler) → registered as the class itself;
- exactly one (e.g. `EfAuditWriter : IAuditWriter`) → registered as that interface only,
  with `ImplementationType` set, so the descriptor is identical to the old hand-written one;
- several → the class is registered once and each interface forwards to it, so a
  singleton or scoped instance isn't silently duplicated per interface.

A class implementing two markers throws at startup instead of picking one arbitrarily.

**Where it's called.** `Program.cs` scans Core and Web. Each plugin's `ConfigureServices`
is now one line, `services.AddMarkedServices(typeof(XxxPlugin).Assembly)`. It stays in
the plugin, not in the host's `AddSbConsolePlugin<T>()`, so the plugin still owns what it
registers and plugin unit tests that call `ConfigureServices` directly keep working
unchanged.

**Still registered by hand**, because they need an instance or a factory:
`AesGcmSecretProtector` (built from the bootstrap key), `TimeProvider.System`, the
keyed per-plugin `IPluginStore`, `PluginRegistry`, the plugins themselves, and the
`MetricsCollectorService` hosted service.

**Guard.** `ProgramDiRegistrationTests` now resolves every public `*QueryHandler` /
`*CommandHandler` in Core and in every registered plugin assembly from the real
`Program.cs` container. A class missing its marker fails there, not in production.

## 13. Navigation: icon rail + contextual drawer (2026-09-30)

**Why.** The flat sidebar listed every plugin's pages under its own section. With five
plugins that was 20+ rows plus the console's own items, and it scrolled. Of the three
options in the "SbConsole Navigation" mockup (1a icon rail, 1b plugin switcher, 1c
connection-first), 1a was picked. It keeps the current routing and the plugin-as-a-unit
model, and the rail only ever grows by one icon per plugin.

**Shape.** `MainLayout` is now a plain flex shell, with no `MudAppBar` or `MudDrawer`.
`NavMenu` renders two columns:
- **Rail (56px):** logo, Dashboard / Connections / Audit, one monogram per plugin, then
  `+` (Plugins). Settings and the drawer toggle sit at the bottom.
- **Drawer (196px):** only the selected plugin's name, version and pages (with badges),
  then its saved connections with a last-test status dot.

**Which plugin the drawer shows.** It's the plugin owning the current URL (`/p/{Id}/...`).
On a host page it keeps whichever plugin it showed last, so the drawer doesn't blank out,
and it falls back to the first registered plugin. A badge on a plugin the drawer isn't
showing surfaces as a dot on that plugin's rail icon.

**SDK.** `IPlugin.Monogram` (optional, default null) is the rail's two-letter mark. When
it's null the host uses the first two letters of `DisplayName`. That's the same
default-member pattern as the other optional plugin hooks, so existing plugins need no
change.

**Loading order.** `RefreshBadgesAsync` lists every plugin's connections first, one local
DB read each, and renders before making the badge calls. Those calls reach out to each
broker and can take seconds when one is unreachable.

**Deferred from the mockup.** The ⌘K search box needs a command palette that doesn't
exist yet. A connection row links to `/connections` rather than switching the page's
connection, because the plugin list pages pick their connection internally and don't read
a `connectionId` query parameter.

## 14. Dashboard: fleet-first layout (2026-09-30)

**Why.** The Dashboard summed same-label metrics across every plugin into one row of
tiles. So "Topics" was Service Bus + Kafka added together, and Redis contributed nothing
(a percentage can't be summed). The header still read `q · t/sub · dlq`, as if only
Service Bus existed. Problems rendered as a flat stack of alert boxes. Of the two options
in the "SbConsole Dashboard" mockup (1a fleet-first, 1b triage-first), 1a was picked, with
its 1c all-clear and 1d checking states.

**Shape.** `Home.razor` has three regions under a 48px header. The header shows
`N connections · P plugins · K need attention`.
- **Fleet:** one card per registered plugin. Each card shows the plugin's monogram and
  name, and its metrics with labels verbatim, summed only across *that plugin's*
  connections. It also has one status dot per connection (ok / warn / bad / unchecked /
  pending) and a problem-count pill colored by the worst severity. The card links to the
  plugin's first page.
- **Needs attention:** one dense triage table sorted unreachable → plugin error → plugin
  warning → unchecked. Each row carries the plugin monogram and the connection name. An
  unreachable connection has Retry/Edit in its row. A connection that couldn't be checked
  gets its own row naming it, replacing the old anonymous "couldn't check N connection(s)"
  alert. An unreachable connection is listed once, even when its plugin check also fails.
  Severity filter chips (All / Errors / Warnings / Unchecked) narrow the list. With nothing
  to report there's one quiet all-clear row, not a green banner.
- **Side column:** `WallboardWidget` restyled as a condensed "Backlog" card (stats, 1h
  sparkline with the outage rule, growth summary), then recent Activity.

The namespace chips are gone; the fleet cards' dots carry that information.

**Progressive checks (1d).** Checks still run concurrently. Each result is now rendered as
soon as it lands. Still-running connections show as pending rows ("checking 3 of 12…"),
so one slow broker no longer blanks the page behind a progress bar. A load generation
counter drops results from a load that a Refresh or Retry has superseded.

**SDK.** No contract change: `GetDashboardMetricsAsync` / `GetDashboardProblemsAsync` are
untouched, and only their doc comments changed to describe per-plugin display. The
monogram fallback moved from `NavMenu` to a shared `PluginMonogram.Of`, so the rail, the
fleet cards and the triage rows agree on it.

**Deferred from the mockup.** Redis still reports no dashboard metrics. Memory % needs a
non-integer metric, and `PluginDashboardMetric.Count` is an `int`. There's also no SQLite
plugin, and unchecked rows have no "Logs →" link because there's no logs page. The 1c
note about when the last problem cleared needs problem history that the host doesn't
keep.

## 15. SQLite plugin (`SbConsole.Plugins.Sqlite`) (2026-10-13)

Spec: `docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md`; plan:
`docs/superpowers/plans/2026-10-13-sqlite-plugin.md`. Phase 1 is a read-only Overview + Tables
browser; write mode / Query (phase 2) and backups (phase 3) follow.

- **First plugin that opens host files.** A SQLite connection is a server-side path, so the SDK
  gained `IHostPathPolicy`, implemented in Core (`HostPathPolicy`) from `SBC_SQLITE_ROOTS`
  (read by `BootstrapOptions`, still the only env reader). It is **fail-closed** (no roots → nothing
  opens), resolves `..` and every symlink before a separator-aware root match, always refuses
  SbConsole's own DB and its sidecars, and hands back the resolved path, which is the only path the
  plugin opens. Core owns this rather than the plugin because only Core knows where its own DB is,
  and a plugin shouldn't learn host env var names. **Known limitation:** a hard link to SbConsole's
  own database placed under a root is not detected, because the policy compares resolved paths, not
  file identity; creating one needs write access to both locations.
- **`IPlugin.Initialize(IServiceProvider)`.** Plugins are constructed with `new()`, so instance hooks
  (`TestConnectionAsync`, dashboard hooks) had no DI. The new optional hook runs once after
  `app.Build()` (`PluginRegistry.InitializeAll`); the SQLite plugin resolves its operations there and
  fails closed until then. Existing plugins are unaffected (default no-op).
- **Statement timeout needs two mechanisms.** Verified against Microsoft.Data.Sqlite 10.0.12: a
  cancellation token (or `SqliteCommand.Cancel()`) does not stop a running statement; the raw
  `sqlite3_interrupt` does, but an interrupt that fires while no statement is running is lost. So
  `Run` also installs `sqlite3_progress_handler` (every 1000 VM instructions; it aborts when the
  linked token is cancelled) and checks the token before running the work. Both abort with
  SQLITE_INTERRUPT (code 9).
- **`foreign_keys` is ON by default** on Microsoft.Data.Sqlite / bundled `e_sqlite3` connections,
  unlike the SQLite CLI, so the UI copy says "per-connection · on for this console's connections".
  The demo seed script uses Python's `sqlite3`, where it is off by default, which is why it turns it
  on before the cascading delete.
- **`dbstat` is absent** from the bundled `e_sqlite3`, so size-by-table falls back to
  `sqlite_stat1` row estimates; sizes appear automatically if a future build includes it.
- **`trusted_schema = OFF`.** Every connection runs `PRAGMA trusted_schema = OFF` right after
  `busy_timeout`, as defence in depth against hostile schemas (views/triggers/defaults calling
  SQL functions) in untrusted files; the full suite passes with it.
- **Secret lookup failures** (e.g. a rotated `SBC_DATA_KEY`) in the two command handlers go through
  `HandlerRunner.TryResolveSecretAsync`: logged, returned as a friendly `PluginResult.Fail`, and
  *not* audited, because nothing ran (same as "Connection not found").
- **Path policy limits.** The check is check-then-open; opening the resolved path removes symlinks
  that existed at check time but not a new symlink raced in by someone with write access under a
  root. Roots must never contain SbConsole's own data directory.
- **Testing exception.** `SqliteOperations` is tested against temp-file databases (no Docker, no
  network) — the only honest way to test PRAGMA parsing, read-only mode and interrupts. Handlers and
  pages still use a substituted `ISqliteOperations`, as every other plugin does.
- **CSV export** is built server-side and handed to the browser through a `DotNetStreamReference`
  and the plugin's `sqlite.js` module — the host has no plugin download endpoint, and this didn't
  justify adding one. It is capped at 100,000 rows **or** 50 MB (`CsvExport.MaxBytes`), whichever
  comes first, and written straight into a `MemoryStream`. The audit detail says which cap applied.
- **Dashboard problems** are three: Error "Integrity check failed", Warning for high free-page
  share, and Warning "Foreign key violations" when the stored integrity check found any.
- **Connection form** shows field-level validation errors (empty path, `;` in the path, timeouts
  below 1) instead of silently keeping the last good secret.
- **Local dev stack.** `docker/sqlite/seed.py` builds a ~35 MB `reports.db`; the compose `sqlite`
  profile runs it into the `sqlite-demo` volume, which the app container mounts at `/sqlite` with
  `SBC_SQLITE_ROOTS=/sqlite` (not `/data`, which holds SbConsole's own database and keys).

### 15.1 SQLite phases 2 and 3 — Query, write mode, DDL, repair, backups (2026-10-13)

Spec §11 of `docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md`. The plugin now has four
pages (Overview, Tables, Query, Backups) and 16 audited actions.

- **ATTACH is impossible from user SQL.** Every connection that runs user-supplied SQL (Query,
  write mode, Create table) sets `SQLITE_LIMIT_ATTACHED = 0` right after open. Without it,
  `ATTACH '/any/file'` or `VACUUM INTO '/any/path'` would walk straight past `IHostPathPolicy`
  (`VACUUM INTO` even works on a read-only connection — verified against the bundled
  `e_sqlite3`). The script splitter also refuses `ATTACH`/`DETACH`/`VACUUM` and transaction control
  up front, so the message is friendly rather than "too many attached databases". Plugin-owned
  connections that need `VACUUM INTO` (backups, restore's pre-restore copy) skip the limit and never
  run user SQL.
- **Write-mode model.** Read-only is the default everywhere, including the Query page on a
  read-write connection. Writing needs `Mode=ReadWrite` in the saved secret (re-read by the
  operations; the page's switch is only a convenience), opens with `SqliteOpenMode.ReadWrite`
  (never `ReadWriteCreate` — a missing file is never created), and on a prod-tagged connection
  asks for the typed name twice: to enter write mode and before running.
- **Dry run as the rollback.** "Run in transaction" is disabled until a dry run of the *same text*:
  `BEGIN IMMEDIATE`, every statement, per-statement `changes()` and the `total_changes()` delta,
  then always `ROLLBACK`. It is the real statement, so it works for any DML/DDL and resolves
  trigger and FK-cascade rows (`total − direct`) that a `COUNT(*)` rewrite would miss; the cost
  (the work is done twice) is stated in the UI. The dry run is a query: nothing durable changes,
  so it writes no audit row. The run itself is one `BEGIN IMMEDIATE … COMMIT`; any error rolls back.
- **Backup / restore model.** A backup is `VACUUM INTO <BackupDir>/<base>-<yyyyMMdd-HHmmss>.db` on
  a read-only connection (writers keep going; the copy is compacted), then verified
  (`integrity_check`, SHA-256, tables, rows ≈, schema fingerprint). `BackupDir` is per connection
  ("Enable backups to"), passes the same path policy as the file — inside a root, never a root
  itself, may be the live file's own directory — and is never created by the plugin. History lives
  in `IPluginStore` (`backups:<connectionId>`, newest first, capped at 200) and keeps failed rows
  too, because a failure nobody sees is the expensive kind. Restore needs a read-write connection
  and a typed confirm on prod: verify the backup → `VACUUM INTO` a `<file>.pre-restore-<stamp>` copy
  next to the live file → online backup API into the live file (writers blocked for the copy, WAL
  handled by SQLite) → integrity check of the result. Retention is grandfather-father-son over
  successful backups (newest per UTC day / ISO week / month; the newest success is always kept;
  a manual backup younger than KeepDaily days is exempt — never pruned and never taking a slot
  from a scheduled one, because the scheduler's prune minutes after a "before migration" copy
  once deleted it; older manual backups go through the normal rules), and only files named in the history, re-checked inside `BackupDir`, are ever deleted.
- **Scheduler and the background actor.** `SqliteBackupScheduler` is a hand-registered
  `BackgroundService` (the one thing in the plugin the markers can't register) ticking every
  minute off `TimeProvider`, with a public `TickAsync(now)` for tests. Per connection with a
  backup dir and an enabled policy, once today's `DailyAtUtc` slot has passed and no scheduled
  record exists at/after it: back up, verify, prune — the policy is the consent, so the scheduler
  prunes without confirmation. The policy carries `EnabledSince` (stamped by the save handler when
  it is switched on, cleared when switched off) so "missed N" never counts slots from before the
  schedule existed; policies stored before the field read it as null and count all 7 days. There is
  no signed-in user in a background scope, so `EfAuditScope`
  now records actor `system` there instead of throwing; the history record says `system` too.
- **Growth sampling.** `growth:<connectionId>` holds file-size samples (≤ 1 per hour, 30-day
  retention) taken by the scheduler tick and by every Overview load — the only write a query
  handler makes, to the plugin's own store, so it isn't audited. The Overview's "Growth — 30 days"
  card shows first → last, delta, per-day rate, an inline SVG sparkline (stroke from the palette
  via CSS) and "~N days at this rate" from `DriveInfo` of the file's volume. The Last backup tile
  and the growth card load beside table sizes, after the tiles, so the tiles never wait.
- **Query on a backup.** Backups "Open read-only" links to `/p/sqlite/query?backup=<file name>`;
  Run / Explain / Export open that file inside the connection's `BackupDir` (bare names only,
  policy-checked) and write mode is unavailable. No temporary host connection is created — the SDK
  has no seam for one.
- **Known limitations.**
  - *No Backups nav badge.* §11.7 wanted "hours since the last successful backup"; the SDK's
    `GetNavBadgeAsync` hook receives only `(href, secret)` — no connection id, no `IPluginStore` —
    so the history can't be read there. The dashboard's "Backup overdue" / "Last backup failed"
    problems carry the signal instead.
  - *Salvage is best-effort, not `.recover`.* `sqlite3_recover` is a CLI extension that the bundled
    build doesn't ship, so "Recover into new file" copies DDL and then rows one at a time, skipping
    what errors, into a new file in `BackupDir`; it never touches the live file.
  - *Download streams through the circuit.* The file goes to the browser via
    `DotNetStreamReference` over the SignalR connection (no plugin download endpoint) — fine at demo
    sizes; the UI says so over 1 GB.
  - *Schema "N objects differ"* needs the backup's per-object fingerprints, which only a fresh
    verify returns (history stores just the overall fingerprint), so the detail panel says
    "identical" / "differs" from history and counts objects after **Verify again**.
- **Demo seed.** `docker/sqlite/seed.py` timestamps events from ~13 months ago up to the moment it
  runs (so the mockup's 90-day retention `DELETE` always has old rows of the churned tenant to dry
  run, plus recent rows it keeps), adds `reports` / `report_runs` (`ON DELETE CASCADE`), a 0-byte
  `new.db` for the empty state, and a world-writable `backups/` for `BackupDir`. Each piece is
  created only when missing; `--force` rebuilds the two files.

## 16. Opt-in demo connections (`SBC_DEMO_CONNECTIONS`) (2026-10-13)

Demo and dev stacks needed every sample connection typed into the Connections form by hand after
each volume reset (the API has no create endpoint — only `/api/v1/health`). `SBC_DEMO_CONNECTIONS`
names a JSON file of connections the host creates at startup:

```json
[{ "name": "sqlite-shop", "kind": "sqlite", "secret": "Path=/sqlite/shop.db;Mode=ReadWrite;BusyTimeoutMs=5000;StatementTimeoutS=30;BackupDir=/sqlite/backups", "tags": ["staging"] }]
```

- **Opt-in, read once, through `BootstrapOptions`.** It stays the only env reader
  (`DemoConnectionsFile`; blank or unset = off). Generic across plugins: `kind` is any plugin's
  `ConnectionKind`, `secret` is that plugin's secret string exactly as its form would produce it.
- **Create-only, idempotent.** `SeedDemoConnectionsCommandHandler` (Core, `IScopedService`) never
  updates or deletes: an entry whose name already exists is left alone even if its kind, secret or
  tags differ, so a user's edits to a demo connection survive restarts and a deleted one comes back
  on the next start (delete the entry from the file, or unset the variable, to stop that). Name
  uniqueness is the existing unique index plus the create handler's `Conflict` result, so there is
  no separate "does it exist" read to race.
- **Same path as the Connections page.** Each entry goes through `CreateConnectionCommandHandler`,
  so the secret is encrypted with the data key, the plugin's `GetConnectionSummary` is stored and a
  `connection.create` audit row is written — actor `system`, as for other non-circuit work (§15's
  scheduler). The handler checks the kind against the registered plugins first and skips (warning)
  an unknown one, unlike a manual create which tolerates it; a demo file naming a plugin that isn't
  compiled in is almost certainly a mistake.
- **Never fatal.** A missing, unreadable or non-array file logs an error and creates nothing;
  a malformed entry (missing name/kind/secret) or a failed create is logged and skipped; startup
  continues either way. Demo data must not be able to take the app down.
- **Where it hooks in.** A one-shot call in `Program.cs`, inside the existing startup scope right
  after `MigrateAsync` (and after `PluginRegistry.InitializeAll`), before the app serves requests.
  Not a hosted service: there is nothing to run later, ordering after migration is explicit in one
  place, and the first page load already sees the connections.
- **Not for production secrets.** The file is plain JSON on disk; the encrypted store only protects
  the copy in SbConsole's database. Use it for local/demo brokers and files (the compose stack mounts
  `docker/sqlite/demo-connections.json`, which holds file paths, not credentials) — real
  credentials go through the Connections page.

### 16.1 SQLite demo databases and the compose wiring (2026-10-13)

`docker/sqlite/seed.py` adds three files beside `reports.db` / `new.db` (unchanged), each created
only when missing and all rebuilt by `--force` (`backups/` is never emptied):

- `shop.db` (~6 MB, journal mode DELETE as a contrast to `reports.db`'s WAL) — the realistic
  relational one: `order_items` → `orders` `ON DELETE CASCADE` and → `products` `ON DELETE RESTRICT`
  so the Drop dialog's consequences show both kinds, plus `CHECK`s, JSON columns, a partial index,
  a view, a trigger, a WITHOUT ROWID table and `ANALYZE` stats. Random data is seeded (`Random(7)`);
  timestamps are relative to the seed run so revenue-by-month reaches "now".
- `corrupt.db` — the only way to demo the failed-integrity paths without a real disk fault. The
  seed writes rows with the index present, then rewrites `item-` → `ITEM-` in a few key payloads on
  the index's root (leaf) page, leaving page headers and cell pointers intact: the b-tree stays
  walkable, so `integrity_check` reports out-of-order/missing index entries and `REINDEX` can clear
  and rebuild it. Three orphan `item_notes` rows (inserted with foreign keys off) feed
  `foreign_key_check`. The seed asserts both "integrity fails" and "REINDEX on a copy → ok" and
  exits non-zero otherwise, so a SQLite change that alters the outcome is caught at seed time.
- `reference.db` — small lookup tables, meant for a Read-only connection.

`docker/sqlite/demo-connections.json` lists the five compose connections (secrets in exactly
`SqliteConnectionSettings.ToSecretString`'s format, pinned by `DemoConnectionsFileTests`); the app
service mounts it read-only at `/demo/connections.json` with `SBC_DEMO_CONNECTIONS` pointing there.
No `{root}` placeholder for `dotnet run`: the README says to copy the file and edit the paths —
one less feature to maintain for a dev convenience. `reports` and `shop` share `/sqlite/backups`;
that is safe because backup files are prefixed with the database's base name and history/pruning
are per connection id.

## 17. SQLite: New database from the UI (2026-10-13)

Spec: `docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md` §13. **Reverses** the SQLite
spec's §10/§11.10 non-goal "creating database files from the UI", on request — behind the same
fail-closed `SBC_SQLITE_ROOTS` policy, so a deployment with no roots still cannot create (or open)
anything.

- **Two SDK seams, both additive.** `IHostPathPolicy.AllowedRoots` (default `[]`, Core returns
  `BootstrapOptions.SqliteRoots` verbatim) so the dialog can offer the roots as its folder list
  without the plugin learning env var names. `IConnectionCreator.CreateAsync(name, kind, secret,
  tags)` → `ConnectionCreateResult` (`Created(Id)` / `Conflict` / `Invalid(Error)`) so a plugin can
  save the connection for a thing it just made. Core's `ConnectionCreator` (marker `IScopedService`)
  delegates to `CreateConnectionCommandHandler` — the Connections page's own path: encrypted secret,
  plugin summary, `connection.create` audit row — with the actor resolved like `EfAuditScope`
  (signed-in user; "system" outside a circuit; shared `AuditActor`). It refuses a kind no
  registered plugin handles (Core can't see Web's `PluginRegistry`, so it checks the same
  `IEnumerable<IPlugin>` the registry is built from). Plugins still never touch the DbContext.
- **Why a plugin-side command, not a Connections-page feature.** Only the plugin knows how to make
  a valid SQLite file and template it; only Core may persist connections. The handler
  (`CreateDatabaseCommandHandler`, Mutating, audited `sqlite.database.create`, detail
  `path · template · journal`) is the only place the two meet.
- **Safety rules.** Everything is validated before the disk is touched: the folder must string-match
  a configured root; an optional sub-folder must be relative, free of `.`/`..`, lexically inside
  it, and already exist; the file name has no separators, `..`, leading dot, `;` or control
  characters (`.db` appended unless `.db`/`.sqlite`/`.sqlite3`); the full path passes
  `IHostPathPolicy.Check` (symlinks followed, SbConsole's own DB refused); neither the file nor a
  `-wal`/`-shm`/`-journal` sibling — nor a directory or dangling symlink of that name — exists; the
  connection name isn't taken; a script is parsed by `SqliteScript.Parse` (same refusals as the
  Query page). Then the file is created with `FileMode.CreateNew` (O_CREAT|O_EXCL) on the resolved
  path — stricter than the spec's "ReadWriteCreate open": a file that appears between check and
  create makes it fail instead of being adopted — and opened writable and attach-limited for the
  journal mode and the template in one transaction. Any failure deletes the file and its siblings;
  so does a Conflict/Invalid/exception from `IConnectionCreator`. No directory is ever created and
  no file overwritten. Once the file exists the remaining steps ignore cancellation, so a cancelled
  request can't leave a file without a connection.
- **Audit convention.** As elsewhere in the plugin, a row only for work that ran: input refused
  before anything touched the disk (blank/taken name, refused folder/file/existing file, refused
  script) returns an error with no `sqlite.database.create` row; once a file was created every
  outcome is audited (success, failed template, connection not saved). A success also writes Core's
  own `connection.create` row.
- **UI.** "+ New database…" in the connection picker's menu (every SQLite page) and on its
  no-connections empty state opens `NewDatabaseDialog` — the Tables page's `sql-dialog` panel,
  floated in an overlay because the picker sits in the header bar. With no roots it shows the
  explanation instead of the form. The `<folder>/backups` offer comes from
  `FindBackupsDirQueryHandler` (policy-checked, must exist). On success the picker reloads its list
  and either selects the new connection in place (already on Overview) or navigates to
  `/p/sqlite/overview?connectionId=…`. Starter schema: users / projects / tasks (RESTRICT, CASCADE,
  SET NULL), an index, a view, 12 rows, `ANALYZE`; no functions in defaults because files are
  opened with `trusted_schema = OFF`. Nothing extra is confirmed for prod-tagged new connections:
  nothing existing is touched.

## 18. PostgreSQL plugin (`SbConsole.Plugins.Postgres`) (2026-10-14)

Spec: `docs/superpowers/specs/2026-10-14-postgres-plugin-design.md`; plan:
`docs/superpowers/plans/2026-10-14-postgres-plugin.md`. Five pages — Overview, Browse, Query,
Activity, Maintenance — and the audited actions behind them. Driver: Npgsql, referenced by the
plugin only. This section records the decisions and the places review changed the spec.

- **A connection is a server, not a database.** The saved secret is an Npgsql string plus two house
  keys (`Mode`, `StatementTimeoutS`); the database is a per-page picker (`pg_database`, `?db=`)
  that re-opens against the chosen database. Every connection the plugin opens sets
  `application_name=sbconsole`, which is how Activity recognises its own backends: "this console"
  chip, excluded from blocker/idle problems, and Cancel/Terminate refuse them (matched on the
  application name server-side, not on a pid the client sent). Consequence: any other client that
  connects with `application_name = 'sbconsole'` can't be cancelled or terminated from the console.
- **The Activity nav badge is cached for 15 s** per connection (keyed by a hash of the secret, never
  the raw secret), including failed probes, so shell polling doesn't hit the server each time.
- **The read-only guard is a seatbelt, not a privilege boundary.** Read mode runs in
  `BEGIN READ ONLY` with `SET LOCAL statement_timeout`; `PgSqlLexer` splits statements and refuses
  transaction/session control up front for a friendly message. The enforcement is on the server:
  read mode sends the text as a **single `NpgsqlBatchCommand`**, so the server's extended protocol
  itself rejects a second statement (`COMMIT; DELETE …`) even if the lexer were fooled. The lexer
  still has to agree with the server about where statements end, and review found mismatches that
  are now fixed and tested: a `\r` ends a `--` comment; non-ASCII identifier characters directly
  before `$$` or `E'…'` don't start a dollar quote or escape string. **Limit:** functions with side
  effects (`pg_terminate_backend`, `dblink`, `pg_reload_conf`) still run inside a read-only
  transaction; a read-only role is the real fix, and the connection form says so.
- **Write mode** needs `Mode=ReadWrite` in the saved secret (re-read per run; the page switch is a
  convenience). A script runs as one transaction made of **one single-command batch per statement**
  (so the position of a server error maps to the failing statement via `PgQueryException(inner,
  start)`); any error rolls back. The guard refuses transaction control (`BEGIN`, `START`, `COMMIT`,
  `END`, `ROLLBACK`, `ABORT`, `SAVEPOINT`, `RELEASE`), `PREPARE`, `CHECKPOINT`, `VACUUM`, and the
  statements the server won't run in a transaction block (`CREATE INDEX CONCURRENTLY`,
  `REINDEX … CONCURRENTLY`, `CREATE`/`DROP DATABASE`, `ALTER SYSTEM`). `COPY` is refused because
  there is no stdin/stdout channel to feed it.
- **`CLUSTER`, `REINDEX`, `ANALYZE` and `LOAD` are refused in read mode**, because PostgreSQL
  permits them inside a `READ ONLY` transaction (verified on PG16) and `CLUSTER`/`REINDEX` take
  `ACCESS EXCLUSIVE` locks. Write mode allows them; Maintenance and Browse are the audited routes.
- **The EXPLAIN tab refuses `ANALYZE`** (`EXPLAIN ANALYZE …` and `EXPLAIN (… ANALYZE …)`), since
  it executes the statement and the tab is not audited; Run accepts it and audits it as a read.
- **Every user-SQL run is audited**, reads included (`pg.query.read` / `pg.query.write`), because
  free-form SQL against a shared server is what an audit log is for; this makes Query's Run a
  command handler. Catalog/metric reads are queries and are not. A cancelled run is audited
  "· cancelled", added to history and rethrown. A refused export is not audited (nothing ran).
- **Capped reads cancel.** Reading stops at the row cap and cancels the command rather than
  draining the cursor, so a `SELECT *` on a 50M-row table costs one page. Write mode does drain
  capped selects (the statement must finish inside its transaction); the UI says so.
- **Browse queries are built from the catalog, never from client text.** The select list casts odd
  types `::text` and omits type modifiers (`char(3)`/`bit(n)` casts keep none, so a typmod mismatch
  can't fail the query), `ORDER BY` is qualified, and the target is looked up by **OID** and quoted
  from the catalog result. The same OID-not-name rule covers VACUUM/ANALYZE/VACUUM FULL and
  REINDEX/Drop index. **REINDEX CONCURRENTLY changes the index OID**, so Browse reloads the detail
  afterwards and keeps the selection by name. Limit: `?oid=` is not restricted to the schemas the
  tree shows (it is read-only metadata the role could already select).
- **Browse owns its URL.** `PgConnectionPicker`/`PgDatabasePicker` are built with `ManageUrl=false`
  on Browse and Query; the page writes `?connectionId=&db=&oid=` itself so a picker clearing its
  selection (connection change, failed load) raises `SelectedChanged` and the parent drops the stale
  `db`/`oid`. `?oid=` without `?db=` is dropped.
- **Overview keeps a stale snapshot.** The last good `PgOverview` (and the previous
  `xact_commit` sample for commits/s) lives in `IPluginStore` under `overview:<connectionId>`.
  When the server is unreachable the page renders the cached tiles dimmed with "stale · HH:mm",
  an alert with the friendly sentence, and retries every 30 s. Writing the snapshot is the plugin's
  own store, so it isn't audited. Limit: the commits/s baseline is shared across circuits
  (two viewers shorten each other's interval).
- **Limited visibility without `pg_monitor`.** A role that isn't superuser or `pg_monitor` sees
  other sessions' `pg_stat_activity` rows with the query and some columns nulled, and
  `pg_stat_replication`/`pg_stat_progress_vacuum` partly empty. Tiles and the Activity tree show
  what the role can see and say so; nothing is guessed. `pgstattuple` is optional: bloat is "—"
  with a hint when the extension isn't installed in the current database; the plugin never installs
  extensions.
- **Blocking chain.** `pg_blocking_pids` places a waiter under its first blocker; cycles fall back
  to flat rows. The "relation" the banner names uses the tuple-lock aware rule from the operations
  layer (a row lock waits on the *transaction*, not the relation), restricted to the current
  database. `ActivityTree.Build(activity, activeOnly, showIdle)` is pure and unit-tested;
  `WorstBlocker` ignores the filters, so the banner can name an idle-in-transaction blocker the
  table currently hides. Auto-refresh pauses while a confirm or action is in flight; a pid can still
  be reused in the window between confirm and click, so Cancel/Terminate re-read the row and verify
  it is a client backend (`GetBackend`) right before acting.
- **`ConfirmationRequest` overload (the one additive SDK change).** `IConfirmationService` gained
  `ConfirmAsync(ConfirmationRequest)` (verb, target, `IsProd`, consequence, label/value details,
  audit action, `Destructive`); the default implementation falls back to the old call, so existing
  callers and plugins are untouched. `ConfirmDialog` takes a **`Detailed` flag** set by the request
  overload, so the richer layout (consequence paragraph, details table, "Audited as …", error
  outline for `Destructive`) never appears on the old path. The typed text is the whole target
  (`pid 48213`), so pasting a bare number doesn't match. Terminate/VACUUM FULL/Drop index use it.
- **Maintenance timeouts.** VACUUM and ANALYZE run outside a transaction with the statement
  timeout raised to `max(setting, 600 s)`; VACUUM FULL to 3600 s, and the session `statement_timeout`
  is `RESET` afterwards so a pooled connection doesn't carry it. A cancelled maintenance run is
  audited. Actions are dispose-safe (the page may be left while one runs). Not built: the
  header overflow button, a dead-% meter bar, showing more than one running vacuum.
- **Audit page has no hash search.** The Query page's "audit row" link therefore goes to `/audit`
  (target `{connection}/{database}`), not to a row; a deep link needs a host change (search by
  detail hash) and is left as a follow-up.
- **Testing exception.** `PostgresOperations` is tested against a real `postgres:16` Testcontainer
  (`Testcontainers.PostgreSql`; the repo's first Testcontainers use), because catalog SQL, read-only
  transactions, timeouts, cancel/terminate and lock detection can't be honestly faked. The tests
  skip themselves when Docker is unavailable (`[DockerFact]`) and share one container
  (`"postgres"` collection; `MaxRowsOverride` is static, so tests touching it must stay in that
  collection). Handlers and pages use a substituted `IPostgresOperations`, like every other plugin.
- **Known limitations.** Partitioned tables show parent-only sizes and DDL; PG18 virtual generated
  columns aren't distinguished; very large `bytea`/`text` cells are loaded whole; `timestamp` cells
  show the server's text form (`+00`); the JSON tab shows 200 rows until "Show all"; Browse's
  filter chips and low-cardinality flag from the mockup, and a "Show lock tree" menu item, were not
  built; a pid-reuse window exists between confirm and action (mitigated above).
- **Local dev stack.** Compose profile `postgres`: `postgres:16` on host port 5433 (preloaded
  `pg_stat_statements`, `track_io_timing=on`) seeded by `docker/postgres/init/*.sql` on first
  start of the `postgres-data` volume (databases `app` ~1.1M rows, `analytics`, `billing`;
  `sessions` and `order_items` have autovacuum off and 40 % deleted for Maintenance; an unused
  `idx_orders_legacy_ref` for Overview), plus `postgres-blocker`, a `psql` loop that keeps one
  idle-in-transaction row lock and two waiters (`checkout-api`, `fulfilment-worker`) alive for
  Activity, restarting every 15 minutes. The healthcheck connects over TCP: the entrypoint's
  temporary init server listens on the socket only, so a socket check would report healthy
  mid-seed. `pg-local` (read-write, `prod`) and `pg-local-ro` (read-only, `demo`) were added to
  the single `docker/sqlite/demo-connections.json` (§16.1; Host `postgres`, the compose network),
  pinned by the Postgres `DemoConnectionsFileTests`; the SQLite test now only checks `kind ==
  "sqlite"` entries. For `dotnet run` the README says to copy the file and change Host/Port to
  `localhost`/`5433`.

## 19. Plugin folders grouped by purpose (2026-10-14)

With seven plugins, the flat `src/Plugins/` listing stopped saying what each one is for. Plugin
projects now sit one level deeper, in a folder per purpose:

- `src/Plugins/Messaging/` — message brokers and queues: ServiceBus, Kafka, Aws (SQS/SNS), RabbitMq.
- `src/Plugins/Databases/` — data stores: Postgres, Sqlite, Redis.

Only paths changed: project names, assembly names, namespaces, plugin ids and `/p/{pluginId}`
routes are untouched, and the Sdk-only dependency rule (§3) still holds. `SbConsole.slnx` mirrors
the grouping as solution folders; the `Dockerfile` restore layer, `SbConsole.Web.csproj` and the
plugin test projects' `ProjectReference`s point at the new paths. Test projects stay flat under
`tests/`. A new plugin goes in the folder matching its purpose; add a new category folder only
when a plugin fits neither.

## 20. Kubernetes plugin (`SbConsole.Plugins.Kubernetes`) (2026-10-14)

Spec: `docs/superpowers/specs/2026-10-14-kubernetes-plugin-design.md`; plan:
`docs/superpowers/plans/2026-10-14-kubernetes-plugin.md`. Overview, Workloads (with Deployment
detail), Pods (detail and logs), Nodes, Config and Events, plus the audited actions behind them.
Client: the official `KubernetesClient`, referenced by the plugin only; it lives in
`src/Plugins/Infrastructure/` (§19). This section records the decisions and where review changed
the spec.

- **Static credentials only.** The saved secret is a kubeconfig, accepted only if its user has a
  bearer token or client certificate/key **inline**. `exec`, `auth-provider` and file-path
  credentials are rejected at save time: they would run binaries or read files on the *host*
  running the console, which for a team-hosted app is not the user's machine and is an
  arbitrary-code path. The form says how to mint a ServiceAccount token instead.
  Because KubernetesClient resolves context/cluster/user names case-insensitively (first match),
  the validator checks *every* user and cluster in the file and rejects names that collide
  ignoring case, and the client factory builds from a trimmed in-memory config holding only the
  validated context, cluster and user (`BuildConfigFromConfigObject`), so an unchecked entry can
  never be selected.
- **Secrets never leave one method.** The typed Secret list is reduced to `SecretSummary` (name,
  type, key names, sizes, age) inside a single method of `KubernetesOperations.Config.cs`; values
  are not copied anywhere. The one value read is a `kubernetes.io/tls` certificate's `NotAfter`,
  parsed and dropped. Workload/pod env never fetches Secrets (a `secretKeyRef` shows its reference).
  Non-HTTP failures on that path are wrapped **without the inner exception**, so nothing carrying a
  response body can reach logs or UI.
- **Polling, not watches.** Pages refresh every 5 s (lists) or 10 s (detail); watches would need a
  long-lived stream per circuit and resync logic for little gain. One shared `K8sPoller` pauses
  while a dialog is open (`K8sDialogGate`) or the tab is hidden (`K8sVisibilityHook`, JS
  `watchVisibility`). Pages are dispose-safe: check `_disposed` after every await.
- **RBAC: remove, don't disable.** `SelfSubjectRulesReview` per namespace (plus
  `SelfSubjectAccessReview` for cluster-scoped checks), cached 5 minutes per connection. Controls
  the user can't use are not rendered; a 403 from the API is the backstop and shows a plain
  message. "All namespaces" probes the rules as `default` (a rules review needs one namespace). A
  connection counts as read-only from its non-self-review rules; the self-review rules every
  account has are ignored. Limit: a Role granted in only some namespaces can show controls in the
  all-namespaces view that 403 on click.
- **Drain runs in the request, with progress.** No background job: the dialog owns it, so closing
  or Stop ends it. Concurrency slots (5) are held only around API calls, never across waits.
  Evictable pods go before PDB-blocked ones, so a blocked pod can't starve the rest; blocked pods
  are retried until the **5-minute deadline**. Ownerless pods are never evicted without "force".
  In-flight calls finish on Stop and are reported; there is **no forced delete after Stop or the
  deadline**. The cordon stays on cancel (the node is simply left cordoned, and says so).
  Deviations from the mockup: deleting `emptyDir` data is **off** by default (kubectl's safer
  default); grace period is at least 1 s; the typed confirmation lives in `DrainDialog` (its
  eviction preview is the confirmation surface), not `IConfirmationService`. Every other
  Destructive action uses `ConfirmationRequest` (§18).
- **ConfigMap change history dropped.** The mockup showed one; Kubernetes keeps none (only
  `managedFields`, which isn't history). Edit is a replace with `resourceVersion`, so a concurrent
  change surfaces as a conflict.
- **Events.** Read from `events.k8s.io/v1`; on the **first 403** the plugin falls back to
  `core/v1` (the built-in `view` role cannot read `events.k8s.io`) and remembers it. "Last seen"
  comes from series / `deprecatedLastTimestamp`: `EventTime` is the *first* occurrence.
- **Dashboard hooks** share a 15 s cached overview per connection, so the shell's polling doesn't
  fan out. The "problems" hook **throws** when the cluster is unreachable, so the host marks the
  connection unchecked rather than healthy.
- **UI notes.** The namespace picker is `MudButton` + `MudPopover`: `MudSelect` renders child
  content inline and steals focus from its search field. The Overview header shows identity only;
  the "strongest ClusterRole binding" in the spec needs rights to list ClusterRoleBindings that
  most accounts lack.
- **Local dev stack.** Compose profile `kubernetes`: single-node k3s (`rancher/k3s:v1.30.6-k3s1`,
  Traefik off, privileged, fixed hostname `k3s-node-1`) with two **static demo tokens** (admin,
  and a `view`-bound viewer), checked in on purpose and local-only. The RBAC bindings are mounted
  as k3s auto-deploy manifests because tokens carry no groups, so the admin can't grant itself
  anything. The `alpine/k8s` seed job creates namespaces with a crash loop, a stuck rollout, a
  PDB-blocked StatefulSet, a failed Job, Pending/FailedMount pods and a TLS secret regenerated per
  run. Demo connections `k3s-local` (prod) and `k3s-local-viewer` (demo) join
  `docker/sqlite/demo-connections.json` (§16.1).
- **Testing exception.** `KubernetesOperations` is tested against a real k3s Testcontainer (shared
  fixture, skipped without Docker): RBAC reviews, eviction/PDB behaviour and API shapes aren't
  honestly fakeable. Handlers and pages use a substituted operations interface.
- **Out of scope.** Exec and Port-forward need a bidirectional stream through Blazor Server and get
  their own spec later. Add label and Edit taints are also out of scope; their controls are not rendered.
- **Known limitations.** Events polls cluster-wide on "All namespaces"; PDB `matchExpressions` are
  ignored when previewing a drain (the preview can over-report blocked pods); a single-node
  cluster can't demonstrate NotReady or a drain that moves workloads (handler tests cover them).

## 21. PostgreSQL query IntelliSense (2026-10-14)

Completion in the Postgres Query editor (spec: `docs/superpowers/specs/2026-10-14-postgres-plugin-design.md`
§16; plan: `docs/superpowers/plans/2026-10-14-postgres-query-intellisense.md`), reversing that spec's
§15 non-goal. Same shape as the SQLite plugin's (§15 / its spec §12): a pure C# `PgCompletionEngine`
answers the page's `[JSInvokable] GetCompletions`, and `postgres-completion.js` only renders the
popup — no editor library. The engine and popup are a **port, not shared code**: plugins reference
only the Sdk, and the dialects differ (dollar quoting, `E''`, schemas, search path, `::`). Hoist a
shared engine/popup into the Sdk or host only when a third plugin needs completion. The schema is
one read-only catalog query (`GetCompletionSchemaQueryHandler`, not audited, capped at 2,000 objects ×
200 columns, search-path schemas kept first when truncated). The page holds a single schema for the
current (connection, database), reloads it on a switch (not a keyed cache) and after a committed write
that ran DDL, and skips the load during static prerender. A finished word that equals a candidate
(`orders`, `OR`) offers nothing, so Enter stays a newline rather than accepting the next prefix match.
Suggestions: keywords, built-in functions, schemas, tables/views/matviews, columns, aliases;
user-defined functions, types and enum labels are out of scope.