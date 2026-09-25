# RabbitMQ plugin — Implementation plan

Spec: `docs/superpowers/specs/2026-09-25-rabbitmq-plugin-design.md` (read it first — this plan only
sequences it). Mockup: `~/Desktop/UI mockups for NerveCenter/SbConsole RabbitMQ.dc.html`
(frames `1a`–`1h`; each frame is a `.dv-turn` section — grep for `1c` etc. to find one).

## Global constraints

- Plugin project `src/Plugins/SbConsole.Plugins.RabbitMq` references **only** `SbConsole.Sdk`
  (plus NuGet `RabbitMQ.Client` 7.2.x, `MudBlazor` 9.9.0, and the `Microsoft.AspNetCore.App`
  framework reference — copy the AWS csproj shape). Test project
  `tests/SbConsole.Plugins.RabbitMq.Tests` copies the AWS test csproj.
- TDD per task: failing test first. Gate per task: `dotnet build -warnaserror` and
  `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests` green (and `dotnet test` whole-solution
  green for tasks touching Web/host).
- Follow the AWS plugin's idioms (it is the most recent): handler shape
  (`Queues/PurgeQueueCommandHandler.cs`), page shape (`Pages/Queues.razor` — generation counters
  for stale loads, keyed `[Inject(Key = "rabbitmq")] IPluginStore`, snackbar on failure), bUnit
  test shape (`tests/SbConsole.Plugins.Aws.Tests/Pages/QueuesPageTests.cs`).
- Theming: MudBlazor props and `--mud-palette-*` tokens only, never hex.
- Real RabbitMQ/HTTP exceptions never reach the UI: `FriendlyRabbitError.From(ex)`, full
  exception logged via `ILogger`.
- Commits: one per task, conventional message `feat(rabbitmq): ...`, staging **only** the files
  the task touched (never `git add -A`; `src/SbConsole.Web/Properties/launchSettings.json` has
  unrelated local changes and must never be committed).

## Tasks

1. **Scaffold.** Projects + `SbConsole.slnx` + `SbConsole.Web.csproj` reference +
   `AddSbConsolePlugin<RabbitMqPlugin>()` in `Program.cs`. `RabbitMqPlugin` shell (§3, nav items,
   `TestConnectionAsync` delegating to `RabbitOperations` is wired in task 3 — until then throw
   `NotImplementedException` is NOT acceptable; return `new ConnectionTestResult(false,
   "Not implemented")`), `PluginResult`, `Client/RabbitConfigParser` (percent-encoded values,
   `Parse`/`Serialize`/`SafeEcho`), `Client/RabbitConnectionSettings` (§2 defaults),
   `Client/ManagementApiException`, `Client/FriendlyRabbitError` (maps
   `ManagementApiException` 401/403/404/5xx, `HttpRequestException`, timeout
   `TaskCanceledException`, RabbitMQ.Client `BrokerUnreachableException`,
   `AuthenticationFailureException`, `OperationInterruptedException` by reply code 403/404/405,
   `AlreadyClosedException`; fallback `FriendlyError.From`). Tests for parser, settings, errors,
   plugin identity.
2. **Management API client.** Domain records (§4) in `Client/`, `ManagementApiClient` with every
   management method in `IRabbitOperations` (§4). Tests with a fake `HttpMessageHandler`.
3. **AMQP + operations.** `AmqpClient` (open/test, `GetMessagesAsync` peek/consume semantics,
   `PublishAsync` mandatory + confirms), `AmqpMessageMapper`, `IRabbitOperations` +
   `RabbitOperations` composing both, `TestConnectionAsync` split result (§2) behind internal
   probe seams so the outcome matrix is unit-tested. Wire `RabbitMqPlugin.TestConnectionAsync`.
4. **Routing logic.** `Routing/RoutingMatcher`, `Routing/DeadLetterTopology`,
   `Routing/PolicyMatcher`, `Routing/NameSuggester` (closest name for "Did you mean"). Pure, tests.
5. **Handlers.** Every query/command handler (§8) + `ConfigureServices` registration. Tests.
6. **Connection form.** `Client/RabbitConnectionFields.razor` (§2), `ConnectionFormComponentType`,
   `GetConnectionSummary`. bUnit tests.
7. **Shared page chrome + Overview (1b).** `Components/ConnectionVhostPicker.razor`, shared
   management-error alert and stale-data banner components (§7 error model), `Pages/Overview.razor`.
8. **Exchanges (1c) + Publish dialog (1g).** `Pages/Exchanges.razor`, `CreateExchangeDialog`,
   `ExchangeBindingsDialog`, `PublishDialog` with routing preview.
9. **Queues (1d).** `Pages/Queues.razor` (auto-refresh, stale-kept-visible), `CreateQueueDialog`,
   purge/delete through `IConfirmationService`.
10. **Queue detail (1e).** `Pages/QueueDetail.razor`, `AddBindingDialog`.
11. **Get messages (1f).** `Pages/GetMessages.razor`, `RepublishDialog`.
12. **Shovels & policies (1h).** `Pages/ShovelsPolicies.razor`, `CreateShovelDialog`.
13. **Host hooks.** §9 badge/metrics/problems with the cached snapshot; `Contribution` counts.
14. **Local stack + docs.** §10 docker service + seed; `docker/README.md`; `docs/design.md` new
    §6.8; `CLAUDE.md` plugin list; manual end-to-end check in the browser against the broker.
