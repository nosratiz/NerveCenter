# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

SbConsole: a team-hosted Blazor web app that acts as a **plugin host platform**. The host
(`SbConsole.Web` + `SbConsole.Core`) provides the chassis — shell UI, auth, saved connections,
encrypted secrets, settings, audit log, plugin storage. Plugins provide domain features:
`SbConsole.Plugins.ServiceBus` (Azure Service Bus), `SbConsole.Plugins.Kafka` (Apache Kafka),
`SbConsole.Plugins.Aws` (AWS SQS/SNS), `SbConsole.Plugins.RabbitMq` (RabbitMQ), `SbConsole.Plugins.Redis` (Redis) and
`SbConsole.Plugins.Sqlite` (SQLite files: read-only browsing and queries, opt-in write mode, backups and restore).
Full architecture and decision history: [docs/design.md](docs/design.md) — read it before making
any non-trivial change; it documents *why* things are shaped the way they are, not just what
exists.

## Commands

Build and test (net10.0, `TreatWarningsAsErrors=true` — a warning fails the build):

```bash
dotnet build                                                          # whole solution
dotnet test                                                            # whole solution
dotnet test tests/SbConsole.Core.Tests                                # one project
dotnet test --filter "FullyQualifiedName~PeekPageTests"                # one class/name filter
dotnet test --filter "FullyQualifiedName~PeekPageTests.SomeTestName"   # one test
```

Gate before every commit: `dotnet build -warnaserror` and `dotnet test` green (per §8 of the design doc). TDD is the house style — write the failing test first, per task.

Run the app (needs the local broker stack below, and a `launchSettings.json` — copy
`src/SbConsole.Web/Properties/launchSettings.json.example`, which documents the required env vars: `SBC_DATA_KEY`, `SBC_DB_PATH`, `SBC_ADMIN_PASSWORD`, `SBC_API_KEY`, `SBC_BIND`, `SBC_SQLITE_ROOTS`, optional `SBC_DEMO_CONNECTIONS`):

```bash
dotnet run --project src/SbConsole.Web --launch-profile http   # also `.claude/launch.json`'s "sbconsole-web" config
```

Local dev stack (brokers the plugins talk to — see [docker/README.md](docker/README.md) for full details, connection secrets, and known emulator limitations):

```bash
docker compose up -d                              # Kafka (KRaft, seeded topics) + Kafka UI
docker compose --profile servicebus up -d         # + Azure Service Bus emulator (SQL Server-backed)
docker compose --profile rabbitmq up -d           # + RabbitMQ 3.13 (management + shovel), seeded vhost /orders, AMQP on 5673
docker compose --profile sqlite --profile app up -d --build   # + SQLite demo DBs (reports, shop, corrupt, reference, new) at /sqlite; demo connections auto-created (SBC_DEMO_CONNECTIONS)
docker compose --profile app up -d --build        # SbConsole itself, containerized (needs .env, see .env.example)
docker compose --profile servicebus --profile app down -v   # full reset, drops volumes
```

## Architecture

### Solution layout and dependency rule

```
src/
├── SbConsole.Sdk/                 # The plugin contract. Zero deps beyond BCL + Microsoft.Extensions.*.Abstractions.
├── SbConsole.Core/                # Host services: handlers, EF Core (SQLite/WAL), auth, audit, secrets, settings.
├── SbConsole.Web/                 # Blazor Web App (Interactive Server), MudBlazor shell, Minimal APIs.
└── Plugins/                       # All plugin projects live here. Each references Sdk ONLY.
    ├── SbConsole.Plugins.ServiceBus/
    ├── SbConsole.Plugins.Kafka/
    ├── SbConsole.Plugins.Aws/
    ├── SbConsole.Plugins.RabbitMq/
    └── SbConsole.Plugins.Sqlite/
tests/  — one xUnit project per src project, plus SbConsole.IntegrationTests (Testcontainers, not yet built)
```

**Plugins depend only on `SbConsole.Sdk`.** Core depends on Sdk. Nothing depends on plugin
assemblies except `SbConsole.Web`'s `Program.cs` registration line
(`AddSbConsolePlugin<TPlugin>()`). No plugin type is referenced by name anywhere in Core — every
interaction flows through SDK interfaces, so plugin loading (currently compile-time) could move
to runtime `AssemblyLoadContext` loading later without changing plugin code.

### The SDK contract (`SbConsole.Sdk`)

The seam every plugin is built against — read `docs/design.md` §3 for the full interface list, but the shape to know:

- `IPlugin` — identity (`Id`, `ConnectionKind`, `DisplayName`), `ConfigureServices`, declares nav items, `TestConnectionAsync`, optional `GetNavBadgeAsync` (default no-op).
- `IPluginStore` — namespaced key/value + JSON storage per plugin ID (**keyed** DI registration — `AddKeyedScoped`, resolved via `[Inject, FromKeyedServices(pluginId)]`; there was a real bug from an earlier unkeyed registration where two plugins silently shared one store).
- `IConnectionProvider` — read access to saved connections of the plugin's kind, secrets decrypted just-in-time.
- `IConfirmationService` — typed-confirmation dialog (type-the-target-name) for destructive actions on prod-tagged connections; plain two-button otherwise.
- `IAuditScope` — plugins report what they did; the host writes the audit row.
- `PluginAction` / `ActionRisk` (`Safe`, `Mutating`, `Destructive`) — drives confirmation requirements.

### Conventions to follow when extending a plugin or Core

- **Handlers**: plain `XxxQueryHandler` / `XxxCommandHandler` classes, DI-registered — no MediatR. Commands write audit rows; queries never write.
- **DI registration is by marker, not by hand**: implement one lifetime marker from `SbConsole.Sdk` — `IScopedService` (handlers), `ISingletonService` (operations wrappers, Ef* stores), `ITransientService` — and `AddMarkedServices(assembly)` picks the class up (Program.cs scans Core + Web; each plugin's `ConfigureServices` scans its own assembly). The class is registered as its own app interface(s) if it has any, else as itself. Only things needing an instance or factory (secret protector, `TimeProvider`, keyed `IPluginStore`, hosted services) stay hand-registered. `ProgramDiRegistrationTests` fails if a handler is missing its marker.
- **Every plugin operation goes through a thin wrapper interface** (e.g. `IServiceBusOperations`, `IKafkaOperations`) between pages/handlers and the real SDK client, so unit tests substitute it directly — no network, no Docker. Real Azure/Kafka SDK exceptions never reach the UI or DB: route through a shared `FriendlyError` helper (in `SbConsole.Sdk`) that caps/collapses `ex.Message` and logs the full exception server-side.
- **Destructive actions** (`ActionRisk.Destructive`) on prod-tagged connections must go through `IConfirmationService`, sourcing `IsProd` from a server-side `IConnectionProvider` lookup by connection id — never trust a client-suppliable "is this prod" parameter.
- **No SQLite-only SQL** in EF Core code — PostgreSQL must stay a viable later option.
- **Plugins never touch the DbContext** — persistence only via `IPluginStore`.
- Plugin pages mount at `/p/{pluginId}/...` and declare their own `@page` route; they become routable automatically because `Program.cs`/`Routes.razor` scan `PluginRegistry.Plugins` for assemblies — no per-plugin host wiring needed.
- Theming: colors/typography/radius only through MudBlazor's `MudTheme` API (`src/SbConsole.Web/Theming/NocturneTheme.cs`), never hard-coded hex in component CSS — derive from `--mud-palette-*` tokens so light/dark both stay correct.

### Testing

- Core handlers: xUnit + FluentAssertions + NSubstitute (substitute `IAuditWriter`, stores, clock via `Microsoft.Extensions.TimeProvider.Testing`).
- Blazor components: bUnit.
- Plugins (Service Bus, Kafka, AWS, RabbitMQ): unit tests only, against a substitute of the plugin's operations interface — no Testcontainers, no real broker traffic, by deliberate choice until the plugin shape proves out further. Deliberate exception: SQLite plugin operations are tested against real temp-file databases (no Docker, no network); handlers and pages still use a substituted operations interface (see docs/design.md §15).
- `SbConsole.IntegrationTests` (Testcontainers + real/emulated brokers) is planned but not yet built.

### Stack (non-negotiable, per design doc §9)

.NET 10, C# latest, nullable enabled, warnings as errors. Blazor Interactive Server only (no WebAssembly). EF Core + SQLite (WAL). Minimal APIs (no controllers), under `/api/v1` — the UI never calls the API itself, it calls Core handlers directly; the API exists for external automation. MudBlazor as the sole component library. xUnit + FluentAssertions + NSubstitute + bUnit + Testcontainers.

## Design/plan docs

`docs/design.md` is the living design doc (append-only, dated sections) — the single source of truth for *why*. `docs/plans/` and `docs/superpowers/plans/` + `docs/superpowers/specs/` hold the dated implementation plans and design specs each feature was built from; consult the relevant one when working in an area it covers rather than re-deriving intent from code alone.
