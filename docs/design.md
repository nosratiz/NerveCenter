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
`GetOldestDeadLetterAsync` at the SDK default. The persisted read-only capability set remains
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
integration have since shipped — see §6.7.2. Delivery logs remain unbuilt; see §6.7.2's Non-goals.)*

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
- `GetOldestDeadLetterAsync` — stays at the SDK default (`null`): SQS exposes no enqueue timestamp
  without *receiving* a message, and a receive has a real side effect (the message goes invisible
  for the visibility timeout), so the wallboard's oldest-message tile is not fed by this plugin.

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
