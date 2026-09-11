# SbConsole — Design (v1)

Status: approved 2026-09-09. Extended 2026-09-10 (Core UI: §3 SDK v1.1, §4
new handlers, §5 screen inventory).
SDK version: `SbConsole.Sdk` 1.1.0 — see §3 for the 2026-09-10 additions
(`IPlugin.ConnectionKind`/`ConnectionKindDisplayName`/`Contribution`,
`IConfirmationService`).

## 1. What it is

A team-hosted Blazor web app that acts as a **plugin host platform**. The host
provides the chassis — shell UI, auth, saved connections, encrypted secrets,
settings, audit log, plugin storage. Plugins provide domain features.
Plugin #1: full Azure Service Bus management.

Deployment model: one shared instance per team (Docker/VM), single shared
admin login, API key for automation. Per-user accounts are a later concern;
the audit schema keeps an `Actor` column from day one.

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
  `ConfigureServices(IServiceCollection)`, declares nav items and root Blazor
  component types. **(v1.1)** also declares `ConnectionKind` (matches
  `Connection.Kind`, e.g. `"azure-servicebus"`), `ConnectionKindDisplayName`
  (shown in the host's Add Connection "Kind" dropdown), and `Contribution`
  (a `PluginContribution(int PageCount, int ActionCount)` record, shown on
  the host's Plugins page). All three are static/declarative properties —
  no plugin method call, no async round-trip; the host reads them straight
  off `PluginRegistry.Plugins`.
- `IPluginStore` — namespaced key/value + JSON document storage, scoped per
  plugin ID. The only persistence a plugin gets. Plugins never touch the
  DbContext.
- `IConnectionProvider` — read access to saved connections of the plugin's
  declared connection kind (e.g. `"azure-servicebus"`), secrets decrypted
  just-in-time, never exposed in UI models.
- `PluginAction` metadata — `ActionRisk` enum (`Safe`, `Mutating`,
  `Destructive`) the host uses to enforce confirmation rules.
- `IAuditScope` — plugins report what they did; the host writes the audit row.
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

Connection reachability testing (pinging a saved connection with its
credentials) is deliberately **not** part of the SDK yet — it is dynamic,
plugin-specific I/O rather than a static property, and is deferred to the
plan that gives a plugin something real to test against.

## 4. Host core (`SbConsole.Core`)

- **Handlers**: plain `XxxQueryHandler` / `XxxCommandHandler` classes,
  DI-registered. No MediatR. Commands write audit rows via `IAuditWriter`;
  queries never write. **(v1.1)** adds `UpdateConnectionCommandHandler`
  (rename/re-tag/replace-secret — v1.0 only had Create/List/Delete),
  `ListAuditEntriesQueryHandler` (paged, filterable by date range, actor,
  risk, target text — v1.0 could only write audit rows, never read them
  back), and `ListPluginsQueryHandler` (joins `PluginRegistry`'s static
  plugin metadata with a live "connections in use" count grouped by
  `Connection.Kind`, for the Plugins page).
- **Persistence**: EF Core + SQLite (WAL) at `SBC_DB_PATH`. Tables:
  `AppSettings`, `Connections`, `AuditEntries`, `PluginDocuments`. No
  SQLite-only SQL — PostgreSQL stays a viable later option.
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
- Plugin pages mount at `/p/{pluginId}/...`, rendering the plugin's registered
  root components.
- Destructive actions (`ActionRisk.Destructive`) on prod-tagged connections
  require the typed-confirmation dialog (`IConfirmationService`, §3).
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
  `ConnectionKindDisplayName`), not free text. A Status column always reads
  "Untested" — reachability testing is deferred (§3). Delete on a prod-tagged
  connection goes through `IConfirmationService`.
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

Deferred past v1.1: connection reachability testing, plugin-contributed
dashboard widgets, charts/wallboard views, audit CSV export, audit-retention
enforcement, the dynamic plugin marketplace (install/enable/disable at
runtime).

## 6. Service Bus plugin (`SbConsole.Plugins.ServiceBus`)

Connection-string auth only in v1.

- **Entities**: queues, topics, subscriptions, rules (SQL + correlation
  filters) — list with live counts (active/DLQ/scheduled), create, edit
  properties, delete (delete = `Destructive`).
- **Messages**: peek (non-destructive) with paging and body/property
  inspection; send with application properties, content-type, scheduled
  enqueue; DLQ browse, resubmit (single/batch), purge (`Destructive`).
- Uses `Azure.Messaging.ServiceBus` (`ServiceBusClient` +
  `ServiceBusAdministrationClient`) behind a thin plugin-internal interface so
  unit tests can substitute it and integration tests hit the emulator.

Out of v1: deferred-message tooling, sessions tooling beyond basic display,
metrics dashboards/history, ARM/namespace creation, Entra ID auth.

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
- Integration: Testcontainers running the official Azure Service Bus emulator
  — real peek/send/DLQ flows over AMQP.
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
colors, typography, corner radius, and shadow tuning through MudBlazor's own
`MudTheme` API (`PaletteDark`/`PaletteLight`, `Typography`,
`LayoutProperties`, `Shadow`). No custom CSS is added to force MudBlazor
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
  (`--radius-md`). Shadow elevation levels are re-tuned per Nocturne's own
  stated elevation logic — "a hairline edge plus ambient darkness" for the
  dark theme (directly from its `--shadow-*` tokens) and a softer ink-tinted
  shadow for the light theme (Nocturne's own stated intent for a light
  ground, values derived since no light shadow tokens exist to copy).
- **Login page parity**: `EmptyLayout` (Plan 1) currently instantiates its
  own bare `<MudThemeProvider />`, disconnected from `MainLayout`'s
  dark-mode-resolution logic (Plan 2 Task 8) — the pre-login page would
  otherwise stay on MudBlazor's stock theme forever. The dark-mode/system-
  preference resolution logic is extracted out of `MainLayout` into a shared
  piece both layouts use, so Login renders in whatever theme (light/dark/
  system) the `theme.mode` setting already specifies.
- A handful of tests pin the theme's key token values (background/primary/
  etc. hex codes for both palettes) so an edit to `NocturneTheme.cs` can't
  silently drift from these decisions without a test catching it.
