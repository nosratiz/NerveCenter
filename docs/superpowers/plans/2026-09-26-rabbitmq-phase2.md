# RabbitMQ Plugin Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add exchange-to-exchange bindings, policy create/edit/delete, a Connections & channels page with force-close, and a cross-connection Dead-letter page with safe bulk replay to the RabbitMQ plugin.

**Architecture:** Everything stays inside `src/Plugins/SbConsole.Plugins.RabbitMq` (references `SbConsole.Sdk` only). New broker calls go on `IRabbitOperations` (management API via `ManagementApiClient`, AMQP via `AmqpClient`), new thin handlers go through `HandlerRunner`, and new pages reuse `ConnectionVhostPicker`, `ManagementErrorAlert`, `StaleDataBanner` and `AutoRefresh`. Pure logic (`RoutingMatcher.ResolveDeep`, `PolicyDefinitionBuilder`, `ReplayOrigin`, `DeadLetterReplayer`) is unit-tested with no broker.

**Tech Stack:** .NET 10, C# latest, Blazor Interactive Server, MudBlazor, RabbitMQ.Client 7.2.2, xUnit + FluentAssertions + NSubstitute + bUnit.

**Spec:** `docs/superpowers/specs/2026-09-26-rabbitmq-phase2-design.md`. Phase-1 spec for conventions: `docs/superpowers/specs/2026-09-25-rabbitmq-plugin-design.md`.

## Global Constraints

- `TreatWarningsAsErrors=true`: a warning fails the build. Nullable enabled.
- Gate before every commit: `dotnet build -warnaserror` and `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests` green. Run the full `dotnet test` in the final task.
- TDD: write the failing test first in every task.
- Plugins reference `SbConsole.Sdk` only. No change to Sdk, Core or Web except `docs/`.
- Theming only through `--mud-palette-*` / MudBlazor props. Never hard-coded hex.
- Numbers `N0` invariant via `RabbitFormat.Count`, rates via `RabbitFormat.Rate` (`null` → `—`).
- Ready and Unacked are never summed anywhere.
- `IsProd` for confirmations always comes from the server-side `ConnectionInfo` (picker selection or `IConnectionProvider.ListAsync`), never from a query string.
- Audit action names: `rabbitmq.message.replay`, `rabbitmq.policy.create`, `rabbitmq.policy.update`, `rabbitmq.policy.delete`, `rabbitmq.connection.close`, `rabbitmq.binding.add`, `rabbitmq.binding.remove`. Audit target `{connectionName}/{vhost}/{name}` via `HandlerRunner.Target`.
- No broker in tests. The local stack (`docker compose --profile rabbitmq up -d`) is for manual verification only.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage only the files the task touched — never `.claude/launch.json` or `src/SbConsole.Web/Properties/launchSettings.json` (the user's local edits).

## Deviations from the spec, decided while planning

1. **§5 operations:** instead of changing `AddBindingAsync`/`RemoveBindingAsync` signatures (which would churn every existing substitute call), add `AddExchangeBindingAsync`/`RemoveExchangeBindingAsync`. The commands gain a `DestinationType` that picks one.
2. **§5 Unbind confirmation:** queue detail's Unbind actually calls `IConfirmationService.ConfirmAsync("Unbind", …, connection.IsProd)` (typed on prod). The Exchanges dialog does exactly the same.
3. **§2 data source:** the Dead-letter page calls `RabbitMqPlugin.FetchSnapshotAsync` directly (uncached) per connection. The page is user-driven, and a post-replay refresh must be fresh, so the 60 s cache would only get in the way.
4. **§4 Direct connections:** shovel/federation connections come back with `protocol` `"Direct 0-9-1"` and null peer/state/channels (captured from the local stack). They show a "internal" chip and their Close button is disabled with the tooltip "Internal broker connection (shovel/federation) — manage it from Shovels & policies".

5. **§5 counts:** the Exchanges "Bindings" count and "no bindings" warning stay outgoing-only — an exchange that only receives from another exchange and binds nowhere still drops messages.
6. **§3 exchange inputs:** dead-letter-exchange and alternate-exchange are free-text fields, not pickers.
7. **§4 close reason:** fixed (`Closed from SbConsole`), not edited per close — one confirmation dialog instead of two.
8. **Page class names:** `BrokerConnections` and `DeadLetters` (routes unchanged), so they don't clash with the `Connections` namespace or the `DeadLetterSnapshot` record.

Task 12 records these in the spec and `design.md`.

## File map

| File | Status | Responsibility |
|---|---|---|
| `Client/BindingDestinationType.cs` | new | `Queue` / `Exchange` enum |
| `Client/ClientConnectionInfo.cs` | new | connection + channel records |
| `Client/Replay.cs` | new | `ReplayRequest`, `ReplayOutcome` |
| `Client/IRabbitOperations.cs`, `RabbitOperations.cs` | modify | 8 new operations |
| `Client/ManagementApiClient.cs` | modify | e2e bindings, policies PUT/DELETE, connections, channels, close |
| `Client/AmqpClient.cs` | modify | `ReplayDeadLettersAsync` + the real `IReplayChannel` |
| `Routing/RoutingMatcher.cs` | modify | `ResolveDeep` (recursive, cycle-safe) |
| `Routing/DeadLetterTopology.cs` | modify | follow exchange hops; `SourcesOf` |
| `Routing/ReplayOrigin.cs` | new | pure origin resolution from x-death |
| `Messages/DeadLetterReplayer.cs` | new | pure replay loop over `IReplayChannel` |
| `Policies/PolicyDefinitionBuilder.cs` | new | typed fields ⇄ definition dictionary, validation |
| `Policies/PutPolicyCommandHandler.cs`, `DeletePolicyCommandHandler.cs` | new | policy commands |
| `ClientConnections/ListClientConnectionsQueryHandler.cs`, `CloseClientConnectionCommandHandler.cs` | new | connections page handlers |
| `DeadLetters/ListDeadLetterOverviewQueryHandler.cs`, `ReplayDeadLettersCommandHandler.cs` | new | dead-letter handlers |
| `Exchanges/ListExchangeBindingsQueryHandler.cs` | modify | returns `ExchangeBindings` (outgoing, incoming, whole vhost) |
| `Bindings/AddBindingCommandHandler.cs`, `RemoveBindingCommandHandler.cs` | modify | `DestinationType` |
| `Pages/PublishDialog.razor` | modify | deep preview with paths |
| `Pages/ExchangeBindingsDialog.razor` | modify | two sections, Unbind, + Add binding |
| `Pages/AddBindingDialog.razor` | modify | source-fixed mode with queue/exchange destination |
| `Pages/Exchanges.razor` | modify | counts include incoming e2e; dialog params |
| `Pages/PolicyDialog.razor` | new | create/edit policy |
| `Pages/ShovelsPolicies.razor` | modify | New / Edit / Delete policy |
| `Pages/Connections.razor` | new | connections & channels |
| `Pages/DeadLetters.razor`, `Pages/ReplayDialog.razor` | new | dead-letter overview + replay |
| `RabbitMqPlugin.cs` | modify | nav items, DI, badge href, contribution |
| `docker/rabbitmq/definitions.json` | modify | one e2e binding |
| `docs/design.md`, the phase-2 spec | modify | addendum + deviations |

All `src` paths above are relative to `src/Plugins/SbConsole.Plugins.RabbitMq/`. Tests live in `tests/SbConsole.Plugins.RabbitMq.Tests/` (same folder layout: `Client/`, `Routing/`, `Handlers/`, `Pages/`).

---

### Task 1: Exchange-to-exchange binding operations and commands

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/BindingDestinationType.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/ManagementApiClient.cs` (after `RemoveBindingAsync`, ~line 325)
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/IRabbitOperations.cs`, `Client/RabbitOperations.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Bindings/AddBindingCommandHandler.cs`, `Bindings/RemoveBindingCommandHandler.cs`
- Test: `tests/SbConsole.Plugins.RabbitMq.Tests/Client/ManagementApiClientWriteTests.cs`, `tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/CommandHandlerTests.cs`

**Interfaces:**
- Produces: `enum BindingDestinationType { Queue, Exchange }`;
  `IRabbitOperations.AddExchangeBindingAsync(string secret, string vhost, string source, string destination, string routingKey, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)`;
  `IRabbitOperations.RemoveExchangeBindingAsync(string secret, string vhost, string source, string destination, string propertiesKey, CancellationToken ct = default)`;
  `record AddBindingCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey, IReadOnlyDictionary<string, object?>? Arguments = null, BindingDestinationType DestinationType = BindingDestinationType.Queue)`;
  `record RemoveBindingCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey, string PropertiesKey, BindingDestinationType DestinationType = BindingDestinationType.Queue)`.

- [ ] **Step 1: Write the failing client tests**

Append inside `ManagementApiClientWriteTests` (before the private helpers at the bottom):

```csharp
    [Fact]
    public async Task AddExchangeBinding_posts_to_the_exchange_destination_path()
    {
        var (client, handler) = Created("POST", "/api/bindings/%2Forders/e/order-events/e/audit.fanout");

        await client.AddExchangeBindingAsync(TestSettings.Orders, "/orders", "order-events", "audit.fanout", "order.#",
            new Dictionary<string, object?>());

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        Json(request.Body).Should().Be(Json("""{"routing_key":"order.#","arguments":{}}"""));
    }

    [Fact]
    public async Task RemoveExchangeBinding_escapes_the_already_encoded_properties_key()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/bindings/%2Forders/e/order-events/e/audit.fanout/order.%2523", "", HttpStatusCode.NoContent);

        await client.RemoveExchangeBindingAsync(TestSettings.Orders, "/orders", "order-events", "audit.fanout", "order.%23");

        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    }
```

- [ ] **Step 2: Write the failing handler tests**

In `CommandHandlerTests.cs`, extend the `SimpleCommands` theory data (line ~20) to

```csharp
    public static TheoryData<string> SimpleCommands => new()
    {
        "exchange.create", "exchange.delete", "queue.create", "queue.delete", "queue.purge",
        "binding.add", "binding.remove", "shovel.create", "shovel.delete", "shovel.restart",
        "binding.add.exchange", "binding.remove.exchange",
    };
```

and add two arms to the `Case(string name)` switch, just before `_ => throw new ArgumentOutOfRangeException(nameof(name)),`:

```csharp
        "binding.add.exchange" => (() => new AddBindingCommandHandler(Operations, Connections, Audit, Log<AddBindingCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "order-events", "audit.fanout", "order.#", DestinationType: BindingDestinationType.Exchange)),
            "rabbitmq.binding.add", "rabbit-uk-prod//orders/order-events→audit.fanout", ActionRisk.Mutating, "key order.#"),
        "binding.remove.exchange" => (() => new RemoveBindingCommandHandler(Operations, Connections, Audit, Log<RemoveBindingCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "order-events", "audit.fanout", "order.#", "order.%23", BindingDestinationType.Exchange)),
            "rabbitmq.binding.remove", "rabbit-uk-prod//orders/order-events→audit.fanout", ActionRisk.Mutating, "key order.#"),
```

In the helper that makes every operation throw (the block with `Operations.AddBindingAsync(default!, …).ThrowsAsyncForAnyArgs(ex);`), add:

```csharp
        Operations.AddExchangeBindingAsync(default!, default!, default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.RemoveExchangeBindingAsync(default!, default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
```

Then add a test proving the exchange path is used and the queue path is not:

```csharp
    [Fact]
    public async Task Exchange_destination_binds_through_the_exchange_operation()
    {
        await new AddBindingCommandHandler(Operations, Connections, Audit, Log<AddBindingCommandHandler>())
            .HandleAsync(new(ConnectionId, Conn, Vhost, "order-events", "audit.fanout", "order.#", DestinationType: BindingDestinationType.Exchange));

        await Operations.Received(1).AddExchangeBindingAsync(Secret, Vhost, "order-events", "audit.fanout", "order.#",
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().AddBindingAsync(default!, default!, default!, default!, default!, default!, default);
    }
```

Add `using SbConsole.Plugins.RabbitMq.Client;` at the top of `CommandHandlerTests.cs` if it isn't there.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~ManagementApiClientWriteTests|FullyQualifiedName~CommandHandlerTests"`
Expected: build FAILS — `AddExchangeBindingAsync`, `BindingDestinationType` not defined.

- [ ] **Step 4: Implement**

`Client/BindingDestinationType.cs`:

```csharp
namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>What a binding delivers to: a queue ("q" in management paths) or another exchange ("e").</summary>
public enum BindingDestinationType
{
    Queue,
    Exchange,
}
```

`ManagementApiClient.cs`, directly after `RemoveBindingAsync`:

```csharp
    /// <summary>Exchange-to-exchange binding: POST /api/bindings/{vhost}/e/{source}/e/{destination}.</summary>
    public Task AddExchangeBindingAsync(
        RabbitConnectionSettings settings, string vhost, string source, string destination, string routingKey,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["routing_key"] = routingKey,
            ["arguments"] = arguments,
        };
        return SendAsync(settings, HttpMethod.Post, $"api/bindings/{Seg(vhost)}/e/{ExchangeSeg(source)}/e/{Seg(destination)}", body, ct);
    }

    /// <summary>Same properties_key escaping rule as <see cref="RemoveBindingAsync"/>.</summary>
    public Task RemoveExchangeBindingAsync(
        RabbitConnectionSettings settings, string vhost, string source, string destination, string propertiesKey,
        CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete,
            $"api/bindings/{Seg(vhost)}/e/{ExchangeSeg(source)}/e/{Seg(destination)}/{Seg(propertiesKey)}", null, ct);
```

`IRabbitOperations.cs`, after `RemoveBindingAsync`:

```csharp
    Task AddExchangeBindingAsync(string secret, string vhost, string source, string destination, string routingKey, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);
    Task RemoveExchangeBindingAsync(string secret, string vhost, string source, string destination, string propertiesKey, CancellationToken ct = default);
```

`RabbitOperations.cs`, after `RemoveBindingAsync`:

```csharp
    public async Task AddExchangeBindingAsync(
        string secret, string vhost, string source, string destination, string routingKey,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default) =>
        await _management.AddExchangeBindingAsync(Settings(secret), vhost, source, destination, routingKey, arguments, ct);

    public async Task RemoveExchangeBindingAsync(
        string secret, string vhost, string source, string destination, string propertiesKey, CancellationToken ct = default) =>
        await _management.RemoveExchangeBindingAsync(Settings(secret), vhost, source, destination, propertiesKey, ct);
```

Replace `Bindings/AddBindingCommandHandler.cs` body:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record AddBindingCommand(
    Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    BindingDestinationType DestinationType = BindingDestinationType.Queue);

public sealed class AddBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddBindingCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(AddBindingCommand cmd, CancellationToken ct = default)
    {
        var arguments = cmd.Arguments ?? new Dictionary<string, object?>();
        return HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.add", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Destination}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => cmd.DestinationType == BindingDestinationType.Exchange
                ? operations.AddExchangeBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.RoutingKey, arguments, ct)
                : operations.AddBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.RoutingKey, arguments, ct), ct);
    }
}
```

Replace `Bindings/RemoveBindingCommandHandler.cs` body:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record RemoveBindingCommand(
    Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey, string PropertiesKey,
    BindingDestinationType DestinationType = BindingDestinationType.Queue);

public sealed class RemoveBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RemoveBindingCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(RemoveBindingCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.remove", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Destination}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => cmd.DestinationType == BindingDestinationType.Exchange
                ? operations.RemoveExchangeBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.PropertiesKey, ct)
                : operations.RemoveBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.PropertiesKey, ct), ct);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS (all existing tests still green — every existing caller of the two commands is positional).

- [ ] **Step 6: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Client/BindingDestinationType.cs src/Plugins/SbConsole.Plugins.RabbitMq/Client/ManagementApiClient.cs src/Plugins/SbConsole.Plugins.RabbitMq/Client/IRabbitOperations.cs src/Plugins/SbConsole.Plugins.RabbitMq/Client/RabbitOperations.cs src/Plugins/SbConsole.Plugins.RabbitMq/Bindings tests/SbConsole.Plugins.RabbitMq.Tests/Client/ManagementApiClientWriteTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/CommandHandlerTests.cs
git commit -m "feat(rabbitmq): exchange-to-exchange bind/unbind operations

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Recursive routing (`ResolveDeep`), DLQ topology through exchange hops, `ExchangeBindings`

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Routing/RoutingMatcher.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Routing/DeadLetterTopology.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Exchanges/ListExchangeBindingsQueryHandler.cs`
- Modify (callers of the handler, to keep compiling): `Pages/PublishDialog.razor`, `Pages/ExchangeBindingsDialog.razor`
- Test: `tests/SbConsole.Plugins.RabbitMq.Tests/Routing/RoutingMatcherTests.cs`, `Routing/DeadLetterTopologyTests.cs`, `Handlers/QueryHandlerTests.cs`

**Interfaces:**
- Produces:
  - `record RoutedQueue(string Queue, IReadOnlyList<string> Path, BindingInfo Binding)`
  - `record DeepRoutePreview(RoutePreview Root, IReadOnlyList<RoutedQueue> Queues, bool ReachedHopLimit, IReadOnlyList<string> UnpredictableExchanges)`
  - `RoutingMatcher.MaxHops = 8`
  - `RoutingMatcher.ResolveDeep(ExchangeSummary root, IReadOnlyList<ExchangeSummary> exchanges, IReadOnlyList<BindingInfo> vhostBindings, string routingKey, IReadOnlyDictionary<string, string> headers) : DeepRoutePreview`
  - `RoutingMatcher.ExchangeLabel(ExchangeSummary e) : string` ("(AMQP default)" for "")
  - `DeadLetterTopology.SourcesOf(string deadLetterQueue) : IReadOnlyList<string>`
  - `record ExchangeBindings(IReadOnlyList<BindingInfo> Outgoing, IReadOnlyList<BindingInfo> Incoming, IReadOnlyList<BindingInfo> Vhost)` in namespace `SbConsole.Plugins.RabbitMq.Exchanges`
  - `ListExchangeBindingsQueryHandler.HandleAsync(Guid, string vhost, string exchange, CancellationToken) : Task<PluginResult<ExchangeBindings>>`

- [ ] **Step 1: Write the failing routing tests**

Append to `RoutingMatcherTests`:

```csharp
    private static readonly ExchangeSummary[] HopExchanges =
    [
        Exchange("order-events", "topic"),
        Exchange("audit.fanout", "fanout"),
        Exchange("eu.only", "direct"),
        Exchange("loop.a", "fanout"),
        Exchange("loop.b", "fanout"),
    ];

    [Fact]
    public void ResolveDeep_follows_an_exchange_binding_to_its_queues_and_reports_the_path()
    {
        BindingInfo[] bindings =
        [
            Binding("order-events", "order-events.q", "order.*.created"),
            Binding("order-events", "audit.fanout", "order.#", destinationType: "exchange"),
            Binding("audit.fanout", "audit.sink", ""),
        ];

        var deep = RoutingMatcher.ResolveDeep(HopExchanges[0], HopExchanges, bindings, "order.uk.created", NoHeaders);

        deep.Queues.Select(q => (q.Queue, string.Join(" → ", q.Path))).Should().Equal(
            ("order-events.q", "order-events → order-events.q"),
            ("audit.sink", "order-events → audit.fanout → audit.sink"));
        deep.Root.Matched.Should().HaveCount(2);
        deep.ReachedHopLimit.Should().BeFalse();
    }

    [Fact]
    public void ResolveDeep_evaluates_each_hop_with_the_same_routing_key()
    {
        BindingInfo[] bindings =
        [
            Binding("order-events", "eu.only", "order.#", destinationType: "exchange"),
            Binding("eu.only", "eu.q", "order.eu.created"),
        ];

        RoutingMatcher.ResolveDeep(HopExchanges[0], HopExchanges, bindings, "order.uk.created", NoHeaders)
            .Queues.Should().BeEmpty();
        RoutingMatcher.ResolveDeep(HopExchanges[0], HopExchanges, bindings, "order.eu.created", NoHeaders)
            .Queues.Should().ContainSingle().Which.Queue.Should().Be("eu.q");
    }

    [Fact]
    public void ResolveDeep_survives_a_cycle_and_lists_each_queue_once()
    {
        BindingInfo[] bindings =
        [
            Binding("loop.a", "loop.b", "", destinationType: "exchange"),
            Binding("loop.b", "loop.a", "", destinationType: "exchange"),
            Binding("loop.a", "shared.q", ""),
            Binding("loop.b", "shared.q", ""),
        ];

        var deep = RoutingMatcher.ResolveDeep(HopExchanges[3], HopExchanges, bindings, "", NoHeaders);

        deep.Queues.Should().ContainSingle().Which.Path.Should().Equal("loop.a", "shared.q");
    }

    [Fact]
    public void ResolveDeep_stops_at_the_hop_limit()
    {
        var exchanges = Enumerable.Range(0, 12).Select(i => Exchange($"x{i}", "fanout")).ToArray();
        var bindings = Enumerable.Range(0, 11)
            .Select(i => Binding($"x{i}", $"x{i + 1}", "", destinationType: "exchange"))
            .Append(Binding("x11", "deep.q", ""))
            .ToArray();

        var deep = RoutingMatcher.ResolveDeep(exchanges[0], exchanges, bindings, "", NoHeaders);

        deep.ReachedHopLimit.Should().BeTrue();
        deep.Queues.Should().BeEmpty();
    }

    [Fact]
    public void ResolveDeep_uses_a_hops_alternate_exchange_only_when_that_hop_matches_nothing()
    {
        ExchangeSummary[] exchanges =
        [
            Exchange("front", "fanout"),
            Exchange("picky", "direct", alternateExchange: "catch.all"),
            Exchange("catch.all", "fanout"),
        ];
        BindingInfo[] bindings =
        [
            Binding("front", "picky", "", destinationType: "exchange"),
            Binding("picky", "picky.q", "wanted"),
            Binding("catch.all", "unrouted.q", ""),
        ];

        RoutingMatcher.ResolveDeep(exchanges[0], exchanges, bindings, "other", NoHeaders)
            .Queues.Should().ContainSingle().Which.Path.Should().Equal("front", "picky", "catch.all", "unrouted.q");
        RoutingMatcher.ResolveDeep(exchanges[0], exchanges, bindings, "wanted", NoHeaders)
            .Queues.Should().ContainSingle().Which.Queue.Should().Be("picky.q");
    }

    [Fact]
    public void ResolveDeep_reports_a_plugin_typed_hop_as_unpredictable()
    {
        ExchangeSummary[] exchanges = [Exchange("front", "fanout"), Exchange("hash", "x-consistent-hash")];
        BindingInfo[] bindings = [Binding("front", "hash", "", destinationType: "exchange"), Binding("hash", "h.q", "1")];

        var deep = RoutingMatcher.ResolveDeep(exchanges[0], exchanges, bindings, "", NoHeaders);

        deep.UnpredictableExchanges.Should().Equal("hash");
        deep.Queues.Should().BeEmpty();
    }
```

Append to `DeadLetterTopologyTests`:

```csharp
    [Fact]
    public void A_queue_reached_from_a_dlx_through_an_exchange_hop_is_a_dlq()
    {
        QueueSummary[] queues = [Queue("work.q", "work.dlx", null), Queue("dlq.archive")];
        BindingInfo[] bindings =
        [
            Binding("work.dlx", "dlq.fan", "", destinationType: "exchange"),
            Binding("dlq.fan", "dlq.archive", ""),
        ];
        ExchangeSummary[] exchanges = [Exchange("work.dlx", "fanout"), Exchange("dlq.fan", "fanout")];

        var topology = DeadLetterTopology.Compute(queues, bindings, exchanges);

        topology.IsDeadLetterQueue("dlq.archive").Should().BeTrue();
        topology.RouteOut("work.q")!.TargetQueues.Should().Equal("dlq.archive");
        topology.SourcesOf("dlq.archive").Should().Equal("work.q");
    }

    [Fact]
    public void SourcesOf_lists_every_queue_whose_dead_letters_can_land_in_the_dlq()
    {
        Seeded().SourcesOf("payments-dlq").Should().Equal("billing.payments.q", "order-events.q");
        Seeded().SourcesOf("audit.sink").Should().BeEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~Routing"`
Expected: build FAILS — `ResolveDeep`, `SourcesOf` not defined.

- [ ] **Step 3: Implement `ResolveDeep`**

In `RoutingMatcher.cs`, change the class summary line "Exchange-to-exchange bindings are matched like any other and not followed." to "Resolve matches one exchange's bindings; ResolveDeep follows exchange-to-exchange bindings." Add above `RoutingMatcher`:

```csharp
/// <summary>A queue a message would reach, and the exchanges it passes through on the way (first = the publish exchange).</summary>
public sealed record RoutedQueue(string Queue, IReadOnlyList<string> Path, BindingInfo Binding);

/// <summary>
/// <see cref="Root"/> is the single-hop preview of the publish exchange (drives the blocked/closest
/// UI); <see cref="Queues"/> is every queue reached through any number of exchange hops.
/// </summary>
public sealed record DeepRoutePreview(
    RoutePreview Root,
    IReadOnlyList<RoutedQueue> Queues,
    bool ReachedHopLimit,
    IReadOnlyList<string> UnpredictableExchanges);
```

Inside `RoutingMatcher`, add:

```csharp
    /// <summary>Exchange hops followed before giving up (the broker has no limit; a preview needs one).</summary>
    public const int MaxHops = 8;

    public static string ExchangeLabel(ExchangeSummary exchange) => exchange.IsDefault ? "(AMQP default)" : exchange.Name;

    /// <summary>
    /// Follows exchange-to-exchange bindings breadth-first with the same routing key and headers,
    /// evaluating each exchange at most once (a cycle is not an error -- the broker also delivers
    /// one copy per queue). A hop that matches nothing falls through to its alternate exchange.
    /// A plugin-typed hop can't be predicted and is reported, not followed. Queues keep the first
    /// (shortest) path that reached them.
    /// </summary>
    public static DeepRoutePreview ResolveDeep(
        ExchangeSummary root,
        IReadOnlyList<ExchangeSummary> exchanges,
        IReadOnlyList<BindingInfo> vhostBindings,
        string routingKey,
        IReadOnlyDictionary<string, string> headers)
    {
        var byName = new Dictionary<string, ExchangeSummary>(StringComparer.Ordinal);
        foreach (var e in exchanges) byName.TryAdd(e.Name, e);

        IReadOnlyList<BindingInfo> BindingsOf(ExchangeSummary e) =>
            vhostBindings.Where(b => string.Equals(b.Source, e.Name, StringComparison.Ordinal)).ToList();

        var rootPreview = Resolve(root, BindingsOf(root), routingKey, headers);
        var queues = new List<RoutedQueue>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Name };
        var unpredictable = new List<string>();
        var reachedLimit = false;

        var frontier = new Queue<(ExchangeSummary Exchange, RoutePreview Preview, List<string> Path)>();
        frontier.Enqueue((root, rootPreview, [ExchangeLabel(root)]));

        while (frontier.Count > 0)
        {
            var (exchange, preview, path) = frontier.Dequeue();
            if (preview.IsUnknownType)
            {
                if (!ReferenceEquals(exchange, root)) unpredictable.Add(exchange.Name);
                continue;
            }

            void Follow(string next)
            {
                if (!byName.TryGetValue(next, out var nextExchange) || !visited.Add(next)) return;
                if (path.Count > MaxHops)
                {
                    reachedLimit = true;
                    return;
                }

                frontier.Enqueue((nextExchange, Resolve(nextExchange, BindingsOf(nextExchange), routingKey, headers), [.. path, next]));
            }

            if (preview.Matched.Count == 0 && !string.IsNullOrEmpty(exchange.AlternateExchange))
            {
                Follow(exchange.AlternateExchange);
                continue;
            }

            foreach (var binding in preview.Matched)
            {
                if (binding.IsToQueue)
                {
                    if (seenQueues.Add(binding.Destination))
                    {
                        queues.Add(new RoutedQueue(binding.Destination, [.. path, binding.Destination], binding));
                    }
                }
                else
                {
                    Follow(binding.Destination);
                }
            }
        }

        return new DeepRoutePreview(rootPreview, queues, reachedLimit, unpredictable);
    }
```

Check the hop-limit arithmetic against the test: path for root has 1 element; `x0 → … → x8` is a path of 9 elements = 8 hops. Following to `x9` is refused when `path.Count > MaxHops` (path `x0..x8` has 9 > 8), so `x11`'s queue is never reached and `ReachedHopLimit` is true. The cycle test: `loop.a` (visited) → `loop.b` enqueued; `loop.b`'s binding back to `loop.a` is refused by `visited`; `shared.q` is added once, from `loop.a` first (BFS order).

- [ ] **Step 4: Implement DLQ reachability in `DeadLetterTopology`**

In `DeadLetterTopology.Compute`, replace the loop

```csharp
        foreach (var b in bindings)
        {
            if (b.IsToQueue && dlxs.Contains(b.Source) && IsTerminal(b.Destination)) dlqs.Add(b.Destination);
        }
```

with

```csharp
        foreach (var dlx in dlxs)
        {
            foreach (var queue in ReachableQueues(dlx, bindings))
            {
                if (IsTerminal(queue)) dlqs.Add(queue);
            }
        }
```

Add these members to the class:

```csharp
    /// <summary>
    /// Every queue a message leaving <paramref name="exchange"/> could reach, ignoring routing keys,
    /// through any number of exchange-to-exchange hops (cycle-safe, <see cref="RoutingMatcher.MaxHops"/> deep).
    /// </summary>
    private static IReadOnlyList<string> ReachableQueues(string exchange, IReadOnlyList<BindingInfo> bindings)
    {
        var queues = new List<string>();
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { exchange };
        var frontier = new Queue<(string Exchange, int Depth)>();
        frontier.Enqueue((exchange, 0));
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            foreach (var b in bindings.Where(b => string.Equals(b.Source, current, StringComparison.Ordinal)))
            {
                if (b.IsToQueue)
                {
                    if (seenQueues.Add(b.Destination)) queues.Add(b.Destination);
                }
                else if (depth < RoutingMatcher.MaxHops && visited.Add(b.Destination))
                {
                    frontier.Enqueue((b.Destination, depth + 1));
                }
            }
        }

        return queues;
    }

    /// <summary>
    /// The queues whose dead letters can land in <paramref name="deadLetterQueue"/> (their route out
    /// lists it), ordinal-sorted. Empty for a queue that isn't a DLQ.
    /// </summary>
    public IReadOnlyList<string> SourcesOf(string deadLetterQueue) =>
        _queues.Keys
            .Where(q => RouteOut(q) is { } route && route.TargetQueues.Contains(deadLetterQueue, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
```

In `RouteOut`, make the targets follow exchange hops. Replace the three `QueueTargets(...)` returns:

```csharp
        if (exchange.Type == "fanout")
        {
            return new DeadLetterRoute(dlx, dlrk, ReachableQueues(dlx, _bindings).ToList(), false, setBy);
        }

        if (keyRouted && dlrk is not null)
        {
            var deep = RoutingMatcher.ResolveDeep(exchange, [.. _exchanges.Values, exchange], exchange.IsDefault ? bindings : _bindings, dlrk, new Dictionary<string, string>());
            return new DeadLetterRoute(dlx, dlrk, deep.Queues.Select(q => q.Queue).ToList(), false, setBy);
        }

        if (exchange.IsDefault)
        {
            return new DeadLetterRoute(dlx, dlrk, [], true, setBy);
        }

        return new DeadLetterRoute(dlx, dlrk, ReachableQueues(dlx, _bindings).ToList(), true, setBy);
```

and delete the now-unused `QueueTargets` helper. (`[.. _exchanges.Values, exchange]`: the synthesized exchange for an unknown DLX isn't in `_exchanges`; `ResolveDeep` de-duplicates by name with `TryAdd`, so the real one wins when both exist.)

- [ ] **Step 5: Run the routing tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~Routing"`
Expected: PASS, including every pre-existing `DeadLetterTopologyTests` case.

- [ ] **Step 6: Write the failing handler test for `ExchangeBindings`**

In `QueryHandlerTests.cs`, replace `Exchange_bindings_are_filtered_to_the_source_exchange` with:

```csharp
    [Fact]
    public async Task Exchange_bindings_split_outgoing_and_incoming_and_keep_the_whole_vhost()
    {
        var toQueue = Binding("order-events", "a", "order.*.created");
        var toExchange = new BindingInfo("order-events", "audit.fanout", "exchange", "order.#", new Dictionary<string, object?>(), "order.%23");
        var intoOrderEvents = new BindingInfo("ingress", "order-events", "exchange", "", new Dictionary<string, object?>(), "~");
        var unrelated = Binding("audit.fanout", "b");
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([toQueue, toExchange, intoOrderEvents, unrelated]);

        var result = await new ListExchangeBindingsQueryHandler(Operations, Connections, Log<ListExchangeBindingsQueryHandler>())
            .HandleAsync(ConnectionId, Vhost, "order-events");

        result.Value!.Outgoing.Should().Equal(toQueue, toExchange);
        result.Value.Incoming.Should().Equal(intoOrderEvents);
        result.Value.Vhost.Should().HaveCount(4);
    }
```

and in `Default_exchange_bindings_are_synthesized_from_the_queue_list` change `result.Value!.Select(` to `result.Value!.Outgoing.Select(`.

- [ ] **Step 7: Implement the handler change**

Replace `Exchanges/ListExchangeBindingsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

/// <summary>
/// One exchange's bindings in both directions, plus every binding in the vhost (what
/// <see cref="RoutingMatcher.ResolveDeep"/> needs to follow exchange hops). Incoming lists only
/// exchange sources -- a queue can't be a binding source.
/// </summary>
public sealed record ExchangeBindings(IReadOnlyList<BindingInfo> Outgoing, IReadOnlyList<BindingInfo> Incoming, IReadOnlyList<BindingInfo> Vhost);

/// <summary>
/// The default exchange has no listable bindings of its own (every queue is implicitly bound by its
/// own name) and can't take part in exchange-to-exchange bindings, so its bindings are synthesized
/// from the queue list and it has nothing incoming.
/// </summary>
public sealed class ListExchangeBindingsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListExchangeBindingsQueryHandler> logger)
{
    public Task<PluginResult<ExchangeBindings>> HandleAsync(Guid connectionId, string vhost, string exchange, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing exchange bindings", async secret =>
        {
            if (exchange.Length == 0)
            {
                var queues = await operations.ListQueuesAsync(secret, vhost, ct);
                var synthesized = RoutingMatcher.SynthesizeDefaultExchangeBindings(queues.Select(q => q.Name));
                return new ExchangeBindings(synthesized, [], synthesized);
            }

            var bindings = await operations.ListBindingsAsync(secret, vhost, ct);
            return new ExchangeBindings(
                bindings.Where(b => b.Source == exchange).ToList(),
                bindings.Where(b => !b.IsToQueue && b.Destination == exchange).ToList(),
                bindings);
        }, ct);
}
```

Keep the two callers compiling with the smallest possible change (Tasks 3 and 4 rewrite them properly):
- `Pages/PublishDialog.razor`: in `LoadBindingsAsync`, change `_bindingsByExchange[exchange.Name] = result.Value!;` to `_bindingsByExchange[exchange.Name] = result.Value!.Outgoing;`.
- `Pages/ExchangeBindingsDialog.razor`: in `OnInitializedAsync`, change `_bindings = result.Value!;` to `_bindings = result.Value!.Outgoing;`.

- [ ] **Step 8: Run the whole plugin test project**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Routing src/Plugins/SbConsole.Plugins.RabbitMq/Exchanges/ListExchangeBindingsQueryHandler.cs src/Plugins/SbConsole.Plugins.RabbitMq/Pages/PublishDialog.razor src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ExchangeBindingsDialog.razor tests/SbConsole.Plugins.RabbitMq.Tests/Routing tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/QueryHandlerTests.cs
git commit -m "feat(rabbitmq): follow exchange-to-exchange bindings in routing and DLQ topology

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Publish dialog shows the deep route

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/PublishDialog.razor`
- Test: `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/PublishDialogTests.cs`

**Interfaces:**
- Consumes: `ExchangeBindings`, `RoutingMatcher.ResolveDeep`, `DeepRoutePreview`, `RoutedQueue` (Task 2).
- Produces: `internal DeepRoutePreview? DeepPreview` on `PublishDialog`; route rows `.route-row` with `.route-destination` (queue name) and `.route-path` (`a → b → q`, only rendered when the path has more than 2 elements).

- [ ] **Step 1: Write the failing test**

Open `PublishDialogTests.cs` and find its render helper (the method that calls `RenderDialog<PublishDialog>` with `[nameof(PublishDialog.Exchange)] = exchange`). Add:

```csharp
    [Fact]
    public async Task Preview_follows_an_exchange_binding_and_shows_the_path()
    {
        var bindings = SeededTopology.Bindings();
        bindings.Add(SeededTopology.Binding("order-events", "audit.fanout", "order.#", destinationType: "exchange"));
        bindings.Add(SeededTopology.Binding("audit.fanout", "audit.archive", ""));
        var exchanges = SeededTopology.Exchanges();
        exchanges.Add(SeededTopology.Exchange("audit.fanout", "fanout"));
        Operations.ListExchangesAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(exchanges);
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(bindings);
        Operations.ListQueuesAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(SeededTopology.Queues());

        var cut = await RenderAsync("order-events", passExchanges: false);
        await TypeRoutingKeyAsync(cut, "order.uk.created");

        cut.Find(".route-summary").TextContent.Should().Contain("Will route to 3 queues");
        var archive = cut.FindAll(".route-row").Single(r => r.QuerySelector(".route-destination")!.TextContent == "audit.archive");
        archive.QuerySelector(".route-path")!.TextContent.Should().Be("order-events → audit.fanout → audit.archive");
    }
```

`passExchanges: false` makes the dialog read the exchange list itself (through `ListExchangesQueryHandler`), so it sees `audit.fanout`; `SeededTopology.Exchanges()` alone doesn't contain it. The expected 3 queues are `order-events.q` (order.*.created), `audit.sink` (#) and `audit.archive` (via `audit.fanout`).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~PublishDialogTests"`
Expected: FAIL — the summary says 2 queues, and there is no `.route-path`.

- [ ] **Step 3: Implement**

In `PublishDialog.razor`'s `@code`:

1. Replace the field `private readonly Dictionary<string, IReadOnlyList<BindingInfo>> _bindingsByExchange = new(StringComparer.Ordinal);` with
   ```csharp
   private readonly Dictionary<string, ExchangeBindings> _bindingsByExchange = new(StringComparer.Ordinal);
   ```
   and in `LoadBindingsAsync` store `result.Value!` (drop the `.Outgoing` added in Task 2).
2. Replace the `Preview` property with:
   ```csharp
   /// <summary>The routing preview for the current inputs (exchange hops followed), or null while it can't be computed.</summary>
   internal DeepRoutePreview? DeepPreview =>
       !_bindingsLoading && _bindingsError is null && SelectedExchange is { Type.Length: > 0 } exchange
       && _bindingsByExchange.TryGetValue(exchange.Name, out var bindings)
           ? RoutingMatcher.ResolveDeep(exchange, _exchanges, bindings.Vhost, _routingKey, _headers)
           : null;

   internal RoutePreview? Preview => DeepPreview?.Root;
   ```
3. Replace `QueueCount` with:
   ```csharp
   private static int QueueCount(DeepRoutePreview preview) => preview.Queues.Count;
   ```
4. In `RenderPreview`, add `var deep = DeepPreview;` after `var exchange = SelectedExchange;`, and replace the `else if (preview.Matched.Count > 0) { … }` branch with:
   ```razor
            else if (deep is not null && preview.Matched.Count > 0)
            {
                <MudText Class="route-summary" Typo="Typo.subtitle2" Color="Color.Success">Will route to @Plural(QueueCount(deep), "queue")</MudText>
                @foreach (var routed in deep.Queues)
                {
                    <div class="route-row d-flex gap-3" style="font-family:monospace">
                        <span class="route-destination">@routed.Queue</span>
                        <span class="route-key mud-text-secondary">@BindingKeyText(exchange, routed.Binding)</span>
                        @if (routed.Path.Count > 2)
                        {
                            <span class="route-path mud-text-secondary">@string.Join(" → ", routed.Path)</span>
                        }
                    </div>
                }
                @if (deep.ReachedHopLimit)
                {
                    <MudText Class="route-hop-limit mud-text-secondary" Typo="Typo.caption">Stopped following exchange bindings after @RoutingMatcher.MaxHops hops.</MudText>
                }
                @foreach (var unpredictable in deep.UnpredictableExchanges)
                {
                    <MudText Class="route-unpredictable mud-text-secondary d-block" Typo="Typo.caption">Continues into <span style="font-family:monospace">@unpredictable</span>, whose type can't be previewed.</MudText>
                }
            }
   ```
   The branch keys on the *root* having matched: when the root matches nothing, the existing alternate-exchange / "Matches no binding" branches below still render exactly as before (so `.route-alternate` tests keep passing). `BindingKeyText(exchange, routed.Binding)` formats with the *root* exchange's type; for a routed binding from a headers hop that shows the key rather than arguments — acceptable, the path column names the hop.
5. In `PublishAsync`, replace the success-message block with:
   ```csharp
                var deep = DeepPreview;
                // A count only when the preview fully accounts for it (no unpredictable hop, no hop limit).
                var message = deep is { Root.IsUnknownType: false, Queues.Count: > 0, ReachedHopLimit: false, UnpredictableExchanges.Count: 0 }
                    ? $"Published — routed to {Plural(QueueCount(deep), "queue")}"
                    : "Published — routed";
   ```
   and delete the now-unused `var preview = Preview;` line at the top of the `try`.
6. `Blocked` stays `Preview is { IsUnknownType: false, Matched.Count: 0, HasAlternateExchange: false } && !_publishAnyway;` — the root decides whether anything is routed at all.
7. Add `@using SbConsole.Plugins.RabbitMq.Exchanges` at the top if not present.

- [ ] **Step 4: Run the publish tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~PublishDialogTests|FullyQualifiedName~ExchangesPageTests|FullyQualifiedName~QueuesPageTests|FullyQualifiedName~QueueDetailPageTests"`
Expected: PASS. If a pre-existing test asserted the old `→ exchange X (routing continues there)` row text, update its expectation to the new row (queue destination + `.route-path`) — that text no longer exists by design.

- [ ] **Step 5: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Pages/PublishDialog.razor tests/SbConsole.Plugins.RabbitMq.Tests/Pages/PublishDialogTests.cs
git commit -m "feat(rabbitmq): publish preview follows exchange hops and shows the path

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Exchange bindings dialog — both directions, Unbind, Add binding (queue or exchange)

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ExchangeBindingsDialog.razor`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/AddBindingDialog.razor`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/Exchanges.razor` (`OpenBindingsAsync`)
- Test: `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ExchangeDialogsTests.cs`, `Pages/AddBindingDialogTests.cs`, `Pages/ExchangesPageTests.cs`

**Interfaces:**
- Consumes: `ExchangeBindings` (Task 2), `RemoveBindingCommand`/`AddBindingCommand` with `DestinationType` (Task 1).
- Produces:
  - `ExchangeBindingsDialog` parameters: existing `ConnectionId`, `Vhost`, `Exchange`, `AlternateExchange`, plus `ConnectionName` (string, default ""), `IsProd` (bool), `Exchanges` (`IReadOnlyList<ExchangeSummary>`, default `[]`), `Queues` (`IReadOnlyList<QueueSummary>`, default `[]`). Closes with `DialogResult.Ok(bool changed)`.
  - CSS hooks: `.binding-row` (outgoing, unchanged), `.incoming-binding-row` with `.incoming-source`, `.routes-to-heading`, `.receives-from-heading`, `button.unbind-binding`, `button.add-exchange-binding`.
  - `AddBindingDialog` new parameters `SourceExchange` (string?, null = the existing queue mode) and `Queues` (`IReadOnlyList<QueueSummary>`, default `[]`); selects with classes `binding-destination-type` (`MudSelect<BindingDestinationType>`) and `binding-destination` (`MudSelect<string>`); `internal IReadOnlyList<string> DestinationCandidates`.

Note on the spec: the Exchanges "Bindings" count and the "no bindings" warning stay outgoing-only. An exchange that only *receives* from another exchange and binds nowhere still drops every message, so the warning must stay on for it. (Recorded as a deviation in Task 12.)

- [ ] **Step 1: Write the failing dialog tests**

In `ExchangeDialogsTests.cs`, extend `RenderBindingsAsync` with the new parameters:

```csharp
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderBindingsAsync(string exchange, string? alternate = null, bool prod = false)
    {
        var cut = RenderDialog<ExchangeBindingsDialog>(new Dictionary<string, object?>
        {
            [nameof(ExchangeBindingsDialog.ConnectionId)] = Dev.Id,
            [nameof(ExchangeBindingsDialog.ConnectionName)] = Dev.Name,
            [nameof(ExchangeBindingsDialog.IsProd)] = prod,
            [nameof(ExchangeBindingsDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(ExchangeBindingsDialog.Exchange)] = exchange,
            [nameof(ExchangeBindingsDialog.AlternateExchange)] = alternate,
            [nameof(ExchangeBindingsDialog.Exchanges)] = SeededTopology.Exchanges(),
            [nameof(ExchangeBindingsDialog.Queues)] = SeededTopology.Queues(),
        });
        await SettleAsync(cut);
        return cut;
    }
```

and add:

```csharp
    [Fact]
    public async Task Bindings_dialog_lists_exchanges_that_bind_into_this_one()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(
            SeededTopology.Bindings().Append(SeededTopology.Binding("order-events", "notify.fanout", "order.#", destinationType: "exchange")).ToList());

        var cut = await RenderBindingsAsync("notify.fanout");

        cut.Find(".routes-to-heading").TextContent.Should().Contain("Routes to — 2");
        cut.Find(".receives-from-heading").TextContent.Should().Contain("Receives from — 1");
        cut.Find(".incoming-binding-row .incoming-source").TextContent.Should().Be("order-events");
    }

    [Fact]
    public async Task Unbind_confirms_with_the_server_side_prod_flag_then_removes_by_destination_type()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(
            SeededTopology.Bindings().Append(SeededTopology.Binding("order-events", "notify.fanout", "order.#", destinationType: "exchange")).ToList());
        Confirmation.ConfirmAsync("Unbind", "order-events → notify.fanout", true, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = await RenderBindingsAsync("order-events", prod: true);
        var row = cut.FindAll(".binding-row").Single(r => r.QuerySelector(".binding-destination")!.TextContent == "notify.fanout");
        await row.QuerySelector("button.unbind-binding")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).RemoveExchangeBindingAsync(DevSecret, SeededTopology.Vhost, "order-events", "notify.fanout", "order.#", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_unbind_confirmation_removes_nothing()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);

        var cut = await RenderBindingsAsync("order-events");
        await cut.FindAll("button.unbind-binding")[0].ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().RemoveBindingAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task The_default_exchange_offers_neither_unbind_nor_add()
    {
        SeededTopology.Seed(Operations, DevSecret);

        var cut = await RenderBindingsAsync("");

        cut.FindAll("button.unbind-binding").Should().BeEmpty();
        cut.FindAll("button.add-exchange-binding").Should().BeEmpty();
    }
```

(`SeededTopology.Binding` sets `PropertiesKey` to the routing key, so `"order.#"` is what `RemoveExchangeBindingAsync` receives.)

In `AddBindingDialogTests.cs` add:

```csharp
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderFromExchangeAsync(string source)
    {
        var cut = RenderDialog<AddBindingDialog>(new Dictionary<string, object?>
        {
            [nameof(AddBindingDialog.ConnectionId)] = Dev.Id,
            [nameof(AddBindingDialog.ConnectionName)] = Dev.Name,
            [nameof(AddBindingDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(AddBindingDialog.SourceExchange)] = source,
            [nameof(AddBindingDialog.Exchanges)] = SeededTopology.Exchanges(),
            [nameof(AddBindingDialog.Queues)] = SeededTopology.Queues(),
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task From_an_exchange_the_destination_can_be_another_exchange()
    {
        var cut = await RenderFromExchangeAsync("order-events");

        await SelectAsync(cut, "binding-destination-type", BindingDestinationType.Exchange);
        await SelectAsync(cut, "binding-destination", "notify.fanout");
        await cut.Find(".binding-routing-key input").InputAsync(new ChangeEventArgs { Value = "order.#" });
        await cut.Find("button.add-binding-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).AddExchangeBindingAsync(DevSecret, SeededTopology.Vhost, "order-events", "notify.fanout", "order.#",
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Any<DialogResult>());
    }

    [Fact]
    public async Task From_an_exchange_the_source_and_the_default_exchange_are_not_offered_as_destinations()
    {
        var cut = await RenderFromExchangeAsync("order-events");
        await SelectAsync(cut, "binding-destination-type", BindingDestinationType.Exchange);

        var candidates = cut.FindComponent<AddBindingDialog>().Instance.DestinationCandidates;
        candidates.Should().NotContain("order-events").And.NotContain("").And.Contain("notify.fanout");
    }

    [Fact]
    public async Task From_an_exchange_the_source_type_decides_the_key_fields()
    {
        var cut = await RenderFromExchangeAsync("notify.fanout");

        cut.FindAll(".binding-routing-key").Should().BeEmpty();
        cut.Find(".fanout-caption").TextContent.Should().Contain("Fanout ignores the routing key");
    }
```

In `ExchangesPageTests.cs` add (mirrors the existing Publish-dialog test):

```csharp
    [Fact]
    public async Task Bindings_opens_the_dialog_with_the_server_side_prod_flag_and_the_lists()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var dialogs = Substitute.For<MudBlazor.IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await Row(cut, "order-events").QuerySelector(".bindings-exchange")!.ClickAsync(new());

        await dialogs.Received(1).ShowAsync<SbConsole.Plugins.RabbitMq.Pages.ExchangeBindingsDialog>(
            Arg.Any<string>(),
            Arg.Is<MudBlazor.DialogParameters>(p =>
                (bool)p[nameof(SbConsole.Plugins.RabbitMq.Pages.ExchangeBindingsDialog.IsProd)]! == false
                && (string)p[nameof(SbConsole.Plugins.RabbitMq.Pages.ExchangeBindingsDialog.ConnectionName)]! == Dev.Name
                && p[nameof(SbConsole.Plugins.RabbitMq.Pages.ExchangeBindingsDialog.Queues)] != null
                && p[nameof(SbConsole.Plugins.RabbitMq.Pages.ExchangeBindingsDialog.Exchanges)] != null),
            Arg.Any<MudBlazor.DialogOptions>());
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~ExchangeDialogsTests|FullyQualifiedName~AddBindingDialogTests|FullyQualifiedName~ExchangesPageTests"`
Expected: build FAILS (unknown parameters `ConnectionName`, `IsProd`, `SourceExchange`, …).

- [ ] **Step 3: Rewrite `ExchangeBindingsDialog.razor`**

```razor
@using System.Globalization
@using SbConsole.Plugins.RabbitMq.Bindings
@using SbConsole.Plugins.RabbitMq.Client
@using SbConsole.Plugins.RabbitMq.Exchanges
@inject ListExchangeBindingsQueryHandler BindingsHandler
@inject RemoveBindingCommandHandler RemoveHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        @if (_loading && _bindings is null)
        {
            <MudProgressLinear Class="bindings-loading" Color="Color.Primary" Indeterminate="true" />
        }
        else if (_error is not null)
        {
            <MudAlert Class="bindings-error" Severity="Severity.Error" Dense="true">@_error</MudAlert>
        }
        else if (_bindings is { } bindings)
        {
            <MudText Class="routes-to-heading mb-1" Typo="Typo.subtitle2">Routes to — @bindings.Outgoing.Count.ToString(CultureInfo.InvariantCulture)</MudText>
            @if (bindings.Outgoing.Count == 0)
            {
                <MudText Class="no-exchange-bindings mud-text-secondary" Typo="Typo.body2">
                    No bindings — messages published here are dropped@(string.IsNullOrEmpty(AlternateExchange) ? "" : $" unless the alternate exchange {AlternateExchange} takes them").
                </MudText>
            }
            else
            {
                <MudSimpleTable Dense="true" Elevation="0">
                    <thead>
                        <tr><th>Destination</th><th>Type</th><th>Routing key</th><th>Arguments</th><th></th></tr>
                    </thead>
                    <tbody>
                        @foreach (var binding in bindings.Outgoing)
                        {
                            <tr class="binding-row">
                                <td class="binding-destination" style="font-family:monospace">@binding.Destination</td>
                                <td class="binding-type">@binding.DestinationType</td>
                                <td class="binding-key" style="font-family:monospace">@KeyText(binding)</td>
                                <td class="binding-args" style="font-family:monospace">@FormatArguments(binding.Arguments)</td>
                                <td>
                                    @if (!IsDefault)
                                    {
                                        <MudButton Class="unbind-binding" Size="Size.Small" Variant="Variant.Text" Color="Color.Warning"
                                                   Disabled="_busy" OnClick="@(() => UnbindAsync(binding))">Unbind</MudButton>
                                    }
                                </td>
                            </tr>
                        }
                    </tbody>
                </MudSimpleTable>
            }

            @if (bindings.Incoming.Count > 0)
            {
                <MudText Class="receives-from-heading mt-4 mb-1" Typo="Typo.subtitle2">Receives from — @bindings.Incoming.Count.ToString(CultureInfo.InvariantCulture)</MudText>
                <MudSimpleTable Dense="true" Elevation="0">
                    <thead>
                        <tr><th>Source exchange</th><th>Routing key</th><th>Arguments</th><th></th></tr>
                    </thead>
                    <tbody>
                        @foreach (var binding in bindings.Incoming)
                        {
                            <tr class="incoming-binding-row">
                                <td class="incoming-source" style="font-family:monospace">@binding.Source</td>
                                <td class="binding-key" style="font-family:monospace">@KeyText(binding)</td>
                                <td class="binding-args" style="font-family:monospace">@FormatArguments(binding.Arguments)</td>
                                <td>
                                    <MudButton Class="unbind-binding" Size="Size.Small" Variant="Variant.Text" Color="Color.Warning"
                                               Disabled="_busy" OnClick="@(() => UnbindAsync(binding))">Unbind</MudButton>
                                </td>
                            </tr>
                        }
                    </tbody>
                </MudSimpleTable>
            }
        }
    </DialogContent>
    <DialogActions>
        @if (!IsDefault)
        {
            <MudButton Class="add-exchange-binding mr-auto ml-2" StartIcon="@Icons.Material.Filled.Add" Disabled="_busy" OnClick="AddAsync">Add binding</MudButton>
        }
        <MudButton Class="close-bindings" OnClick="Close">Close</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    /// <summary>From the page's server-side ConnectionInfo, never from the client.</summary>
    [Parameter] public bool IsProd { get; set; }
    [Parameter] public string Vhost { get; set; } = "/";
    [Parameter] public string Exchange { get; set; } = "";
    [Parameter] public string? AlternateExchange { get; set; }
    [Parameter] public IReadOnlyList<ExchangeSummary> Exchanges { get; set; } = [];
    [Parameter] public IReadOnlyList<QueueSummary> Queues { get; set; } = [];

    private ExchangeBindings? _bindings;
    private string? _error;
    private bool _loading = true;
    private bool _busy;
    private bool _changed;

    // The default exchange's bindings are implicit: they can't be removed or added to.
    private bool IsDefault => Exchange.Length == 0;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await BindingsHandler.HandleAsync(ConnectionId, Vhost, Exchange);
            if (result.IsSuccess)
            {
                _bindings = result.Value!;
                _error = null;
            }
            else
            {
                _error = result.Error;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    internal static string FormatArguments(IReadOnlyDictionary<string, object?> arguments) =>
        string.Join(" · ", arguments.Select(a => $"{a.Key}={Convert.ToString(a.Value, CultureInfo.InvariantCulture)}"));

    private static string KeyText(BindingInfo binding) => binding.RoutingKey.Length == 0 ? "(no key)" : binding.RoutingKey;

    private async Task UnbindAsync(BindingInfo binding)
    {
        // Same rule as queue detail's Unbind: typed confirmation on prod, two-button otherwise.
        if (!await Confirmation.ConfirmAsync("Unbind", $"{binding.Source} → {binding.Destination}", IsProd))
        {
            return;
        }

        _busy = true;
        try
        {
            var type = binding.IsToQueue ? BindingDestinationType.Queue : BindingDestinationType.Exchange;
            var result = await RemoveHandler.HandleAsync(new RemoveBindingCommand(
                ConnectionId, ConnectionName, Vhost, binding.Source, binding.Destination, binding.RoutingKey, binding.PropertiesKey, type));
            if (result.IsSuccess)
            {
                _changed = true;
                Snackbar.Add($"Unbound {binding.Source} → {binding.Destination}", Severity.Success);
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    private async Task AddAsync()
    {
        var parameters = new DialogParameters<AddBindingDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.ConnectionName, ConnectionName },
            { x => x.Vhost, Vhost },
            { x => x.SourceExchange, Exchange },
            { x => x.Exchanges, Exchanges },
            { x => x.Queues, Queues },
        };
        var dialog = await DialogService.ShowAsync<AddBindingDialog>($"Add binding — {Exchange}", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            _changed = true;
            await LoadAsync();
        }
    }

    private void Close() => MudDialog.Close(DialogResult.Ok(_changed));
}
```

Check the existing test `Bindings_dialog_lists_destination_type_key_and_arguments` still finds exactly 2 `.binding-row`s and that the old `.no-exchange-bindings` texts are unchanged — they are, by construction.

- [ ] **Step 4: Add source-exchange mode to `AddBindingDialog.razor`**

Replace the markup from the top down to (not including) `@if (IsFanout)` with:

```razor
@using SbConsole.Plugins.RabbitMq.Bindings
@using SbConsole.Plugins.RabbitMq.Client

<MudDialog>
    <DialogContent>
        @if (SourceExchange is null)
        {
            <MudText Typo="Typo.body2" Class="mud-text-secondary mb-2">
                Bind <span style="font-family:monospace">@Queue</span> · @RabbitFormat.VhostLabel(Vhost)
            </MudText>
            <div class="d-flex align-center gap-2">
                <MudSelect T="string" Class="binding-exchange" Value="_exchange" ValueChanged="OnExchangeChanged" Label="Exchange">
                    @foreach (var exchange in Candidates)
                    {
                        <MudSelectItem Value="@exchange.Name">@exchange.Name</MudSelectItem>
                    }
                </MudSelect>
                @if (SourceSummary is { } selected)
                {
                    <MudChip T="string" Class="binding-exchange-type" Size="Size.Small"
                             Color="@(selected.Type == "topic" ? Color.Primary : Color.Default)"
                             Variant="@(selected.Type == "topic" ? Variant.Filled : Variant.Outlined)">@selected.Type</MudChip>
                }
            </div>
        }
        else
        {
            <MudText Typo="Typo.body2" Class="mud-text-secondary mb-2">
                Bind from <span style="font-family:monospace">@SourceExchange</span>@(SourceSummary is { } source ? $" ({source.Type})" : "") · @RabbitFormat.VhostLabel(Vhost)
            </MudText>
            <div class="d-flex align-center gap-2">
                <MudSelect T="BindingDestinationType" Class="binding-destination-type" Value="_destinationType" ValueChanged="OnDestinationTypeChanged"
                           Label="Destination type" Style="max-width:160px">
                    <MudSelectItem Value="BindingDestinationType.Queue">Queue</MudSelectItem>
                    <MudSelectItem Value="BindingDestinationType.Exchange">Exchange</MudSelectItem>
                </MudSelect>
                <MudSelect T="string" Class="binding-destination" Value="_destination" ValueChanged="OnDestinationChanged"
                           Label="@(_destinationType == BindingDestinationType.Queue ? "Queue" : "Exchange")">
                    @foreach (var name in DestinationCandidates)
                    {
                        <MudSelectItem Value="@name">@name</MudSelectItem>
                    }
                </MudSelect>
            </div>
        }
```

(The remaining markup — `@if (IsFanout) … else …`, the headers block, the error alert and `DialogActions` — is unchanged.)

In `@code`, add the parameters and state, and replace `SelectedExchange`/`IsFanout`/`IsHeaders`/`CanSubmit`/`SaveAsync`:

```csharp
    /// <summary>Set when opened from an exchange: the source is fixed and the destination is picked (queue or exchange).</summary>
    [Parameter] public string? SourceExchange { get; set; }
    /// <summary>Destination candidates in source-exchange mode.</summary>
    [Parameter] public IReadOnlyList<QueueSummary> Queues { get; set; } = [];

    private BindingDestinationType _destinationType = BindingDestinationType.Queue;
    private string? _destination;

    /// <summary>The exchange whose type decides key/headers matching: the fixed source, or the one picked for a queue.</summary>
    private ExchangeSummary? SourceSummary => Exchanges.FirstOrDefault(e => e.Name == (SourceExchange ?? _exchange));
    private bool IsFanout => SourceSummary?.Type == "fanout";
    private bool IsHeaders => SourceSummary?.Type == "headers";

    // Neither the default exchange (it can't be bound to) nor the source itself (no self-binding).
    internal IReadOnlyList<string> DestinationCandidates => _destinationType == BindingDestinationType.Queue
        ? Queues.Select(q => q.Name).Order(StringComparer.Ordinal).ToList()
        : Exchanges.Where(e => !e.IsDefault && e.Name != SourceExchange).Select(e => e.Name).Order(StringComparer.Ordinal).ToList();

    private bool CanSubmit =>
        SourceSummary is not null
        && (SourceExchange is null || !string.IsNullOrEmpty(_destination))
        && (!IsHeaders || _argumentErrors.Count == 0);

    private void OnDestinationTypeChanged(BindingDestinationType type)
    {
        _destinationType = type;
        _destination = null;
    }

    private void OnDestinationChanged(string? destination) => _destination = destination;
```

In `SaveAsync`, replace the guard and the command construction:

```csharp
        if (!CanSubmit || SourceSummary is not { } exchange)
        {
            return;
        }
```

```csharp
            var destination = SourceExchange is null ? Queue : _destination!;
            var type = SourceExchange is null ? BindingDestinationType.Queue : _destinationType;
            var result = await AddHandler.HandleAsync(new AddBindingCommand(
                ConnectionId, ConnectionName, Vhost, exchange.Name, destination, routingKey, arguments, type));
```

Delete the old `SelectedExchange` property (every use now reads `SourceSummary`).

- [ ] **Step 5: Wire the Exchanges page**

In `Exchanges.razor`, replace `OpenBindingsAsync`:

```csharp
    private async Task OpenBindingsAsync(ExchangeSummary exchange)
    {
        var selection = _selection!;
        var parameters = new DialogParameters<ExchangeBindingsDialog>
        {
            { x => x.ConnectionId, selection.Connection.Id },
            { x => x.ConnectionName, selection.Connection.Name },
            // IsProd comes from the server-side connection lookup the picker made, never from the client.
            { x => x.IsProd, selection.Connection.IsProd },
            { x => x.Vhost, selection.Vhost },
            { x => x.Exchange, exchange.Name },
            { x => x.AlternateExchange, exchange.AlternateExchange },
            { x => x.Exchanges, _snapshot?.Exchanges ?? [] },
            { x => x.Queues, _snapshot?.Queues ?? [] },
        };
        var dialog = await DialogService.ShowAsync<ExchangeBindingsDialog>($"Bindings — {Label(exchange)}", parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: true })
        {
            await LoadAsync();
        }
    }
```

With a substituted `IDialogService` in tests, `ShowAsync` returns an NSubstitute auto-value whose `Result` is a completed task of a substitute `DialogResult`, so `Data` is null and no reload happens — the test doesn't depend on it either way.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ExchangeBindingsDialog.razor src/Plugins/SbConsole.Plugins.RabbitMq/Pages/AddBindingDialog.razor src/Plugins/SbConsole.Plugins.RabbitMq/Pages/Exchanges.razor tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ExchangeDialogsTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/AddBindingDialogTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ExchangesPageTests.cs
git commit -m "feat(rabbitmq): exchange bindings dialog shows both directions, unbinds, binds to exchanges

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Policy write operations, `PolicyDefinitionBuilder`, policy command handlers

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/ManagementApiClient.cs` (after `ListPoliciesAsync` writes section — put the two methods after `RestartShovelAsync`)
- Modify: `Client/IRabbitOperations.cs`, `Client/RabbitOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Policies/PolicyDefinitionBuilder.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Policies/PutPolicyCommandHandler.cs`, `Policies/DeletePolicyCommandHandler.cs`
- Modify: `RabbitMqPlugin.cs` (DI), `tests/.../Components/RabbitPageTestBase.cs` (DI)
- Test: `tests/SbConsole.Plugins.RabbitMq.Tests/Client/ManagementApiClientWriteTests.cs`, create `tests/SbConsole.Plugins.RabbitMq.Tests/Policies/PolicyDefinitionBuilderTests.cs`, `Handlers/CommandHandlerTests.cs`

**Interfaces:**
- Produces:
  - `IRabbitOperations.PutPolicyAsync(string secret, string vhost, PolicyInfo policy, CancellationToken ct = default)`
  - `IRabbitOperations.DeletePolicyAsync(string secret, string vhost, string name, CancellationToken ct = default)`
  - `record PolicyFields` (all `string?`, raw user text): `DeadLetterExchange`, `DeadLetterRoutingKey`, `MessageTtl`, `Expires`, `MaxLength`, `MaxLengthBytes`, `Overflow`, `QueueMode`, `DeliveryLimit`, `AlternateExchange`
  - `record PolicyDefinitionResult(IReadOnlyDictionary<string, object?> Definition, IReadOnlyList<string> Errors)` with `bool IsValid`
  - `PolicyDefinitionBuilder.Build(PolicyFields fields, string? extraJson) : PolicyDefinitionResult`
  - `PolicyDefinitionBuilder.Split(IReadOnlyDictionary<string, object?> definition) : (PolicyFields Fields, string ExtraJson)`
  - `PolicyDefinitionBuilder.ValidatePattern(string? pattern) : string?` (null = valid)
  - `record PutPolicyCommand(Guid ConnectionId, string ConnectionName, string Vhost, PolicyInfo Policy, bool IsNew)`, `PutPolicyCommandHandler.HandleAsync(PutPolicyCommand, CancellationToken) : Task<PluginResult>`
  - `record DeletePolicyCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name)`, `DeletePolicyCommandHandler`

- [ ] **Step 1: Write the failing client tests**

Append to `ManagementApiClientWriteTests`:

```csharp
    [Fact]
    public async Task PutPolicy_puts_pattern_apply_to_priority_and_definition()
    {
        var (client, handler) = Created("PUT", "/api/policies/%2Forders/dlq-ttl");

        await client.PutPolicyAsync(TestSettings.Orders, "/orders", new PolicyInfo("dlq-ttl", "-dlq$", "queues", 5,
            new Dictionary<string, object?> { ["message-ttl"] = 604800000L, ["dead-letter-exchange"] = "archive" }));

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Put);
        Json(request.Body).Should().Be(Json("""
            {"pattern":"-dlq$","apply-to":"queues","priority":5,"definition":{"message-ttl":604800000,"dead-letter-exchange":"archive"}}
            """));
    }

    [Fact]
    public async Task DeletePolicy_deletes_the_escaped_name()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/policies/%2Forders/orders%20limits", "", HttpStatusCode.NoContent);

        await client.DeletePolicyAsync(TestSettings.Orders, "/orders", "orders limits");

        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    }
```

- [ ] **Step 2: Write the failing builder tests**

Create `tests/SbConsole.Plugins.RabbitMq.Tests/Policies/PolicyDefinitionBuilderTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Policies;

namespace SbConsole.Plugins.RabbitMq.Tests.Policies;

public class PolicyDefinitionBuilderTests
{
    [Fact]
    public void Typed_fields_become_definition_keys_with_numbers_as_longs_and_blanks_left_out()
    {
        var result = PolicyDefinitionBuilder.Build(
            new PolicyFields { DeadLetterExchange = " billing.retry.dlx ", MessageTtl = "30000", Overflow = "reject-publish", MaxLength = "  " },
            extraJson: null);

        result.IsValid.Should().BeTrue();
        result.Definition.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["dead-letter-exchange"] = "billing.retry.dlx",
            ["message-ttl"] = 30000L,
            ["overflow"] = "reject-publish",
        });
    }

    [Theory]
    [InlineData("-1", "message-ttl must be a whole number of 0 or more")]
    [InlineData("1.5", "message-ttl must be a whole number of 0 or more")]
    [InlineData("ten", "message-ttl must be a whole number of 0 or more")]
    public void A_bad_number_is_an_error(string ttl, string error)
    {
        PolicyDefinitionBuilder.Build(new PolicyFields { MessageTtl = ttl }, null).Errors.Should().Contain(error);
    }

    [Fact]
    public void Extra_json_keys_are_merged_with_their_json_types()
    {
        var result = PolicyDefinitionBuilder.Build(new PolicyFields(), """{"ha-mode":"exactly","ha-params":2,"ha-promote-on-shutdown":"always","x":true,"f":1.5}""");

        result.IsValid.Should().BeTrue();
        result.Definition["ha-mode"].Should().Be("exactly");
        result.Definition["ha-params"].Should().Be(2L);
        result.Definition["x"].Should().Be(true);
        result.Definition["f"].Should().Be(1.5);
    }

    [Theory]
    [InlineData("[1,2]", "Extra keys must be a JSON object")]
    [InlineData("{not json", "Extra keys aren't valid JSON")]
    public void Bad_extra_json_is_an_error(string json, string error)
    {
        PolicyDefinitionBuilder.Build(new PolicyFields { MessageTtl = "1" }, json).Errors.Should().ContainSingle(e => e.StartsWith(error));
    }

    [Fact]
    public void An_extra_key_that_duplicates_a_typed_field_is_an_error()
    {
        PolicyDefinitionBuilder.Build(new PolicyFields { MessageTtl = "1000" }, """{"message-ttl":5}""")
            .Errors.Should().Contain("message-ttl is set above — remove it from the extra keys");
    }

    [Fact]
    public void An_empty_definition_is_an_error()
    {
        PolicyDefinitionBuilder.Build(new PolicyFields(), "  ").Errors.Should().Equal("A policy with no keys does nothing");
    }

    [Fact]
    public void Split_then_build_round_trips_every_key()
    {
        var definition = new Dictionary<string, object?>
        {
            ["message-ttl"] = 604800000L,
            ["dead-letter-exchange"] = "archive",
            ["queue-mode"] = "lazy",
            ["ha-mode"] = "all",
            ["federation-upstream-set"] = "all",
        };

        var (fields, extra) = PolicyDefinitionBuilder.Split(definition);

        fields.MessageTtl.Should().Be("604800000");
        fields.DeadLetterExchange.Should().Be("archive");
        fields.QueueMode.Should().Be("lazy");
        extra.Should().Contain("\"ha-mode\"").And.Contain("\"federation-upstream-set\"").And.NotContain("message-ttl");
        PolicyDefinitionBuilder.Build(fields, extra).Definition.Should().BeEquivalentTo(definition);
    }

    [Fact]
    public void Split_keeps_a_typed_key_with_an_unexpected_value_in_the_extra_json()
    {
        var (fields, extra) = PolicyDefinitionBuilder.Split(new Dictionary<string, object?> { ["message-ttl"] = "soon" });

        fields.MessageTtl.Should().BeNull();
        extra.Should().Contain("\"message-ttl\": \"soon\"");
    }

    [Theory]
    [InlineData("^orders\\.", null)]
    [InlineData("", "Pattern is required")]
    [InlineData("(unclosed", "Pattern isn't a valid regular expression")]
    public void ValidatePattern(string pattern, string? errorStart)
    {
        var error = PolicyDefinitionBuilder.ValidatePattern(pattern);
        if (errorStart is null) error.Should().BeNull();
        else error.Should().StartWith(errorStart);
    }
}
```

- [ ] **Step 3: Write the failing handler tests**

In `CommandHandlerTests.cs`, extend `SimpleCommands` with `"policy.create", "policy.update", "policy.delete"`, add arms to `Case`:

```csharp
        "policy.create" => (() => new PutPolicyCommandHandler(Operations, Connections, Audit, Log<PutPolicyCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, new PolicyInfo("dlq-ttl", "-dlq$", "queues", 5, new Dictionary<string, object?> { ["message-ttl"] = 1L }), IsNew: true)),
            "rabbitmq.policy.create", "rabbit-uk-prod//orders/dlq-ttl", ActionRisk.Mutating, "pattern -dlq$ · apply-to queues · priority 5"),
        "policy.update" => (() => new PutPolicyCommandHandler(Operations, Connections, Audit, Log<PutPolicyCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, new PolicyInfo("dlq-ttl", "-dlq$", "queues", 5, new Dictionary<string, object?> { ["message-ttl"] = 1L }), IsNew: false)),
            "rabbitmq.policy.update", "rabbit-uk-prod//orders/dlq-ttl", ActionRisk.Mutating, "pattern -dlq$ · apply-to queues · priority 5"),
        "policy.delete" => (() => new DeletePolicyCommandHandler(Operations, Connections, Audit, Log<DeletePolicyCommandHandler>())
                .HandleAsync(new(ConnectionId, Conn, Vhost, "dlq-ttl")),
            "rabbitmq.policy.delete", "rabbit-uk-prod//orders/dlq-ttl", ActionRisk.Destructive, null),
```

and in `ThrowOnEveryWrite` add:

```csharp
        Operations.PutPolicyAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
        Operations.DeletePolicyAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(ex);
```

Add `using SbConsole.Plugins.RabbitMq.Policies;` at the top.

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~Polic|FullyQualifiedName~CommandHandlerTests|FullyQualifiedName~ManagementApiClientWriteTests"`
Expected: build FAILS.

- [ ] **Step 5: Implement the client and operations**

`ManagementApiClient.cs`, after `RestartShovelAsync`:

```csharp
    /// <summary>Creates or replaces a policy (RabbitMQ has no partial update; the whole policy is sent).</summary>
    public Task PutPolicyAsync(RabbitConnectionSettings settings, string vhost, PolicyInfo policy, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["pattern"] = policy.Pattern,
            ["apply-to"] = policy.ApplyTo,
            ["priority"] = policy.Priority,
            ["definition"] = policy.Definition,
        };
        return SendAsync(settings, HttpMethod.Put, $"api/policies/{Seg(vhost)}/{Seg(policy.Name)}", body, ct);
    }

    public Task DeletePolicyAsync(RabbitConnectionSettings settings, string vhost, string name, CancellationToken ct = default) =>
        SendAsync(settings, HttpMethod.Delete, $"api/policies/{Seg(vhost)}/{Seg(name)}", null, ct);
```

`IRabbitOperations.cs`, after `RestartShovelAsync`:

```csharp
    Task PutPolicyAsync(string secret, string vhost, PolicyInfo policy, CancellationToken ct = default);
    Task DeletePolicyAsync(string secret, string vhost, string name, CancellationToken ct = default);
```

`RabbitOperations.cs`, after `RestartShovelAsync`:

```csharp
    public async Task PutPolicyAsync(string secret, string vhost, PolicyInfo policy, CancellationToken ct = default) =>
        await _management.PutPolicyAsync(Settings(secret), vhost, policy, ct);

    public async Task DeletePolicyAsync(string secret, string vhost, string name, CancellationToken ct = default) =>
        await _management.DeletePolicyAsync(Settings(secret), vhost, name, ct);
```

- [ ] **Step 6: Implement `PolicyDefinitionBuilder`**

`Policies/PolicyDefinitionBuilder.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbConsole.Plugins.RabbitMq.Policies;

/// <summary>The policy dialog's typed inputs, as the raw text the user typed (blank = key absent).</summary>
public sealed record PolicyFields
{
    public string? DeadLetterExchange { get; init; }
    public string? DeadLetterRoutingKey { get; init; }
    public string? MessageTtl { get; init; }
    public string? Expires { get; init; }
    public string? MaxLength { get; init; }
    public string? MaxLengthBytes { get; init; }
    public string? Overflow { get; init; }
    public string? QueueMode { get; init; }
    public string? DeliveryLimit { get; init; }
    public string? AlternateExchange { get; init; }
}

public sealed record PolicyDefinitionResult(IReadOnlyDictionary<string, object?> Definition, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Typed policy fields + an "extra keys" JSON object ⇄ the definition dictionary the management
/// API takes (design spec §3). Pure. Numbers are sent as JSON integers (long); an extra key keeps
/// its JSON type. Split is Build's inverse: known keys with the expected value type go to the typed
/// fields, everything else (including a known key holding an unexpected value) to the JSON, so an
/// edit never silently drops a key.
/// </summary>
public static class PolicyDefinitionBuilder
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly (string Key, Func<PolicyFields, string?> Get, bool Numeric)[] Typed =
    [
        ("dead-letter-exchange", f => f.DeadLetterExchange, false),
        ("dead-letter-routing-key", f => f.DeadLetterRoutingKey, false),
        ("message-ttl", f => f.MessageTtl, true),
        ("expires", f => f.Expires, true),
        ("max-length", f => f.MaxLength, true),
        ("max-length-bytes", f => f.MaxLengthBytes, true),
        ("overflow", f => f.Overflow, false),
        ("queue-mode", f => f.QueueMode, false),
        ("delivery-limit", f => f.DeliveryLimit, true),
        ("alternate-exchange", f => f.AlternateExchange, false),
    ];

    public static PolicyDefinitionResult Build(PolicyFields fields, string? extraJson)
    {
        var definition = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var (key, get, numeric) in Typed)
        {
            var text = get(fields)?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            if (!numeric)
            {
                definition[key] = text;
            }
            else if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                definition[key] = number;
            }
            else
            {
                errors.Add($"{key} must be a whole number of 0 or more");
            }
        }

        if (!string.IsNullOrWhiteSpace(extraJson))
        {
            try
            {
                using var document = JsonDocument.Parse(extraJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Extra keys must be a JSON object");
                }
                else
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (definition.ContainsKey(property.Name))
                        {
                            errors.Add($"{property.Name} is set above — remove it from the extra keys");
                        }
                        else
                        {
                            definition[property.Name] = ToClr(property.Value);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"Extra keys aren't valid JSON: {ex.Message}");
            }
        }

        if (definition.Count == 0 && errors.Count == 0)
        {
            errors.Add("A policy with no keys does nothing");
        }

        return new PolicyDefinitionResult(definition, errors);
    }

    public static (PolicyFields Fields, string ExtraJson) Split(IReadOnlyDictionary<string, object?> definition)
    {
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in definition)
        {
            var spec = Typed.FirstOrDefault(t => t.Key == key);
            var text = spec.Key is null ? null : spec.Numeric ? AsWholeNumber(value) : value as string;
            if (text is null)
            {
                extra[key] = value;
            }
            else
            {
                typed[key] = text;
            }
        }

        var fields = new PolicyFields
        {
            DeadLetterExchange = typed.GetValueOrDefault("dead-letter-exchange"),
            DeadLetterRoutingKey = typed.GetValueOrDefault("dead-letter-routing-key"),
            MessageTtl = typed.GetValueOrDefault("message-ttl"),
            Expires = typed.GetValueOrDefault("expires"),
            MaxLength = typed.GetValueOrDefault("max-length"),
            MaxLengthBytes = typed.GetValueOrDefault("max-length-bytes"),
            Overflow = typed.GetValueOrDefault("overflow"),
            QueueMode = typed.GetValueOrDefault("queue-mode"),
            DeliveryLimit = typed.GetValueOrDefault("delivery-limit"),
            AlternateExchange = typed.GetValueOrDefault("alternate-exchange"),
        };

        return (fields, extra.Count == 0 ? "" : JsonSerializer.Serialize(extra, Indented));
    }

    /// <summary>Null when the pattern is usable (compiled like <see cref="Routing.PolicyMatcher"/> does); otherwise why not.</summary>
    public static string? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return "Pattern is required";
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Pattern isn't a valid regular expression: {ex.Message}";
        }
    }

    private static string? AsWholeNumber(object? value) => value switch
    {
        long l when l >= 0 => l.ToString(CultureInfo.InvariantCulture),
        int i when i >= 0 => i.ToString(CultureInfo.InvariantCulture),
        double d when d >= 0 && d == Math.Floor(d) && d <= long.MaxValue => ((long)d).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value), StringComparer.Ordinal),
        _ => null,
    };
}
```

- [ ] **Step 7: Implement the handlers and register them**

`Policies/PutPolicyCommandHandler.cs`:

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Policies;

/// <summary>IsNew only picks the audit verb: the broker's PUT creates or replaces either way.</summary>
public sealed record PutPolicyCommand(Guid ConnectionId, string ConnectionName, string Vhost, PolicyInfo Policy, bool IsNew);

public sealed class PutPolicyCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PutPolicyCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(PutPolicyCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            cmd.IsNew ? "rabbitmq.policy.create" : "rabbitmq.policy.update",
            HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Policy.Name), ActionRisk.Mutating,
            $"pattern {cmd.Policy.Pattern} · apply-to {cmd.Policy.ApplyTo} · priority {cmd.Policy.Priority.ToString(CultureInfo.InvariantCulture)}",
            secret => operations.PutPolicyAsync(secret, cmd.Vhost, cmd.Policy, ct), ct);
}
```

`Policies/DeletePolicyCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Policies;

public sealed record DeletePolicyCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name);

public sealed class DeletePolicyCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeletePolicyCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeletePolicyCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.policy.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Name), ActionRisk.Destructive, null,
            secret => operations.DeletePolicyAsync(secret, cmd.Vhost, cmd.Name, ct), ct);
}
```

In `RabbitMqPlugin.ConfigureServices`, after `services.AddScoped<Policies.ListPoliciesQueryHandler>();`:

```csharp
        services.AddScoped<Policies.PutPolicyCommandHandler>();
        services.AddScoped<Policies.DeletePolicyCommandHandler>();
```

In `RabbitPageTestBase` constructor, after the `ListPoliciesQueryHandler` line:

```csharp
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.Policies.PutPolicyCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.Policies.DeletePolicyCommandHandler>();
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Client src/Plugins/SbConsole.Plugins.RabbitMq/Policies src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs tests/SbConsole.Plugins.RabbitMq.Tests/Client/ManagementApiClientWriteTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Policies tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/CommandHandlerTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Components/RabbitPageTestBase.cs
git commit -m "feat(rabbitmq): policy put/delete operations, definition builder and handlers

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Policy dialog and New / Edit / Delete on the Policies tab

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/PolicyDialog.razor`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ShovelsPolicies.razor`
- Test: create `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/PolicyDialogTests.cs`; modify `Pages/ShovelsPoliciesPageTests.cs`

**Interfaces:**
- Consumes: `PolicyDefinitionBuilder`, `PolicyFields`, `PutPolicyCommand(Handler)`, `DeletePolicyCommand(Handler)` (Task 5); `PolicyMatcher.CountMatches`.
- Produces: `PolicyDialog` parameters `ConnectionId`, `ConnectionName`, `Vhost`, `Existing` (`PolicyInfo?`), `Queues`, `Exchanges`; CSS hooks `.policy-name`, `.policy-pattern`, `.policy-apply-to` (`MudSelect<string>`), `.policy-priority`, `.policy-dlx`, `.policy-dlrk`, `.policy-ttl`, `.policy-expires`, `.policy-max-length`, `.policy-max-length-bytes`, `.policy-overflow` (`MudSelect<string>`), `.policy-queue-mode` (`MudSelect<string>`), `.policy-delivery-limit`, `.policy-alternate-exchange`, `.policy-extra`, `.policy-matches`, `.policy-edit-warning`, `.policy-errors`, `button.policy-save`. On the page: `button.new-policy`, `button.edit-policy`, `button.delete-policy`.

- [ ] **Step 1: Write the failing dialog tests**

Create `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/PolicyDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class PolicyDialogTests : RabbitPageTestBase
{
    private static readonly List<QueueSummary> Queues =
    [
        SeededTopology.Queue("payments-dlq"),
        SeededTopology.Queue("orders-dlq"),
        SeededTopology.Queue("order-events.q"),
    ];

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync(PolicyInfo? existing = null)
    {
        var cut = RenderDialog<PolicyDialog>(new Dictionary<string, object?>
        {
            [nameof(PolicyDialog.ConnectionId)] = Dev.Id,
            [nameof(PolicyDialog.ConnectionName)] = Dev.Name,
            [nameof(PolicyDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(PolicyDialog.Existing)] = existing,
            [nameof(PolicyDialog.Queues)] = Queues,
            [nameof(PolicyDialog.Exchanges)] = SeededTopology.Exchanges(),
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task TypeAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, string value) =>
        cut.Find($".{cssClass} input, .{cssClass} textarea").InputAsync(new ChangeEventArgs { Value = value });

    private static Task SelectAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, string value)
    {
        var select = cut.FindComponents<MudSelect<string>>().Single(s => (s.Instance.Class ?? "").Split(' ').Contains(cssClass));
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    [Fact]
    public async Task Creating_puts_the_typed_and_extra_keys_and_closes()
    {
        var cut = await RenderAsync();
        await TypeAsync(cut, "policy-name", "dlq-ttl");
        await TypeAsync(cut, "policy-pattern", "-dlq$");
        await SelectAsync(cut, "policy-apply-to", "queues");
        await TypeAsync(cut, "policy-priority", "5");
        await TypeAsync(cut, "policy-ttl", "604800000");
        await TypeAsync(cut, "policy-extra", """{"ha-mode":"all"}""");

        await cut.Find("button.policy-save").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PutPolicyAsync(DevSecret, SeededTopology.Vhost,
            Arg.Is<PolicyInfo>(p => p.Name == "dlq-ttl" && p.Pattern == "-dlq$" && p.ApplyTo == "queues" && p.Priority == 5
                && (long)p.Definition["message-ttl"]! == 604800000L && (string)p.Definition["ha-mode"]! == "all"),
            Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.policy.create", Arg.Any<string>(), SbConsole.Sdk.ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Any<DialogResult>());
    }

    [Fact]
    public async Task The_preview_counts_matching_queues_and_exchanges_separately()
    {
        var cut = await RenderAsync();
        await TypeAsync(cut, "policy-pattern", "-dlq$");
        await SelectAsync(cut, "policy-apply-to", "queues");

        cut.Find(".policy-matches").TextContent.Should().Contain("Matches 2 queues · 0 exchanges");
    }

    [Fact]
    public async Task Validation_errors_block_save()
    {
        var cut = await RenderAsync();
        await TypeAsync(cut, "policy-name", "bad");
        await TypeAsync(cut, "policy-pattern", "(unclosed");
        await TypeAsync(cut, "policy-ttl", "-5");

        cut.Find("button.policy-save").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".policy-errors").TextContent.Should().Contain("Pattern isn't a valid regular expression")
            .And.Contain("message-ttl must be a whole number of 0 or more");
    }

    [Fact]
    public async Task Editing_prefills_typed_and_extra_keys_locks_the_name_warns_and_audits_an_update()
    {
        var existing = new PolicyInfo("dlq-ttl", "-dlq$", "queues", 5,
            new Dictionary<string, object?> { ["message-ttl"] = 604800000L, ["ha-mode"] = "all" });
        var cut = await RenderAsync(existing);

        cut.Find(".policy-name input").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".policy-ttl input").GetAttribute("value").Should().Be("604800000");
        cut.Find(".policy-extra textarea").GetAttribute("value").Should().Contain("\"ha-mode\"");
        cut.Find(".policy-edit-warning").TextContent.Should().Contain("Applies to 2 queues now");

        await cut.Find("button.policy-save").ClickAsync(new());
        await SettleAsync(cut);

        await Audit.Received(1).RecordAsync("rabbitmq.policy.update", Arg.Any<string>(), SbConsole.Sdk.ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_broker_rejection_is_shown_and_the_dialog_stays_open()
    {
        Operations.PutPolicyAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new ManagementApiException(400, "PUT", "/api/policies/%2Forders/x", "Validation failed"));
        var cut = await RenderAsync();
        await TypeAsync(cut, "policy-name", "x");
        await TypeAsync(cut, "policy-pattern", ".*");
        await TypeAsync(cut, "policy-ttl", "1");

        await cut.Find("button.policy-save").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".policy-save-error").TextContent.Should().Contain("Validation failed");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default!);
    }
}
```

In `ShovelsPoliciesPageTests.cs` add:

```csharp
    [Fact]
    public async Task Delete_policy_confirms_with_the_server_side_prod_flag_and_deletes()
    {
        Seed();
        Confirmation.ConfirmAsync("Delete policy", "dlq-ttl", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync("tab=policies");

        var row = cut.FindAll(".policy-row").Single(r => r.QuerySelector(".policy-name")!.TextContent == "dlq-ttl");
        await row.QuerySelector("button.delete-policy")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).DeletePolicyAsync(DevSecret, Vhost, "dlq-ttl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_and_edit_open_the_policy_dialog()
    {
        Seed();
        var dialogs = Substitute.For<MudBlazor.IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync("tab=policies");

        await cut.Find("button.new-policy").ClickAsync(new());
        var row = cut.FindAll(".policy-row").Single(r => r.QuerySelector(".policy-name")!.TextContent == "orders-limits");
        await row.QuerySelector("button.edit-policy")!.ClickAsync(new());

        await dialogs.Received(1).ShowAsync<SbConsole.Plugins.RabbitMq.Pages.PolicyDialog>(
            "New policy", Arg.Is<MudBlazor.DialogParameters>(p => p[nameof(SbConsole.Plugins.RabbitMq.Pages.PolicyDialog.Existing)] == null),
            Arg.Any<MudBlazor.DialogOptions>());
        await dialogs.Received(1).ShowAsync<SbConsole.Plugins.RabbitMq.Pages.PolicyDialog>(
            "Edit policy — orders-limits",
            Arg.Is<MudBlazor.DialogParameters>(p => ((PolicyInfo)p[nameof(SbConsole.Plugins.RabbitMq.Pages.PolicyDialog.Existing)]!).Name == "orders-limits"),
            Arg.Any<MudBlazor.DialogOptions>());
    }
```

(`Vhost` is the constant this test class already uses; if it's named differently there, use that name.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~PolicyDialogTests|FullyQualifiedName~ShovelsPoliciesPageTests"`
Expected: build FAILS — `PolicyDialog` not found.

- [ ] **Step 3: Implement `PolicyDialog.razor`**

```razor
@using System.Globalization
@using SbConsole.Plugins.RabbitMq.Client
@using SbConsole.Plugins.RabbitMq.Policies
@using SbConsole.Plugins.RabbitMq.Routing
@inject PutPolicyCommandHandler PutHandler

<MudDialog>
    <DialogContent>
        <div class="d-flex gap-3">
            <MudTextField T="string" Class="policy-name" @bind-Value="_name" Label="Name" Immediate="true" Disabled="@(Existing is not null)" />
            <MudTextField T="string" Class="policy-priority" @bind-Value="_priority" Label="Priority" Immediate="true" Style="max-width:120px" />
        </div>
        <div class="d-flex gap-3 mt-2">
            <MudTextField T="string" Class="policy-pattern" @bind-Value="_pattern" Label="Pattern (regex)" Immediate="true" Style="font-family:monospace" />
            <MudSelect T="string" Class="policy-apply-to" @bind-Value="_applyTo" Label="Apply to" Style="max-width:200px">
                @foreach (var applyTo in ApplyToChoices)
                {
                    <MudSelectItem Value="@applyTo">@applyTo</MudSelectItem>
                }
            </MudSelect>
        </div>
        <MudText Class="policy-matches mud-text-secondary d-block mt-1" Typo="Typo.caption">@MatchesText</MudText>
        @if (EditWarning is { } warning)
        {
            <MudAlert Class="policy-edit-warning mt-2" Severity="Severity.Warning" Dense="true">@warning</MudAlert>
        }

        <MudText Typo="Typo.overline" Class="mud-text-secondary d-block mt-3">Definition</MudText>
        <div class="d-flex flex-wrap gap-3">
            <MudTextField T="string" Class="policy-dlx" @bind-Value="_dlx" Label="dead-letter-exchange" Immediate="true" Style="min-width:220px" />
            <MudTextField T="string" Class="policy-dlrk" @bind-Value="_dlrk" Label="dead-letter-routing-key" Immediate="true" Style="min-width:220px" />
            <MudTextField T="string" Class="policy-ttl" @bind-Value="_ttl" Label="message-ttl (ms)" Immediate="true" />
            <MudTextField T="string" Class="policy-expires" @bind-Value="_expires" Label="expires (ms)" Immediate="true" />
            <MudTextField T="string" Class="policy-max-length" @bind-Value="_maxLength" Label="max-length" Immediate="true" />
            <MudTextField T="string" Class="policy-max-length-bytes" @bind-Value="_maxLengthBytes" Label="max-length-bytes" Immediate="true" />
            <MudSelect T="string" Class="policy-overflow" @bind-Value="_overflow" Label="overflow" Clearable="true" Style="min-width:200px">
                <MudSelectItem Value="@("drop-head")">drop-head</MudSelectItem>
                <MudSelectItem Value="@("reject-publish")">reject-publish</MudSelectItem>
                <MudSelectItem Value="@("reject-publish-dlx")">reject-publish-dlx</MudSelectItem>
            </MudSelect>
            <MudSelect T="string" Class="policy-queue-mode" @bind-Value="_queueMode" Label="queue-mode" Clearable="true" Style="min-width:160px">
                <MudSelectItem Value="@("default")">default</MudSelectItem>
                <MudSelectItem Value="@("lazy")">lazy</MudSelectItem>
            </MudSelect>
            <MudTextField T="string" Class="policy-delivery-limit" @bind-Value="_deliveryLimit" Label="delivery-limit" Immediate="true" />
            <MudTextField T="string" Class="policy-alternate-exchange" @bind-Value="_alternateExchange" Label="alternate-exchange" Immediate="true" Style="min-width:220px" />
        </div>
        <MudTextField T="string" Class="policy-extra mt-3" @bind-Value="_extra" Label="Extra keys (JSON object)" Lines="4" Immediate="true"
                      HelperText="Anything else, e.g. {&quot;ha-mode&quot;: &quot;all&quot;}" Style="font-family:monospace" />

        @if (Errors.Count > 0 && Touched)
        {
            <div class="policy-errors mt-2">
                @foreach (var error in Errors)
                {
                    <MudText Typo="Typo.caption" Class="mud-error-text d-block">@error</MudText>
                }
            </div>
        }

        @if (_saveError is not null)
        {
            <MudAlert Class="policy-save-error mt-3" Severity="Severity.Error" Dense="true">@_saveError</MudAlert>
        }
    </DialogContent>
    <DialogActions>
        <MudText Typo="Typo.caption" Class="mud-text-secondary mr-auto ml-4">Logged to audit · takes effect immediately</MudText>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Class="policy-save" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(Errors.Count > 0 || _busy)" OnClick="SaveAsync">
            @(Existing is null ? "Create" : "Save")
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    private static readonly string[] ApplyToChoices = ["all", "queues", "exchanges", "classic_queues", "quorum_queues", "streams"];
    private static readonly IReadOnlyDictionary<string, object?> NoDefinition = new Dictionary<string, object?>();

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string Vhost { get; set; } = "/";
    /// <summary>Null = create a new policy.</summary>
    [Parameter] public PolicyInfo? Existing { get; set; }
    [Parameter] public IReadOnlyList<QueueSummary> Queues { get; set; } = [];
    [Parameter] public IReadOnlyList<ExchangeSummary> Exchanges { get; set; } = [];

    private string? _name;
    private string? _pattern;
    private string _applyTo = "all";
    private string? _priority = "0";
    private string? _dlx, _dlrk, _ttl, _expires, _maxLength, _maxLengthBytes, _overflow, _queueMode, _deliveryLimit, _alternateExchange;
    private string? _extra;
    private string? _saveError;
    private bool _busy;

    // Errors show once the user has typed something, so an empty create form isn't a wall of red.
    private bool Touched => Existing is not null || !string.IsNullOrEmpty(_name) || !string.IsNullOrEmpty(_pattern);

    protected override void OnInitialized()
    {
        if (Existing is not { } p) return;
        _name = p.Name;
        _pattern = p.Pattern;
        _applyTo = p.ApplyTo;
        _priority = p.Priority.ToString(CultureInfo.InvariantCulture);
        var (fields, extra) = PolicyDefinitionBuilder.Split(p.Definition);
        _dlx = fields.DeadLetterExchange;
        _dlrk = fields.DeadLetterRoutingKey;
        _ttl = fields.MessageTtl;
        _expires = fields.Expires;
        _maxLength = fields.MaxLength;
        _maxLengthBytes = fields.MaxLengthBytes;
        _overflow = fields.Overflow;
        _queueMode = fields.QueueMode;
        _deliveryLimit = fields.DeliveryLimit;
        _alternateExchange = fields.AlternateExchange;
        _extra = extra;
    }

    private PolicyFields Fields => new()
    {
        DeadLetterExchange = _dlx, DeadLetterRoutingKey = _dlrk, MessageTtl = _ttl, Expires = _expires,
        MaxLength = _maxLength, MaxLengthBytes = _maxLengthBytes, Overflow = _overflow, QueueMode = _queueMode,
        DeliveryLimit = _deliveryLimit, AlternateExchange = _alternateExchange,
    };

    private PolicyDefinitionResult Definition => PolicyDefinitionBuilder.Build(Fields, _extra);

    private IReadOnlyList<string> Errors
    {
        get
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(_name)) errors.Add("Name is required");
            if (PolicyDefinitionBuilder.ValidatePattern(_pattern) is { } patternError) errors.Add(patternError);
            if (!int.TryParse(_priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) errors.Add("Priority must be a whole number");
            errors.AddRange(Definition.Errors);
            return errors;
        }
    }

    private (int? Queues, int? Exchanges) Matches(string? pattern, string applyTo)
    {
        if (string.IsNullOrEmpty(pattern)) return (0, 0);
        PolicyInfo Probe(string scope) => new("", pattern, scope, 0, NoDefinition);
        int? queues = applyTo == "exchanges" ? 0 : PolicyMatcher.CountMatches(Probe(applyTo == "all" ? "queues" : applyTo), Queues, []);
        int? exchanges = applyTo is "exchanges" or "all" ? PolicyMatcher.CountMatches(Probe("exchanges"), [], Exchanges) : 0;
        return (queues, exchanges);
    }

    private string MatchesText
    {
        get
        {
            var (queues, exchanges) = Matches(_pattern, _applyTo);
            return queues is null || exchanges is null
                ? "Matches ? — the pattern isn't a valid .NET regex or timed out"
                : $"Matches {Plural(queues.Value, "queue")} · {Plural(exchanges.Value, "exchange")}";
        }
    }

    private string? EditWarning
    {
        get
        {
            if (Existing is not { } p) return null;
            var (queues, exchanges) = Matches(p.Pattern, p.ApplyTo);
            if ((queues ?? 0) + (exchanges ?? 0) == 0) return null;
            return $"Applies to {Plural(queues ?? 0, "queue")} now. Changes take effect immediately and silently override queue arguments.";
        }
    }

    private static string Plural(int n, string noun) => $"{n.ToString(CultureInfo.InvariantCulture)} {noun}{(n == 1 ? "" : "s")}";

    private async Task SaveAsync()
    {
        if (Errors.Count > 0) return;

        var policy = new PolicyInfo(_name!.Trim(), _pattern!, _applyTo,
            int.Parse(_priority!, NumberStyles.Integer, CultureInfo.InvariantCulture), Definition.Definition);
        _busy = true;
        _saveError = null;
        try
        {
            var result = await PutHandler.HandleAsync(new PutPolicyCommand(ConnectionId, ConnectionName, Vhost, policy, IsNew: Existing is null));
            if (result.IsSuccess)
            {
                MudDialog.Close(DialogResult.Ok(true));
            }
            else
            {
                _saveError = result.Error;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Wire the Policies tab**

In `ShovelsPolicies.razor`:

1. Add injections at the top:
   ```razor
   @inject DeletePolicyCommandHandler DeletePolicyHandler
   ```
2. Replace the heading line `<MudText Class="policies-heading my-2" …>Policies — …</MudText>` with:
   ```razor
                        <div class="d-flex align-center my-2">
                            <MudText Class="policies-heading" Typo="Typo.subtitle1">Policies — @policies.Policies.Count, applied by pattern</MudText>
                            <MudSpacer />
                            <MudButton Class="new-policy" Variant="Variant.Filled" Color="Color.Primary" Size="Size.Small"
                                       StartIcon="@Icons.Material.Filled.Add" OnClick="@(() => OpenPolicyAsync(null))">New policy</MudButton>
                        </div>
   ```
   Move the `new-policy` button *outside* the `@if (policies.Policies.Count == 0)` branch so it shows in the empty state too (it already is, as written above the `@if`).
3. Add a header cell `<th></th>` at the end of the policies `<thead>` row, and at the end of each `policy-row`:
   ```razor
                                            <td style="white-space:nowrap">
                                                <MudButton Class="edit-policy" Size="Size.Small" Variant="Variant.Text" Disabled="@(_busyPolicy is not null)"
                                                           OnClick="@(() => OpenPolicyAsync(policy))">Edit</MudButton>
                                                <MudButton Class="delete-policy" Size="Size.Small" Variant="Variant.Text" Color="Color.Error"
                                                           Disabled="@(_busyPolicy is not null)" OnClick="@(() => DeletePolicyAsync(policy.Name))">Delete</MudButton>
                                            </td>
   ```
4. In `@code`, add:
   ```csharp
    private string? _busyPolicy;

    private async Task OpenPolicyAsync(PolicyInfo? existing)
    {
        var selection = _selection!;
        var snapshot = _policies.Snapshot;
        var parameters = new DialogParameters<PolicyDialog>
        {
            { x => x.ConnectionId, selection.Connection.Id },
            { x => x.ConnectionName, selection.Connection.Name },
            { x => x.Vhost, selection.Vhost },
            { x => x.Existing, existing },
            { x => x.Queues, snapshot?.Queues ?? [] },
            { x => x.Exchanges, snapshot?.Exchanges ?? [] },
        };
        var title = existing is null ? "New policy" : $"Edit policy — {existing.Name}";
        var dialog = await DialogService.ShowAsync<PolicyDialog>(title, parameters, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task DeletePolicyAsync(string name)
    {
        var selection = _selection!;
        // IsProd comes from the server-side connection lookup the picker made, never from the client.
        if (!await Confirmation.ConfirmAsync("Delete policy", name, selection.Connection.IsProd))
        {
            return;
        }

        _busyPolicy = name;
        try
        {
            var result = await DeletePolicyHandler.HandleAsync(new DeletePolicyCommand(selection.Connection.Id, selection.Connection.Name, selection.Vhost, name));
            Snackbar.Add(result.IsSuccess ? $"Deleted policy {name}" : result.Error!, result.IsSuccess ? Severity.Success : Severity.Error);
        }
        finally
        {
            _busyPolicy = null;
        }

        await LoadAsync();
    }
   ```
   With a substituted `IDialogService`, `dialog.Result` returns a substitute `DialogResult` whose `Canceled` is false, so `LoadAsync` re-runs — harmless.
5. Replace the caption text with: `A policy overrides queue arguments silently — queue detail names the winning policy beside the arguments. Policy changes are audited.` Then update the existing test that asserts `.policies-caption` (around line 292) to the new text.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Pages/PolicyDialog.razor src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ShovelsPolicies.razor tests/SbConsole.Plugins.RabbitMq.Tests/Pages/PolicyDialogTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ShovelsPoliciesPageTests.cs
git commit -m "feat(rabbitmq): create, edit and delete policies from the Policies tab

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Client connections and channels — operations, fixtures, handlers

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/ClientConnectionInfo.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/ManagementApiClient.cs` (reads, close, request headers, 2 DTOs)
- Modify: `Client/IRabbitOperations.cs`, `Client/RabbitOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/ClientConnections/ListClientConnectionsQueryHandler.cs`, `ClientConnections/CloseClientConnectionCommandHandler.cs`
- Modify: `RabbitMqPlugin.cs` (DI), `tests/.../Components/RabbitPageTestBase.cs` (DI)
- Create fixtures: `tests/SbConsole.Plugins.RabbitMq.Tests/Client/Fixtures/connections__2Forders.json`, `channels__2Forders.json`
- Modify: `tests/SbConsole.Plugins.RabbitMq.Tests/Client/ManagementApiTestSupport.cs` (record request headers)
- Test: `Client/ManagementApiClientReadTests.cs`, `Client/ManagementApiClientWriteTests.cs`, `Handlers/QueryHandlerTests.cs`, `Handlers/CommandHandlerTests.cs`

**Interfaces:**
- Produces:
  - `record ClientConnectionInfo(string Name, string User, string? ClientProvidedName, string? Product, string? ProductVersion, string? Platform, string Protocol, bool IsDirect, bool Tls, string State, int Channels, double? RecvRate, double? SendRate, DateTimeOffset? ConnectedAt, string? PeerHost, int? PeerPort)` with `bool IsThisConsole` and `string DisplayName`
  - `record ChannelInfo(string Name, string ConnectionName, int Number, int Prefetch, long Unacked, int Consumers, bool ConfirmMode, bool Transactional, string State)`
  - `IRabbitOperations.ListClientConnectionsAsync(string secret, string vhost, CancellationToken ct = default) : Task<IReadOnlyList<ClientConnectionInfo>>`
  - `IRabbitOperations.ListChannelsAsync(string secret, string vhost, CancellationToken ct = default) : Task<IReadOnlyList<ChannelInfo>>`
  - `IRabbitOperations.CloseClientConnectionAsync(string secret, string name, string? reason, CancellationToken ct = default)`
  - namespace `SbConsole.Plugins.RabbitMq.ClientConnections`: `record ClientConnectionsSnapshot(IReadOnlyList<ClientConnectionInfo> Connections, IReadOnlyList<ChannelInfo> Channels)`; `ListClientConnectionsQueryHandler.HandleAsync(Guid connectionId, string vhost, CancellationToken) : Task<PluginResult<ClientConnectionsSnapshot>>`; `enum CloseOutcome { Closed, AlreadyClosed }`; `record CloseClientConnectionCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name, string DisplayName, string? Reason)`; `CloseClientConnectionCommandHandler.HandleAsync(CloseClientConnectionCommand, CancellationToken) : Task<PluginResult<CloseOutcome>>`
  - `ClientConnectionInfo.CloseReason = "Closed from SbConsole"` (const; the reason sent with every close — see Task 12 deviation 7)

- [ ] **Step 1: Add the fixtures**

The `Direct` entries are verbatim captures from the local stack's 3.13.7 (`GET /api/vhosts/%2Forders/connections` and `/channels`, 2026-09-26). The network entries follow the same broker's field names for socket connections (`type` `network`, `peer_host`/`peer_port`, `ssl`, `state`, `channels`, `client_properties`, `user_provided_name`, `recv_oct_details`/`send_oct_details`).

`tests/SbConsole.Plugins.RabbitMq.Tests/Client/Fixtures/connections__2Forders.json`:

```json
[
  {
    "client_properties": { "connection_name": "Shovel legacy-migrate" },
    "connected_at": 1790367116078,
    "name": "<rabbit@rabbit-local.1790367115.1012.0>",
    "node": "rabbit@rabbit-local",
    "protocol": "Direct 0-9-1",
    "type": "direct",
    "user": "none",
    "user_who_performed_action": "none",
    "vhost": "/orders"
  },
  {
    "auth_mechanism": "PLAIN",
    "channels": 1,
    "client_properties": {
      "capabilities": { "publisher_confirms": true },
      "connection_name": "sbconsole",
      "platform": ".NET",
      "product": "RabbitMQ",
      "version": "7.2.2"
    },
    "connected_at": 1790370000000,
    "name": "172.18.0.1:53144 -> 172.18.0.3:5672",
    "node": "rabbit@rabbit-local",
    "peer_host": "172.18.0.1",
    "peer_port": 53144,
    "protocol": "AMQP 0-9-1",
    "recv_oct_details": { "rate": 412.6 },
    "send_oct_details": { "rate": 96.0 },
    "ssl": false,
    "state": "running",
    "type": "network",
    "user": "sbconsole",
    "user_provided_name": "sbconsole",
    "vhost": "/orders"
  },
  {
    "channels": 3,
    "client_properties": { "product": "pika", "version": "1.3.2", "platform": "Python 3.12.4" },
    "connected_at": 1790360000000,
    "name": "10.4.2.17:40112 -> 172.18.0.3:5672",
    "peer_host": "10.4.2.17",
    "peer_port": 40112,
    "protocol": "AMQP 0-9-1",
    "ssl": true,
    "state": "blocked",
    "type": "network",
    "user": "billing-worker",
    "vhost": "/orders"
  }
]
```

`tests/SbConsole.Plugins.RabbitMq.Tests/Client/Fixtures/channels__2Forders.json`:

```json
[
  {
    "name": "<rabbit@rabbit-local.1790367115.1012.0> (1)",
    "number": 1,
    "prefetch_count": 0,
    "messages_unacknowledged": 0,
    "consumer_count": 0,
    "confirm": true,
    "transactional": false,
    "state": "running",
    "connection_details": { "name": "<rabbit@rabbit-local.1790367115.1012.0>", "peer_host": "undefined", "peer_port": "undefined" }
  },
  {
    "name": "10.4.2.17:40112 -> 172.18.0.3:5672 (2)",
    "number": 2,
    "prefetch_count": 0,
    "messages_unacknowledged": 118,
    "consumer_count": 1,
    "confirm": false,
    "transactional": false,
    "state": "running",
    "connection_details": { "name": "10.4.2.17:40112 -> 172.18.0.3:5672", "peer_host": "10.4.2.17", "peer_port": 40112 }
  }
]
```

Check the test csproj copies fixtures: it already has a glob for `Client/Fixtures/*.json` (the existing fixtures load through `Fixtures.Read`). If `dotnet test` later can't find the new files, add them to the same `<None Update=… CopyToOutputDirectory=…>` item the existing fixtures use.

- [ ] **Step 2: Record request headers in the fake handler**

In `ManagementApiTestSupport.cs`, extend the record and capture non-standard headers:

```csharp
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Accept, string? Body, string? ContentType)
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}
```

and in `SendAsync`, build it with headers:

```csharp
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType)
            {
                Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            });
```

- [ ] **Step 3: Write the failing client tests**

Append to `ManagementApiClientReadTests.cs` (it already uses `TestSettings.Create()` and `RespondFixture`):

```csharp
    [Fact]
    public async Task ListClientConnections_maps_network_and_direct_connections()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/vhosts/%2Forders/connections", "connections__2Forders.json");

        var connections = await client.ListClientConnectionsAsync(TestSettings.Orders, "/orders");

        connections.Should().HaveCount(3);
        var shovel = connections[0];
        shovel.IsDirect.Should().BeTrue();
        shovel.ClientProvidedName.Should().Be("Shovel legacy-migrate");
        shovel.State.Should().Be("running");
        shovel.PeerHost.Should().BeNull();
        shovel.PeerPort.Should().BeNull();

        var console = connections[1];
        console.IsDirect.Should().BeFalse();
        console.IsThisConsole.Should().BeTrue();
        console.User.Should().Be("sbconsole");
        console.Product.Should().Be("RabbitMQ");
        console.ProductVersion.Should().Be("7.2.2");
        console.Platform.Should().Be(".NET");
        console.Protocol.Should().Be("AMQP 0-9-1");
        console.Tls.Should().BeFalse();
        console.Channels.Should().Be(1);
        console.RecvRate.Should().Be(412.6);
        console.SendRate.Should().Be(96.0);
        console.PeerHost.Should().Be("172.18.0.1");
        console.PeerPort.Should().Be(53144);
        console.ConnectedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790370000000));
        console.DisplayName.Should().Be("172.18.0.1:53144");

        var worker = connections[2];
        worker.ClientProvidedName.Should().BeNull();
        worker.Tls.Should().BeTrue();
        worker.State.Should().Be("blocked");
        worker.RecvRate.Should().BeNull();
    }

    [Fact]
    public async Task ListChannels_maps_prefetch_unacked_and_owning_connection()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/vhosts/%2Forders/channels", "channels__2Forders.json");

        var channels = await client.ListChannelsAsync(TestSettings.Orders, "/orders");

        channels.Should().HaveCount(2);
        channels[1].Should().Be(new ChannelInfo("10.4.2.17:40112 -> 172.18.0.3:5672 (2)", "10.4.2.17:40112 -> 172.18.0.3:5672",
            2, 0, 118, 1, false, false, "running"));
        channels[0].ConfirmMode.Should().BeTrue();
    }
```

Append to `ManagementApiClientWriteTests.cs`:

```csharp
    [Fact]
    public async Task CloseClientConnection_deletes_the_escaped_name_with_an_ascii_reason_header()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/connections/10.4.2.17%3A40112%20-%3E%20172.18.0.3%3A5672", "", HttpStatusCode.NoContent);

        await client.CloseClientConnectionAsync(TestSettings.Orders, "10.4.2.17:40112 -> 172.18.0.3:5672", "Closed from SbConsole — ops");

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Delete);
        request.Headers["X-Reason"].Should().Be("Closed from SbConsole ? ops");
    }

    [Fact]
    public async Task CloseClientConnection_without_a_reason_sends_no_header()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("DELETE", "/api/connections/abc", "", HttpStatusCode.NoContent);

        await client.CloseClientConnectionAsync(TestSettings.Orders, "abc", null);

        handler.Requests.Single().Headers.Should().NotContainKey("X-Reason");
    }
```

(`Uri.EscapeDataString` leaves `-` alone and escapes `:`, space and `>`: `10.4.2.17%3A40112%20-%3E%20172.18.0.3%3A5672`. If the fake's key doesn't match because .NET normalises `%3A` in the path, compare against `handler.Requests.Single().Uri.AbsolutePath` in the test instead and register the route with that exact form.)

- [ ] **Step 4: Write the failing handler tests**

Append to `QueryHandlerTests.cs`:

```csharp
    [Fact]
    public async Task Client_connections_snapshot_reads_connections_and_channels()
    {
        Operations.ListClientConnectionsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            new ClientConnectionInfo("c1", "u", null, null, null, null, "AMQP 0-9-1", false, false, "running", 1, null, null, null, "h", 1)]);
        Operations.ListChannelsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            new ChannelInfo("c1 (1)", "c1", 1, 10, 0, 1, false, false, "running")]);

        var result = await new SbConsole.Plugins.RabbitMq.ClientConnections.ListClientConnectionsQueryHandler(
            Operations, Connections, Log<SbConsole.Plugins.RabbitMq.ClientConnections.ListClientConnectionsQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.Value!.Connections.Should().ContainSingle();
        result.Value.Channels.Should().ContainSingle();
    }
```

Append to `CommandHandlerTests.cs`:

```csharp
    private SbConsole.Plugins.RabbitMq.ClientConnections.CloseClientConnectionCommandHandler CloseHandler() =>
        new(Operations, Connections, Audit, Log<SbConsole.Plugins.RabbitMq.ClientConnections.CloseClientConnectionCommandHandler>());

    private SbConsole.Plugins.RabbitMq.ClientConnections.CloseClientConnectionCommand CloseCommand() =>
        new(ConnectionId, Conn, Vhost, "10.4.2.17:40112 -> 172.18.0.3:5672", "10.4.2.17:40112", "Closed from SbConsole");

    [Fact]
    public async Task Close_connection_is_destructive_and_audited_with_the_reason()
    {
        var result = await CloseHandler().HandleAsync(CloseCommand());

        result.Value.Should().Be(SbConsole.Plugins.RabbitMq.ClientConnections.CloseOutcome.Closed);
        await Operations.Received(1).CloseClientConnectionAsync(Secret, "10.4.2.17:40112 -> 172.18.0.3:5672", "Closed from SbConsole", Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.connection.close", "rabbit-uk-prod//orders/10.4.2.17:40112", ActionRisk.Destructive, true, "Closed from SbConsole", CancellationToken.None);
    }

    [Fact]
    public async Task Close_of_an_already_closed_connection_is_a_success()
    {
        Operations.CloseClientConnectionAsync(default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ManagementApiException(404, "DELETE", "/api/connections/x", "Not Found"));

        var result = await CloseHandler().HandleAsync(CloseCommand());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(SbConsole.Plugins.RabbitMq.ClientConnections.CloseOutcome.AlreadyClosed);
    }

    [Fact]
    public async Task Close_refused_with_403_names_the_missing_administrator_tag()
    {
        Operations.CloseClientConnectionAsync(default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new ManagementApiException(403, "DELETE", "/api/connections/x", "Access refused."));

        var result = await CloseHandler().HandleAsync(CloseCommand());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Closing another user's connection needs the administrator tag on u.");
        await Audit.Received(1).RecordAsync("rabbitmq.connection.close", Arg.Any<string>(), ActionRisk.Destructive, false, Arg.Any<string?>(), CancellationToken.None);
    }
```

(`HandlerTestBase.Secret` has `username=u`, hence "on u.")

- [ ] **Step 5: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: build FAILS — new types/methods missing.

- [ ] **Step 6: Implement the records**

`Client/ClientConnectionInfo.cs`:

```csharp
using System.Globalization;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// One client connection to a vhost (GET /api/vhosts/{vhost}/connections). Direct connections are
/// broker-internal (shovel, federation): no socket, so no peer, state, channel count or rates.
/// </summary>
public sealed record ClientConnectionInfo(
    string Name,
    string User,
    string? ClientProvidedName,
    string? Product,
    string? ProductVersion,
    string? Platform,
    string Protocol,
    bool IsDirect,
    bool Tls,
    string State,
    int Channels,
    double? RecvRate,
    double? SendRate,
    DateTimeOffset? ConnectedAt,
    string? PeerHost,
    int? PeerPort)
{
    /// <summary>The reason sent (X-Reason) with every close from the console; the client sees it in its connection.close.</summary>
    public const string CloseReason = "Closed from SbConsole";

    /// <summary>The plugin's own AMQP connections set ClientProvidedName = "sbconsole" (AmqpClient.CreateFactory).</summary>
    public bool IsThisConsole => ClientProvidedName == "sbconsole";

    public string DisplayName => PeerHost is { } host && PeerPort is { } port
        ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)}"
        : ClientProvidedName ?? Name;
}

/// <summary>One channel (GET /api/vhosts/{vhost}/channels). ConnectionName matches <see cref="ClientConnectionInfo.Name"/>.</summary>
public sealed record ChannelInfo(
    string Name,
    string ConnectionName,
    int Number,
    int Prefetch,
    long Unacked,
    int Consumers,
    bool ConfirmMode,
    bool Transactional,
    string State);
```

- [ ] **Step 7: Implement the management client**

1. Give `SendAsync` and `CreateRequest` optional headers. Change the signatures and bodies:

```csharp
    private async Task SendAsync(
        RabbitConnectionSettings settings, HttpMethod method, string relativePath, object? body, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var client = CreateClient(settings);
        using var request = CreateRequest(settings, method, relativePath, body);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, request, ct);
    }
```

(`CreateRequest` is unchanged.)

2. Add, after `ListPoliciesAsync`:

```csharp
    public async Task<IReadOnlyList<ClientConnectionInfo>> ListClientConnectionsAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct = default)
    {
        var connections = await GetAsync<List<ConnectionDto>>(settings, $"api/vhosts/{Seg(vhost)}/connections", ct);
        return connections.Select(c =>
        {
            var direct = c.Type == "direct";
            return new ClientConnectionInfo(
                Name: c.Name ?? "",
                User: c.User ?? "",
                ClientProvidedName: NullIfEmpty(c.UserProvidedName) ?? NullIfEmpty(ClientProperty(c.ClientProperties, "connection_name")),
                Product: NullIfEmpty(ClientProperty(c.ClientProperties, "product")),
                ProductVersion: NullIfEmpty(ClientProperty(c.ClientProperties, "version")),
                Platform: NullIfEmpty(ClientProperty(c.ClientProperties, "platform")),
                Protocol: c.Protocol ?? "",
                IsDirect: direct,
                Tls: c.Ssl,
                // A direct connection has no socket state; it is up for as long as it is listed.
                State: c.State ?? (direct ? "running" : "unknown"),
                Channels: (int)c.Channels,
                RecvRate: c.RecvOctDetails?.Rate,
                SendRate: c.SendOctDetails?.Rate,
                ConnectedAt: c.ConnectedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(c.ConnectedAt) : null,
                PeerHost: Defined(c.PeerHost),
                PeerPort: c.PeerPort > 0 ? (int)c.PeerPort : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct = default)
    {
        var channels = await GetAsync<List<ChannelDto>>(settings, $"api/vhosts/{Seg(vhost)}/channels", ct);
        return channels.Select(c => new ChannelInfo(
            c.Name ?? "",
            c.ConnectionDetails?.Name ?? "",
            (int)c.Number,
            (int)c.PrefetchCount,
            c.MessagesUnacknowledged,
            (int)c.ConsumerCount,
            c.Confirm,
            c.Transactional,
            c.State ?? "unknown")).ToList();
    }
```

3. Add, after `DeletePolicyAsync`:

```csharp
    /// <summary>
    /// Force-closes a client connection. The reason travels in X-Reason and reaches the client in
    /// connection.close; HTTP headers are ASCII, so anything else becomes '?', capped at 200 chars.
    /// </summary>
    public Task CloseClientConnectionAsync(RabbitConnectionSettings settings, string name, string? reason, CancellationToken ct = default)
    {
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var ascii = new string(reason.Trim().Take(200).Select(ch => ch is >= ' ' and <= '~' ? ch : '?').ToArray());
            headers = new Dictionary<string, string> { ["X-Reason"] = ascii };
        }

        return SendAsync(settings, HttpMethod.Delete, $"api/connections/{Seg(name)}", null, ct, headers);
    }
```

4. Add helpers in the mapping section:

```csharp
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // The broker renders missing socket details as the Erlang atom "undefined".
    private static string? Defined(string? value) => value is null or "" or "undefined" ? null : value;

    private static string? ClientProperty(JsonElement? properties, string name) =>
        properties is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
```

5. Add the DTOs next to the others:

```csharp
    private sealed class ConnectionDto
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? User { get; set; }

        public string? UserProvidedName { get; set; }

        public string? Protocol { get; set; }

        public bool Ssl { get; set; }

        public string? State { get; set; }

        public long Channels { get; set; }

        public string? PeerHost { get; set; }

        public long PeerPort { get; set; }

        public long ConnectedAt { get; set; }

        public JsonElement? ClientProperties { get; set; }

        public RateDto? RecvOctDetails { get; set; }

        public RateDto? SendOctDetails { get; set; }
    }

    private sealed class ChannelDto
    {
        public string? Name { get; set; }

        public long Number { get; set; }

        public long PrefetchCount { get; set; }

        public long MessagesUnacknowledged { get; set; }

        public long ConsumerCount { get; set; }

        public bool Confirm { get; set; }

        public bool Transactional { get; set; }

        public string? State { get; set; }

        public ChannelDetailsDto? ConnectionDetails { get; set; }
    }
```

(The lenient converters turn a missing or `"undefined"` number/bool into `0`/`false`, and `ChannelDetailsDto` already has `Name`.)

6. `IRabbitOperations.cs` — add under reads:

```csharp
    Task<IReadOnlyList<ClientConnectionInfo>> ListClientConnectionsAsync(string secret, string vhost, CancellationToken ct = default);
    Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(string secret, string vhost, CancellationToken ct = default);
```

and under writes:

```csharp
    Task CloseClientConnectionAsync(string secret, string name, string? reason, CancellationToken ct = default);
```

`RabbitOperations.cs`:

```csharp
    public async Task<IReadOnlyList<ClientConnectionInfo>> ListClientConnectionsAsync(string secret, string vhost, CancellationToken ct = default) =>
        await _management.ListClientConnectionsAsync(Settings(secret), vhost, ct);

    public async Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(string secret, string vhost, CancellationToken ct = default) =>
        await _management.ListChannelsAsync(Settings(secret), vhost, ct);

    public async Task CloseClientConnectionAsync(string secret, string name, string? reason, CancellationToken ct = default) =>
        await _management.CloseClientConnectionAsync(Settings(secret), name, reason, ct);
```

- [ ] **Step 8: Implement the handlers**

`ClientConnections/ListClientConnectionsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.ClientConnections;

public sealed record ClientConnectionsSnapshot(IReadOnlyList<ClientConnectionInfo> Connections, IReadOnlyList<ChannelInfo> Channels);

public sealed class ListClientConnectionsQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListClientConnectionsQueryHandler> logger)
{
    public Task<PluginResult<ClientConnectionsSnapshot>> HandleAsync(Guid connectionId, string vhost, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Listing client connections", async secret =>
        {
            var clients = operations.ListClientConnectionsAsync(secret, vhost, ct);
            var channels = operations.ListChannelsAsync(secret, vhost, ct);
            await Task.WhenAll(clients, channels);
            return new ClientConnectionsSnapshot(await clients, await channels);
        }, ct);
}
```

`ClientConnections/CloseClientConnectionCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.ClientConnections;

public enum CloseOutcome
{
    Closed,
    /// <summary>404: the connection was gone before the close arrived -- the user's goal is met.</summary>
    AlreadyClosed,
}

/// <summary>Name = the broker's connection name (the path key); DisplayName = what the audit target shows.</summary>
public sealed record CloseClientConnectionCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name, string DisplayName, string? Reason);

public sealed class CloseClientConnectionCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CloseClientConnectionCommandHandler> logger)
{
    public async Task<PluginResult<CloseOutcome>> HandleAsync(CloseClientConnectionCommand cmd, CancellationToken ct = default)
    {
        var outcome = CloseOutcome.Closed;
        var result = await HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.connection.close", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.DisplayName), ActionRisk.Destructive, cmd.Reason,
            async secret =>
            {
                try
                {
                    await operations.CloseClientConnectionAsync(secret, cmd.Name, cmd.Reason, ct);
                }
                catch (ManagementApiException { StatusCode: 404 })
                {
                    outcome = CloseOutcome.AlreadyClosed;
                }
                catch (ManagementApiException { StatusCode: 403 } ex)
                {
                    // The generic 403 text blames the vhost permission; for a close it is the missing tag.
                    var user = RabbitConnectionSettings.From(secret).Username;
                    throw new InvalidOperationException($"Closing another user's connection needs the administrator tag on {user}.", ex);
                }
            }, ct);

        return result.IsSuccess ? PluginResult<CloseOutcome>.Ok(outcome) : PluginResult<CloseOutcome>.Fail(result.Error!);
    }
}
```

`FriendlyRabbitError.From(InvalidOperationException)` falls through to `SbConsole.Sdk.FriendlyError.From`, which returns the (truncated) `ex.Message` unchanged, so the 403 text reaches the page as written.

Register in `RabbitMqPlugin.ConfigureServices`:

```csharp
        services.AddScoped<ClientConnections.ListClientConnectionsQueryHandler>();
        services.AddScoped<ClientConnections.CloseClientConnectionCommandHandler>();
```

and in `RabbitPageTestBase`:

```csharp
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.ClientConnections.ListClientConnectionsQueryHandler>();
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.ClientConnections.CloseClientConnectionCommandHandler>();
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Client src/Plugins/SbConsole.Plugins.RabbitMq/ClientConnections src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs tests/SbConsole.Plugins.RabbitMq.Tests/Client tests/SbConsole.Plugins.RabbitMq.Tests/Handlers tests/SbConsole.Plugins.RabbitMq.Tests/Components/RabbitPageTestBase.cs
git commit -m "feat(rabbitmq): list client connections and channels, force-close a connection

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Connections & channels page

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/BrokerConnections.razor` (route `/p/rabbitmq/connections`; named `BrokerConnections` so the class doesn't collide with the existing `SbConsole.Plugins.RabbitMq.Connections` namespace)
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs` (nav item)
- Test: create `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/BrokerConnectionsPageTests.cs`; modify `tests/SbConsole.Plugins.RabbitMq.Tests/RabbitMqPluginTests.cs`

**Interfaces:**
- Consumes: `ListClientConnectionsQueryHandler`, `CloseClientConnectionCommandHandler`, `CloseOutcome`, `ClientConnectionInfo.CloseReason` (Task 7); `ConnectionVhostPicker`, `ManagementErrorAlert`, `StaleDataBanner`, `AutoRefresh`, `RabbitFormat`.
- Produces: CSS hooks `.connections-summary`, `.connection-filter`, `.client-connection-row`, `.connection-display`, `.connection-client`, `.connection-state`, `.this-console-chip`, `.internal-chip`, `button.expand-channels`, `.channel-row`, `.unbounded-prefetch-chip`, `button.close-connection`, `.connections-empty`; `internal Task<bool> AutoRefreshTickAsync()`; store key `connections.autoRefreshSeconds`.

- [ ] **Step 1: Write the failing page tests**

Create `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/BrokerConnectionsPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class BrokerConnectionsPageTests : RabbitPageTestBase
{
    private const string Vhost = "/orders";

    private static ClientConnectionInfo Network(string name, string user, string host, int port, int channels = 1,
        string? clientName = null, string state = "running", string? product = "RabbitMQ", string? version = "7.2.2") =>
        new(name, user, clientName, product, version, ".NET", "AMQP 0-9-1", false, false, state, channels, 12, 3,
            new DateTimeOffset(2026, 9, 26, 13, 0, 0, TimeSpan.Zero), host, port);

    private static readonly ClientConnectionInfo Shovel =
        new("<rabbit@rabbit-local.1.2.0>", "none", "Shovel legacy-migrate", null, null, null, "Direct 0-9-1", true, false, "running", 0, null, null, null, null, null);

    private void Seed(string secret)
    {
        Operations.ListClientConnectionsAsync(secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            Shovel,
            Network("172.18.0.1:53144 -> 172.18.0.3:5672", "sbconsole", "172.18.0.1", 53144, clientName: "sbconsole"),
            Network("10.4.2.17:40112 -> 172.18.0.3:5672", "billing-worker", "10.4.2.17", 40112, channels: 2, state: "blocked", product: "pika", version: "1.3.2"),
        ]);
        Operations.ListChannelsAsync(secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            new ChannelInfo("10.4.2.17:40112 -> 172.18.0.3:5672 (1)", "10.4.2.17:40112 -> 172.18.0.3:5672", 1, 0, 118, 1, false, false, "running"),
            new ChannelInfo("10.4.2.17:40112 -> 172.18.0.3:5672 (2)", "10.4.2.17:40112 -> 172.18.0.3:5672", 2, 50, 4, 1, true, false, "running"),
        ]);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync(ConnectionInfoChoice choice = ConnectionInfoChoice.Dev)
    {
        var connection = choice == ConnectionInfoChoice.Dev ? Dev : Prod;
        Navigation.NavigateTo($"/p/rabbitmq/connections?connectionId={connection.Id}&vhost=%2Forders");
        RenderFragment page = builder =>
        {
            builder.OpenComponent<BrokerConnections>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    public enum ConnectionInfoChoice { Dev, Prod }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string display) =>
        cut.FindAll(".client-connection-row").Single(r => r.QuerySelector(".connection-display")!.TextContent.Contains(display));

    [Fact]
    public async Task Lists_connections_with_client_state_and_a_summary()
    {
        Seed(DevSecret);
        var cut = await RenderPageAsync();

        cut.FindAll(".client-connection-row").Should().HaveCount(3);
        cut.Find(".connections-summary").TextContent.Should().Contain("3 connections · 2 channels · 122 unacked");
        Row(cut, "10.4.2.17:40112").QuerySelector(".connection-client")!.TextContent.Should().Contain("pika 1.3.2");
        Row(cut, "10.4.2.17:40112").QuerySelector(".connection-state")!.TextContent.Should().Contain("blocked");
    }

    [Fact]
    public async Task Marks_the_consoles_own_and_internal_connections_and_internal_ones_cannot_be_closed()
    {
        Seed(DevSecret);
        var cut = await RenderPageAsync();

        Row(cut, "172.18.0.1:53144").QuerySelector(".this-console-chip").Should().NotBeNull();
        var shovel = Row(cut, "Shovel legacy-migrate");
        shovel.QuerySelector(".internal-chip").Should().NotBeNull();
        shovel.QuerySelector("button.close-connection")!.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Expanding_a_connection_shows_its_channels_and_flags_unbounded_prefetch()
    {
        Seed(DevSecret);
        var cut = await RenderPageAsync();

        await Row(cut, "10.4.2.17:40112").QuerySelector("button.expand-channels")!.ClickAsync(new());

        var channels = cut.FindAll(".channel-row");
        channels.Should().HaveCount(2);
        channels[0].QuerySelector(".unbounded-prefetch-chip").Should().NotBeNull();
        channels[1].QuerySelector(".unbounded-prefetch-chip").Should().BeNull();
    }

    [Fact]
    public async Task Close_confirms_with_the_server_side_prod_flag_then_closes_with_the_console_reason()
    {
        Seed(ProdSecret);
        Confirmation.ConfirmAsync("Close connection", "10.4.2.17:40112", true, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync(ConnectionInfoChoice.Prod);

        await Row(cut, "10.4.2.17:40112").QuerySelector("button.close-connection")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CloseClientConnectionAsync(ProdSecret, "10.4.2.17:40112 -> 172.18.0.3:5672", "Closed from SbConsole", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_close_closes_nothing()
    {
        Seed(DevSecret);
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await Row(cut, "10.4.2.17:40112").QuerySelector("button.close-connection")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().CloseClientConnectionAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task The_filter_matches_user_client_and_address()
    {
        Seed(DevSecret);
        var cut = await RenderPageAsync();

        await cut.Find(".connection-filter input").InputAsync(new ChangeEventArgs { Value = "pika" });

        cut.FindAll(".client-connection-row").Should().ContainSingle();
    }

    [Fact]
    public async Task No_connections_shows_the_empty_state()
    {
        Operations.ListClientConnectionsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListChannelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        var cut = await RenderPageAsync();

        cut.Find(".connections-empty").TextContent.Should().Contain("No client connections to /orders.");
    }

    [Fact]
    public async Task A_first_load_failure_shows_the_management_error()
    {
        Operations.ListClientConnectionsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/vhosts/%2Forders/connections", "Access refused."));
        Operations.ListChannelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        var cut = await RenderPageAsync();

        cut.Find(".management-error").TextContent.Should().Contain("403");
    }
}
```

`ConnectionVhostPicker` reads `?connectionId=&vhost=` from the URI (`QueryHelpers.ParseQuery(Navigation.Uri)`), which is why `RenderPageAsync` navigates first — the same approach `GetMessagesPageTests` uses to pick the prod connection.

In `RabbitMqPluginTests.cs`, rename `Declares_the_four_nav_items_in_mockup_order` to `Declares_the_nav_items_in_order` and expect:

```csharp
        plugin.NavItems.Select(n => (n.Title, n.Href)).Should().Equal(
            ("Overview", "/p/rabbitmq/overview"),
            ("Exchanges", "/p/rabbitmq/exchanges"),
            ("Queues", "/p/rabbitmq/queues"),
            ("Shovels & policies", "/p/rabbitmq/shovels"),
            ("Connections", "/p/rabbitmq/connections"));
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~BrokerConnectionsPageTests|FullyQualifiedName~RabbitMqPluginTests"`
Expected: build FAILS — `BrokerConnections` not found.

- [ ] **Step 3: Implement the page**

`Pages/BrokerConnections.razor`:

```razor
@page "/p/rabbitmq/connections"
@using System.Globalization
@using Microsoft.Extensions.Logging
@using SbConsole.Plugins.RabbitMq.Client
@using SbConsole.Plugins.RabbitMq.ClientConnections
@inject ListClientConnectionsQueryHandler ListHandler
@inject CloseClientConnectionCommandHandler CloseHandler
@inject IConfirmationService Confirmation
@inject ISnackbar Snackbar
@inject TimeProvider Clock
@inject ILogger<BrokerConnections> Logger
@implements IDisposable

<PageTitle>RabbitMQ connections</PageTitle>
<h1>Connections</h1>

<ConnectionVhostPicker SelectionChanged="OnSelectionChangedAsync" Loading="_loading" />

@if (_selection is not null)
{
    @if (_snapshot is null)
    {
        @if (_loadError is not null)
        {
            <ManagementErrorAlert Error="@_loadError" OnRetry="RetryAsync" />
        }
        else
        {
            <div class="connections-loading">
                @for (var i = 0; i < 3; i++) { <MudSkeleton Class="my-2" /> }
                <MudText Typo="Typo.caption" Class="mud-text-secondary" Style="font-family:monospace">Reading /api/vhosts/@Uri.EscapeDataString(_selection.Vhost)/connections</MudText>
            </div>
        }
    }
    else
    {
        var snapshot = _snapshot;
        var now = Now;
        var rows = VisibleConnections(snapshot);

        @if (_refreshError is not null)
        {
            <StaleDataBanner Error="@_refreshError" LastGoodAt="_loadedAt" Now="now" OnRetry="RetryAsync"
                             AutoRefreshPaused="_paused" OnTogglePause="TogglePause" />
        }

        <div class="d-flex align-center flex-wrap gap-3 my-3">
            <MudTextField T="string" Class="connection-filter" Value="_filter" ValueChanged="OnFilterChanged" Immediate="true"
                          Placeholder="Filter by address, user or client…" Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search"
                          Variant="Variant.Outlined" Margin="Margin.Dense" Style="max-width:300px" />
            <MudText Class="connections-summary mud-text-secondary" Typo="Typo.body2">
                @Plural(snapshot.Connections.Count, "connection") · @Plural(snapshot.Channels.Count, "channel") · @RabbitFormat.Count(snapshot.Channels.Sum(c => c.Unacked)) unacked
            </MudText>
            <MudSpacer />
            <div class="d-flex align-center gap-2">
                <MudText Typo="Typo.overline" Class="mud-text-secondary">Auto-refresh</MudText>
                <MudButtonGroup Class="auto-refresh-picker" Variant="Variant.Outlined" Size="Size.Small">
                    @foreach (var seconds in AutoRefreshChoices)
                    {
                        <MudButton Class="@($"auto-refresh-{seconds}")" Color="@(seconds == AutoRefreshSeconds ? Color.Primary : Color.Default)"
                                   Variant="@(seconds == AutoRefreshSeconds ? Variant.Filled : Variant.Outlined)"
                                   OnClick="@(() => SetAutoRefreshAsync(seconds))">@(seconds == 0 ? "Off" : $"{seconds}s")</MudButton>
                    }
                </MudButtonGroup>
            </div>
        </div>

        <div class="@(_refreshError is null ? "connections-content" : "connections-content stale-data")" style="@(_refreshError is null ? null : StaleDataBanner.StaleStyle)">
            @if (snapshot.Connections.Count == 0)
            {
                <MudPaper Class="connections-empty pa-6 my-2" Outlined="true">
                    <MudText Typo="Typo.subtitle1">No client connections to <span style="font-family:monospace">@RabbitFormat.VhostLabel(_selection.Vhost)</span>.</MudText>
                </MudPaper>
            }
            else
            {
                <MudSimpleTable Dense="true" Elevation="0">
                    <thead>
                        <tr><th></th><th>Connection</th><th>User</th><th>Client</th><th>Protocol</th><th>State</th>
                            <th style="text-align:right">Channels</th><th style="text-align:right">Recv /s</th><th style="text-align:right">Send /s</th><th>Connected</th><th></th></tr>
                    </thead>
                    <tbody>
                        @foreach (var connection in rows)
                        {
                            var expanded = _expanded.Contains(connection.Name);
                            <tr class="client-connection-row">
                                <td>
                                    @if (!connection.IsDirect)
                                    {
                                        <MudIconButton Class="expand-channels" Size="Size.Small"
                                                       Icon="@(expanded ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
                                                       OnClick="@(() => ToggleExpanded(connection.Name))" />
                                    }
                                </td>
                                <td>
                                    <span class="connection-display" style="font-family:monospace">@connection.DisplayName</span>
                                    @if (connection.PeerHost is not null && connection.ClientProvidedName is { } clientName)
                                    {
                                        <MudText Typo="Typo.caption" Class="mud-text-secondary d-block">@clientName</MudText>
                                    }
                                    @if (connection.IsThisConsole)
                                    {
                                        <MudChip T="string" Class="this-console-chip" Size="Size.Small" Color="Color.Info" Variant="Variant.Outlined">this console</MudChip>
                                    }
                                    @if (connection.IsDirect)
                                    {
                                        <MudChip T="string" Class="internal-chip" Size="Size.Small" Variant="Variant.Outlined">internal</MudChip>
                                    }
                                </td>
                                <td>@connection.User</td>
                                <td class="connection-client">@ClientText(connection)</td>
                                <td>
                                    @connection.Protocol
                                    @if (connection.Tls)
                                    {
                                        <MudChip T="string" Class="tls-chip" Size="Size.Small" Color="Color.Success" Variant="Variant.Outlined">TLS</MudChip>
                                    }
                                </td>
                                <td class="connection-state"><MudChip T="string" Size="Size.Small" Color="@StateColor(connection.State)" Variant="Variant.Outlined">@connection.State</MudChip></td>
                                <td style="text-align:right;font-family:monospace">@(connection.IsDirect ? "—" : RabbitFormat.Count(connection.Channels))</td>
                                <td style="text-align:right;font-family:monospace">@RabbitFormat.Rate(connection.RecvRate)</td>
                                <td style="text-align:right;font-family:monospace">@RabbitFormat.Rate(connection.SendRate)</td>
                                <td>@(connection.ConnectedAt is { } at ? RabbitFormat.Ago(at, now) : "—")</td>
                                <td>
                                    <MudTooltip Text="@(connection.IsDirect ? "Internal broker connection (shovel/federation) — manage it from Shovels & policies" : "")">
                                        <MudButton Class="close-connection" Size="Size.Small" Variant="Variant.Text" Color="Color.Error"
                                                   Disabled="@(connection.IsDirect || _closing is not null)"
                                                   OnClick="@(() => CloseAsync(connection))">Close</MudButton>
                                    </MudTooltip>
                                </td>
                            </tr>
                            @if (expanded)
                            {
                                var channels = snapshot.Channels.Where(c => c.ConnectionName == connection.Name).OrderBy(c => c.Number).ToList();
                                @if (channels.Count == 0)
                                {
                                    <tr class="channel-empty-row"><td></td><td colspan="10" class="mud-text-secondary">No open channels.</td></tr>
                                }
                                @foreach (var channel in channels)
                                {
                                    <tr class="channel-row">
                                        <td></td>
                                        <td colspan="2" style="font-family:monospace">channel #@channel.Number.ToString(CultureInfo.InvariantCulture)</td>
                                        <td>prefetch @(channel.Prefetch == 0 ? "unlimited" : RabbitFormat.Count(channel.Prefetch))
                                            @if (channel.Prefetch == 0 && channel.Unacked > 0)
                                            {
                                                <MudChip T="string" Class="unbounded-prefetch-chip" Size="Size.Small" Color="Color.Warning" Variant="Variant.Outlined">unbounded prefetch</MudChip>
                                            }
                                        </td>
                                        <td>@RabbitFormat.Count(channel.Unacked) unacked</td>
                                        <td>@Plural(channel.Consumers, "consumer")</td>
                                        <td colspan="2">@(channel.ConfirmMode ? "confirm" : channel.Transactional ? "tx" : "")</td>
                                        <td colspan="3">@channel.State</td>
                                    </tr>
                                }
                            }
                        }
                    </tbody>
                </MudSimpleTable>
            }
            <MudText Typo="Typo.caption" Class="mud-text-secondary d-block mt-2">Closing another user's connection needs the administrator tag. The client sees the reason "@ClientConnectionInfo.CloseReason".</MudText>
        </div>
    }
}

@code {
    private const string AutoRefreshKey = "connections.autoRefreshSeconds";
    private const int DefaultAutoRefreshSeconds = 10;
    private static readonly int[] AutoRefreshChoices = [0, 10, 30, 60];

    [Inject(Key = "rabbitmq")]
    private IPluginStore Store { get; set; } = default!;

    private RabbitSelection? _selection;
    private ClientConnectionsSnapshot? _snapshot;
    private DateTimeOffset _loadedAt;
    private bool _loading;
    private string? _loadError;
    private string? _refreshError;
    private bool _paused;
    private int _generation;
    private AutoRefresh? _autoRefresh;
    private string _filter = "";
    private string? _closing;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    internal int AutoRefreshSeconds { get; private set; } = DefaultAutoRefreshSeconds;

    private DateTimeOffset Now => TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), Clock.LocalTimeZone);

    protected override async Task OnInitializedAsync()
    {
        _autoRefresh = new AutoRefresh(Clock);
        AutoRefreshSeconds = await ReadAutoRefreshSecondsAsync();
        RestartAutoRefresh();
    }

    private async Task<int> ReadAutoRefreshSecondsAsync()
    {
        try
        {
            var stored = await Store.GetAsync(AutoRefreshKey);
            return int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && AutoRefreshChoices.Contains(seconds)
                ? seconds
                : DefaultAutoRefreshSeconds;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Reading the RabbitMQ connections auto-refresh setting failed; defaulting to 10s.");
            return DefaultAutoRefreshSeconds;
        }
    }

    private async Task SetAutoRefreshAsync(int seconds)
    {
        AutoRefreshSeconds = seconds;
        _paused = false;
        RestartAutoRefresh();
        try
        {
            await Store.SetAsync(AutoRefreshKey, seconds.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Persisting the RabbitMQ connections auto-refresh setting failed.");
        }
    }

    private async Task OnSelectionChangedAsync(RabbitSelection selection)
    {
        _selection = selection;
        _snapshot = null;
        _loadError = null;
        _refreshError = null;
        _expanded.Clear();
        await LoadAsync();
        RestartAutoRefresh();
    }

    private void OnFilterChanged(string? value) => _filter = value ?? "";

    internal async Task<bool> AutoRefreshTickAsync()
    {
        if (_loading || _selection is null)
        {
            return false;
        }

        await LoadAsync();
        StateHasChanged();
        return true;
    }

    private Task RetryAsync() => LoadAsync();

    private void TogglePause()
    {
        _paused = !_paused;
        RestartAutoRefresh();
    }

    private void RestartAutoRefresh()
    {
        if (_paused || _selection is null || AutoRefreshSeconds == 0)
        {
            _autoRefresh?.Stop();
            return;
        }

        _autoRefresh?.Start(TimeSpan.FromSeconds(AutoRefreshSeconds), AutoRefreshTickAsync, InvokeAsync,
            ex => Logger.LogWarning(ex, "Auto-refreshing the RabbitMQ connections list failed."));
    }

    private async Task LoadAsync()
    {
        if (_selection is not { } selection)
        {
            return;
        }

        var generation = ++_generation;
        _loading = true;
        try
        {
            var result = await ListHandler.HandleAsync(selection.Connection.Id, selection.Vhost);
            if (generation != _generation)
            {
                return;
            }

            if (result.IsSuccess)
            {
                _snapshot = result.Value!;
                _loadedAt = Now;
                _loadError = null;
                _refreshError = null;
            }
            else if (_snapshot is null)
            {
                _loadError = result.Error!;
            }
            else
            {
                _refreshError = result.Error!;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _loading = false;
            }
        }
    }

    private List<ClientConnectionInfo> VisibleConnections(ClientConnectionsSnapshot snapshot) =>
        snapshot.Connections
            .Where(c => _filter.Length == 0
                || c.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || c.User.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || ClientText(c).Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (c.ClientProvidedName?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(c => c.IsDirect ? 1 : 0)
            .ThenBy(c => c.DisplayName, StringComparer.Ordinal)
            .ToList();

    private static string ClientText(ClientConnectionInfo c) =>
        c.Product is null ? "—" : c.ProductVersion is null ? c.Product : $"{c.Product} {c.ProductVersion}";

    private static Color StateColor(string state) => state switch
    {
        "running" => Color.Success,
        "blocked" or "blocking" or "flow" => Color.Warning,
        "closing" or "closed" => Color.Error,
        _ => Color.Default,
    };

    private static string Plural(int n, string noun) => $"{RabbitFormat.Count(n)} {noun}{(n == 1 ? "" : "s")}";

    private void ToggleExpanded(string name)
    {
        if (!_expanded.Remove(name))
        {
            _expanded.Add(name);
        }
    }

    private async Task CloseAsync(ClientConnectionInfo connection)
    {
        var selection = _selection!;
        if (connection.IsDirect)
        {
            return;
        }

        // Always confirmed (Destructive); IsProd from the server-side connection lookup the picker made.
        if (!await Confirmation.ConfirmAsync("Close connection", connection.DisplayName, selection.Connection.IsProd))
        {
            return;
        }

        _closing = connection.Name;
        try
        {
            var result = await CloseHandler.HandleAsync(new CloseClientConnectionCommand(
                selection.Connection.Id, selection.Connection.Name, selection.Vhost, connection.Name, connection.DisplayName, ClientConnectionInfo.CloseReason));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
            else
            {
                Snackbar.Add(result.Value == CloseOutcome.AlreadyClosed ? "Connection had already closed" : $"Closed {connection.DisplayName}", Severity.Success);
            }
        }
        finally
        {
            _closing = null;
        }

        await LoadAsync();
    }

    public void Dispose() => _autoRefresh?.Dispose();
}
```

`RabbitFormat.Count` takes a `long`; the `int` arguments convert implicitly.

Add the nav item in `RabbitMqPlugin.NavItems` after Shovels:

```csharp
        new("Connections", "/p/rabbitmq/connections"),
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Pages/BrokerConnections.razor src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/BrokerConnectionsPageTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/RabbitMqPluginTests.cs
git commit -m "feat(rabbitmq): connections & channels page with force-close

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Safe replay — `ReplayOrigin`, `DeadLetterReplayer`, AMQP transport

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/Replay.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Routing/ReplayOrigin.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Messages/DeadLetterReplayer.cs`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/Client/AmqpClient.cs`
- Modify: `Client/IRabbitOperations.cs`, `Client/RabbitOperations.cs`
- Test: create `tests/SbConsole.Plugins.RabbitMq.Tests/Routing/ReplayOriginTests.cs`, `tests/SbConsole.Plugins.RabbitMq.Tests/Messages/DeadLetterReplayerTests.cs`

**Interfaces:**
- Produces:
  - `record ReplayRequest(int MaxMessages, int PoisonThreshold)`; `record ReplayOutcome(int Replayed, int SkippedNoOrigin, int SkippedPoison, string? StoppedReason, int? StoppedAtIndex)` (namespace `SbConsole.Plugins.RabbitMq.Client`)
  - `record ReplayTarget(string Exchange, string RoutingKey, long DeathCount)`; `ReplayOrigin.Resolve(RabbitMessage message) : ReplayTarget?` (namespace `…Routing`)
  - `internal record ReplayDelivery(ulong DeliveryTag, RabbitMessage Message)`; `internal interface IReplayChannel { Task<ReplayDelivery?> GetAsync(CancellationToken ct); Task<bool> RepublishAsync(ReplayDelivery delivery, string exchange, string routingKey, CancellationToken ct); Task AckAsync(ulong deliveryTag); Task RequeueAsync(ulong deliveryTag); }`; `internal static class DeadLetterReplayer { public const int MaxMessages = 1000; RunAsync(IReplayChannel, ReplayRequest, Func<Exception, string> describe, CancellationToken) : Task<ReplayOutcome> }` (namespace `…Messages`)
  - `IRabbitOperations.ReplayDeadLettersAsync(string secret, string vhost, string queue, ReplayRequest request, CancellationToken ct = default) : Task<ReplayOutcome>`

The test project must see `internal` types. Check `src/Plugins/SbConsole.Plugins.RabbitMq/SbConsole.Plugins.RabbitMq.csproj` for `<InternalsVisibleTo Include="SbConsole.Plugins.RabbitMq.Tests" />` — the existing tests construct the `internal` `ManagementApiClient`, so it is there.

- [ ] **Step 1: Write the failing origin tests**

`tests/SbConsole.Plugins.RabbitMq.Tests/Routing/ReplayOriginTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

public class ReplayOriginTests
{
    private static RabbitMessage Message(IReadOnlyList<DeathRecord> deaths, string? firstQueue, string? firstExchange) =>
        new(0, "m-1", null, "billing.retry.dlx", "dlq.payments", false, "application/json", null, 2, null, null, null,
            new Dictionary<string, string>(), [1], deaths, firstQueue, firstExchange, null);

    private static DeathRecord Death(string queue, string exchange, string reason, long count, params string[] keys) =>
        new(queue, exchange, reason, keys, count, null);

    [Fact]
    public void The_origin_is_the_record_matching_the_first_death_headers_not_the_first_entry()
    {
        // x-death is most-recent-first: the retry queue's death is listed before the original one.
        var message = Message(
            [Death("billing.retry", "billing.retry.dlx", "expired", 3, "dlq.payments"),
             Death("billing.payments.q", "billing.direct", "rejected", 2, "payment.capture")],
            firstQueue: "billing.payments.q", firstExchange: "billing.direct");

        ReplayOrigin.Resolve(message).Should().Be(new ReplayTarget("billing.direct", "payment.capture", 2));
    }

    [Fact]
    public void A_message_first_published_to_the_default_exchange_goes_back_to_its_queue_by_name()
    {
        var message = Message([Death("work.q", "", "rejected", 1, "work.q")], "work.q", "");

        ReplayOrigin.Resolve(message).Should().Be(new ReplayTarget("", "work.q", 1));
    }

    [Fact]
    public void Missing_history_or_first_death_headers_mean_no_origin()
    {
        ReplayOrigin.Resolve(Message([], null, null)).Should().BeNull();
        ReplayOrigin.Resolve(Message([Death("q", "x", "expired", 1, "k")], null, "x")).Should().BeNull();
        ReplayOrigin.Resolve(Message([Death("q", "x", "expired", 1, "k")], "other", "x")).Should().BeNull();
    }

    [Fact]
    public void A_record_with_no_routing_keys_replays_with_an_empty_key()
    {
        ReplayOrigin.Resolve(Message([Death("q", "fan", "expired", 1)], "q", "fan")).Should().Be(new ReplayTarget("fan", "", 1));
    }
}
```

- [ ] **Step 2: Write the failing replayer tests**

`tests/SbConsole.Plugins.RabbitMq.Tests/Messages/DeadLetterReplayerTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Messages;

namespace SbConsole.Plugins.RabbitMq.Tests.Messages;

public class DeadLetterReplayerTests
{
    /// <summary>Scripted channel: deliveries come out in order; every call is logged.</summary>
    private sealed class FakeChannel(params RabbitMessage[] messages) : IReplayChannel
    {
        private readonly Queue<RabbitMessage> _queue = new(messages);
        private ulong _tag;

        public List<string> Log { get; } = [];
        public Func<ReplayDelivery, bool> Routed { get; set; } = _ => true;
        public Exception? PublishThrowsOn { get; set; }
        public ulong? PublishThrowsForTag { get; set; }
        public bool GetThrowsCancel { get; set; }

        public Task<ReplayDelivery?> GetAsync(CancellationToken ct)
        {
            if (GetThrowsCancel) throw new OperationCanceledException(ct);
            Log.Add("get");
            return Task.FromResult(_queue.TryDequeue(out var m) ? new ReplayDelivery(++_tag, m) : null);
        }

        public Task<bool> RepublishAsync(ReplayDelivery delivery, string exchange, string routingKey, CancellationToken ct)
        {
            Log.Add($"publish {delivery.DeliveryTag} {exchange}/{routingKey}");
            if (PublishThrowsOn is { } ex && PublishThrowsForTag == delivery.DeliveryTag) throw ex;
            return Task.FromResult(Routed(delivery));
        }

        public Task AckAsync(ulong deliveryTag)
        {
            Log.Add($"ack {deliveryTag}");
            return Task.CompletedTask;
        }

        public Task RequeueAsync(ulong deliveryTag)
        {
            Log.Add($"requeue {deliveryTag}");
            return Task.CompletedTask;
        }
    }

    private static RabbitMessage Dead(string id, long count = 1) =>
        new(0, id, null, "billing.retry.dlx", "dlq.payments", false, null, null, 2, null, null, null,
            new Dictionary<string, string>(), [1],
            [new DeathRecord("billing.payments.q", "billing.direct", "rejected", ["payment.capture"], count, null)],
            "billing.payments.q", "billing.direct", null);

    private static RabbitMessage NoHistory(string id) =>
        new(0, id, null, "", "payments-dlq", false, null, null, 2, null, null, null,
            new Dictionary<string, string>(), [1], [], null, null, null);

    private static Task<ReplayOutcome> Run(FakeChannel channel, int max = 100, int poison = 5, CancellationToken ct = default) =>
        DeadLetterReplayer.RunAsync(channel, new ReplayRequest(max, poison), ex => ex.Message, ct);

    [Fact]
    public async Task Each_message_is_acked_only_after_its_republish_is_confirmed()
    {
        var channel = new FakeChannel(Dead("a"), Dead("b"));

        var outcome = await Run(channel);

        outcome.Should().Be(new ReplayOutcome(2, 0, 0, null, null));
        channel.Log.Should().Equal(
            "get", "publish 1 billing.direct/payment.capture", "ack 1",
            "get", "publish 2 billing.direct/payment.capture", "ack 2",
            "get");
    }

    [Fact]
    public async Task Messages_without_history_and_poison_messages_are_skipped_and_requeued_at_the_end()
    {
        var channel = new FakeChannel(NoHistory("x"), Dead("poison", count: 5), Dead("ok"));

        var outcome = await Run(channel);

        outcome.Should().Be(new ReplayOutcome(1, 1, 1, null, null));
        channel.Log.Should().Equal(
            "get", "get", "get", "publish 3 billing.direct/payment.capture", "ack 3", "get",
            "requeue 1", "requeue 2");
    }

    [Fact]
    public async Task An_unroutable_republish_stops_the_run_and_requeues_that_message()
    {
        var channel = new FakeChannel(Dead("a"), Dead("b"), Dead("c")) { Routed = d => d.DeliveryTag != 2 };

        var outcome = await Run(channel);

        outcome.Replayed.Should().Be(1);
        outcome.StoppedAtIndex.Should().Be(2);
        outcome.StoppedReason.Should().Be("billing.direct/payment.capture is unroutable — the broker returned the message");
        channel.Log.Should().Equal(
            "get", "publish 1 billing.direct/payment.capture", "ack 1",
            "get", "publish 2 billing.direct/payment.capture", "requeue 2");
    }

    [Fact]
    public async Task A_failed_republish_stops_the_run_and_requeues_everything_held()
    {
        var channel = new FakeChannel(NoHistory("x"), Dead("a"))
        {
            PublishThrowsOn = new InvalidOperationException("negative confirm"),
            PublishThrowsForTag = 2,
        };

        var outcome = await Run(channel);

        outcome.Should().Be(new ReplayOutcome(0, 1, 0, "negative confirm", 2));
        channel.Log.Should().EndWith(["requeue 1", "requeue 2"]);
        channel.Log.Should().NotContain(l => l.StartsWith("ack"));
    }

    [Fact]
    public async Task Cancellation_reports_where_it_stopped_and_still_requeues_what_was_held()
    {
        var cancelled = new FakeChannel(NoHistory("y")) { GetThrowsCancel = true };
        var outcome = await Run(cancelled);

        outcome.StoppedAtIndex.Should().Be(1);
        outcome.StoppedReason.Should().NotBeNull();
    }

    [Fact]
    public async Task The_run_is_capped_at_1000_messages()
    {
        var channel = new FakeChannel(Enumerable.Range(0, 1005).Select(i => Dead($"m{i}")).ToArray());

        var outcome = await Run(channel, max: 5000);

        outcome.Replayed.Should().Be(DeadLetterReplayer.MaxMessages);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~ReplayOriginTests|FullyQualifiedName~DeadLetterReplayerTests"`
Expected: build FAILS.

- [ ] **Step 4: Implement the records, `ReplayOrigin` and `DeadLetterReplayer`**

`Client/Replay.cs`:

```csharp
namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>MaxMessages is clamped to 1..1000; PoisonThreshold to ≥ 1 (design spec §2.1).</summary>
public sealed record ReplayRequest(int MaxMessages, int PoisonThreshold);

/// <summary>
/// What one replay run did. StoppedReason/StoppedAtIndex are set when the run ended early (a
/// returned or unconfirmed republish, a broker error, cancellation); the 1-based index is the
/// message it stopped on, which -- like every skipped message -- went back to the DLQ.
/// </summary>
public sealed record ReplayOutcome(int Replayed, int SkippedNoOrigin, int SkippedPoison, string? StoppedReason, int? StoppedAtIndex);
```

`Routing/ReplayOrigin.cs`:

```csharp
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Routing;

/// <summary>Where a dead letter is replayed to, and how many times it has already died from there.</summary>
public sealed record ReplayTarget(string Exchange, string RoutingKey, long DeathCount);

/// <summary>
/// Resolves a dead letter's origin (design spec §2.1). x-death is ordered most-recent-first, so the
/// origin is NOT its first entry: it is the record whose queue and exchange equal the
/// x-first-death-queue / x-first-death-exchange headers. Pure.
/// </summary>
public static class ReplayOrigin
{
    public static ReplayTarget? Resolve(RabbitMessage message)
    {
        if (message.FirstDeathQueue is not { } queue || message.FirstDeathExchange is not { } exchange)
        {
            return null;
        }

        var record = message.Deaths.FirstOrDefault(d =>
            string.Equals(d.Queue, queue, StringComparison.Ordinal) && string.Equals(d.Exchange, exchange, StringComparison.Ordinal));
        if (record is null)
        {
            return null;
        }

        // Published straight to the queue through the default exchange: route back by queue name.
        if (exchange.Length == 0)
        {
            return new ReplayTarget("", queue, record.Count);
        }

        return new ReplayTarget(exchange, record.RoutingKeys.Count > 0 ? record.RoutingKeys[0] : "", record.Count);
    }
}
```

`Messages/DeadLetterReplayer.cs`:

```csharp
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;

namespace SbConsole.Plugins.RabbitMq.Messages;

internal sealed record ReplayDelivery(ulong DeliveryTag, RabbitMessage Message);

/// <summary>The four AMQP steps replay needs, on one channel with publisher confirms. The seam that keeps the loop testable.</summary>
internal interface IReplayChannel
{
    /// <summary>basic.get without auto-ack; null when the queue is empty.</summary>
    Task<ReplayDelivery?> GetAsync(CancellationToken ct);

    /// <summary>Publishes the delivery unchanged (properties, headers incl. x-death, body), mandatory, and waits for the confirm. False = basic.return (unroutable); throws on a negative confirm.</summary>
    Task<bool> RepublishAsync(ReplayDelivery delivery, string exchange, string routingKey, CancellationToken ct);

    Task AckAsync(ulong deliveryTag);

    Task RequeueAsync(ulong deliveryTag);
}

/// <summary>
/// The replay loop (design spec §2.1). A message is acked only after the broker confirmed its
/// republished copy, so no failure point loses a message (a connection drop between confirm and
/// ack can duplicate one: at-least-once). Skipped messages are held unacked until the end so
/// basic.get moves past them, then all held tags are requeued -- on success, on a stop, and on
/// cancellation. If requeueing fails the channel is broken, and closing it requeues the rest.
/// </summary>
internal static class DeadLetterReplayer
{
    public const int MaxMessages = 1000;

    public static async Task<ReplayOutcome> RunAsync(IReplayChannel channel, ReplayRequest request, Func<Exception, string> describe, CancellationToken ct)
    {
        var max = Math.Clamp(request.MaxMessages, 1, MaxMessages);
        var threshold = Math.Max(1, request.PoisonThreshold);
        var held = new List<ulong>();
        int replayed = 0, noOrigin = 0, poison = 0, index = 0;
        string? stoppedReason = null;
        int? stoppedAt = null;

        try
        {
            for (index = 1; index <= max; index++)
            {
                ct.ThrowIfCancellationRequested();
                var delivery = await channel.GetAsync(ct);
                if (delivery is null)
                {
                    break;
                }

                var target = ReplayOrigin.Resolve(delivery.Message);
                if (target is null)
                {
                    noOrigin++;
                    held.Add(delivery.DeliveryTag);
                    continue;
                }

                if (target.DeathCount >= threshold)
                {
                    poison++;
                    held.Add(delivery.DeliveryTag);
                    continue;
                }

                bool routed;
                try
                {
                    routed = await channel.RepublishAsync(delivery, target.Exchange, target.RoutingKey, ct);
                }
                catch
                {
                    held.Add(delivery.DeliveryTag);
                    throw;
                }

                if (!routed)
                {
                    held.Add(delivery.DeliveryTag);
                    stoppedReason = $"{target.Exchange}/{target.RoutingKey} is unroutable — the broker returned the message";
                    stoppedAt = index;
                    break;
                }

                await channel.AckAsync(delivery.DeliveryTag);
                replayed++;
            }
        }
        catch (Exception ex)
        {
            stoppedReason = describe(ex);
            stoppedAt = index;
        }
        finally
        {
            foreach (var tag in held)
            {
                try
                {
                    await channel.RequeueAsync(tag);
                }
                catch (Exception)
                {
                    // The channel is gone; closing it requeues whatever is still held.
                    break;
                }
            }
        }

        return new ReplayOutcome(replayed, noOrigin, poison, stoppedReason, stoppedAt);
    }
}
```

Check the cancellation test: `GetThrowsCancel` throws `OperationCanceledException` from `GetAsync` at `index == 1` → caught → `stoppedAt = 1`, `stoppedReason = ex.Message` (non-null). The capped test: `max` is clamped to 1000, the loop exits with `index == 1001`, no stop.

- [ ] **Step 5: Run the pure tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~ReplayOriginTests|FullyQualifiedName~DeadLetterReplayerTests"`
Expected: PASS.

- [ ] **Step 6: Implement the AMQP transport and operation**

In `AmqpClient.cs` add `using SbConsole.Plugins.RabbitMq.Messages;` and, after `PublishAsync`:

```csharp
    /// <summary>
    /// Replays up to request.MaxMessages dead letters from <paramref name="queue"/> to their origin
    /// (<see cref="DeadLetterReplayer"/>), on one channel with publisher confirms and tracking. Not
    /// unit-tested here (needs a broker); the loop's guarantees are tested through IReplayChannel.
    /// </summary>
    public async Task<ReplayOutcome> ReplayDeadLettersAsync(
        RabbitConnectionSettings settings, string vhost, string queue, ReplayRequest request, CancellationToken ct = default)
    {
        var connection = await OpenAsync(settings, vhost, ct);
        IChannel? channel = null;
        ReplayOutcome outcome;
        try
        {
            channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            outcome = await DeadLetterReplayer.RunAsync(new AmqpReplayChannel(channel, queue), request, FriendlyRabbitError.From, ct);
        }
        catch
        {
            // Opening the channel failed (the loop itself never throws): dropping everything requeues.
            await DisposeQuietlyAsync(channel, connection, logger);
            throw;
        }

        await ShutdownQuietlyAsync(channel, connection, logger);
        return outcome;
    }

    private sealed class AmqpReplayChannel(IChannel channel, string queue) : IReplayChannel
    {
        // The original properties and a copy of the body, per held delivery tag, for the republish.
        private readonly Dictionary<ulong, (IReadOnlyBasicProperties Properties, byte[] Body)> _held = new();
        private int _index;

        public async Task<ReplayDelivery?> GetAsync(CancellationToken ct)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false, ct);
            if (result is null)
            {
                return null;
            }

            var body = result.Body.ToArray();
            _held[result.DeliveryTag] = (result.BasicProperties, body);
            var message = AmqpMessageMapper.Map(_index++, result.Exchange, result.RoutingKey, result.Redelivered, result.BasicProperties, body);
            return new ReplayDelivery(result.DeliveryTag, message);
        }

        public async Task<bool> RepublishAsync(ReplayDelivery delivery, string exchange, string routingKey, CancellationToken ct)
        {
            var (properties, body) = _held[delivery.DeliveryTag];
            try
            {
                await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, new BasicProperties(properties), body, ct);
                return true;
            }
            catch (PublishException ex) when (ex.IsReturn)
            {
                return false;
            }
            catch (PublishException ex)
            {
                throw new InvalidOperationException("The broker did not accept the republished message (negative publisher confirm).", ex);
            }
        }

        public async Task AckAsync(ulong deliveryTag)
        {
            _held.Remove(deliveryTag);
            await channel.BasicAckAsync(deliveryTag, multiple: false, CancellationToken.None);
        }

        public async Task RequeueAsync(ulong deliveryTag)
        {
            _held.Remove(deliveryTag);
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }
```

(`BasicProperties(IReadOnlyBasicProperties)` is the copy constructor RabbitMQ.Client 7.2.2 ships — verified by reflection while planning. `BasicAckAsync`/`BasicNackAsync` return `ValueTask`; awaiting them inside `async Task` methods is fine.)

`IRabbitOperations.cs`, AMQP section:

```csharp
    /// <summary>
    /// Moves up to request.MaxMessages dead letters back to the exchange/key they were first
    /// dead-lettered from, acking each only after its republish is confirmed (design spec §2.1).
    /// </summary>
    Task<ReplayOutcome> ReplayDeadLettersAsync(string secret, string vhost, string queue, ReplayRequest request, CancellationToken ct = default);
```

`RabbitOperations.cs`:

```csharp
    public async Task<ReplayOutcome> ReplayDeadLettersAsync(string secret, string vhost, string queue, ReplayRequest request, CancellationToken ct = default) =>
        await _amqp.ReplayDeadLettersAsync(Settings(secret), vhost, queue, request, ct);
```

- [ ] **Step 7: Build and run the full plugin tests**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: build succeeds with 0 warnings; tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Client src/Plugins/SbConsole.Plugins.RabbitMq/Routing/ReplayOrigin.cs src/Plugins/SbConsole.Plugins.RabbitMq/Messages/DeadLetterReplayer.cs tests/SbConsole.Plugins.RabbitMq.Tests/Routing/ReplayOriginTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Messages
git commit -m "feat(rabbitmq): replay dead letters to their origin, acking only after confirm

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Dead-letter handlers — cross-connection overview and replay command

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/DeadLetters/ListDeadLettersQueryHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/DeadLetters/ReplayDeadLettersCommandHandler.cs`
- Modify: `RabbitMqPlugin.cs` (DI), `tests/.../Components/RabbitPageTestBase.cs` (DI)
- Test: create `tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/DeadLetterHandlerTests.cs`

**Interfaces:**
- Consumes: `RabbitMqPlugin.FetchSnapshotAsync`, `RabbitMqPlugin.IsDeadLetterQueue`, `BrokerSnapshot` (internal, existing); `DeadLetterTopology.SourcesOf` / `RouteOut` (Task 2); `ReplayRequest`, `ReplayOutcome`, `IRabbitOperations.ReplayDeadLettersAsync` (Task 9).
- Produces (namespace `SbConsole.Plugins.RabbitMq.DeadLetters`):
  - `record DeadLetterQueueRow(ConnectionInfo Connection, string Vhost, QueueSummary Queue, IReadOnlyList<string> Sources, IReadOnlyList<string> DeadLetterExchanges)`
  - `record DeadLetterConnectionError(ConnectionInfo Connection, string Error)`
  - `record DeadLetterSnapshot(IReadOnlyList<DeadLetterQueueRow> Queues, IReadOnlyList<DeadLetterConnectionError> Errors, int ConnectionCount)`
  - `ListDeadLettersQueryHandler.HandleAsync(CancellationToken ct = default) : Task<DeadLetterSnapshot>`
  - `record ReplayDeadLettersCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Queue, ReplayRequest Request)`
  - `ReplayDeadLettersCommandHandler.HandleAsync(ReplayDeadLettersCommand, CancellationToken) : Task<PluginResult<ReplayOutcome>>`
  - `ReplayDeadLettersCommandHandler.Describe(ReplayOutcome outcome, int poisonThreshold) : string` (public static; the page reuses it)

- [ ] **Step 1: Write the failing handler tests**

`tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/DeadLetterHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.DeadLetters;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Handlers;

public class DeadLetterHandlerTests : HandlerTestBase
{
    private const string OtherSecret = "host=rabbit-2;vhost=%2F;username=u;password=p";
    private readonly ConnectionInfo _first;
    private readonly ConnectionInfo _second = new(Guid.NewGuid(), "rabbit-eu", "rabbitmq", []);

    public DeadLetterHandlerTests()
    {
        _first = new ConnectionInfo(ConnectionId, "rabbit-uk-prod", "rabbitmq", ["prod"]);
        Connections.ListAsync("rabbitmq", Arg.Any<CancellationToken>()).Returns([_first, _second]);
        Connections.GetSecretAsync(_second.Id, Arg.Any<CancellationToken>()).Returns(OtherSecret);
    }

    private static QueueSummary Q(string name, long ready, string? dlx = null, string? dlrk = null) =>
        Queue(name, ready) with { DeadLetterExchange = dlx, DeadLetterRoutingKey = dlrk };

    private void SeedFirst()
    {
        Operations.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns([
            Q("billing.payments.q", 0, "billing.retry.dlx", "dlq.payments"),
            Q("order-events.q", 0, "billing.retry.dlx", "dlq.order-events"),
            Q("payments-dlq", 10),
            Q("audit.sink", 15),
        ]);
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            Binding("billing.retry.dlx", "payments-dlq", "dlq.payments"),
            Binding("billing.retry.dlx", "payments-dlq", "dlq.order-events"),
        ]);
        Operations.ListExchangesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([
            new ExchangeSummary("billing.retry.dlx", "direct", true, false, false, null, null, null, new Dictionary<string, object?>()),
        ]);
    }

    [Fact]
    public async Task Overview_lists_every_dlq_with_its_sources_and_dlx_and_reports_failing_connections()
    {
        SeedFirst();
        Operations.ListNodesAsync(OtherSecret, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(401, "GET", "/api/nodes", null));
        Operations.ListQueuesAsync(OtherSecret, null, Arg.Any<CancellationToken>()).Returns([]);

        var snapshot = await new ListDeadLettersQueryHandler(Operations, Connections, Log<ListDeadLettersQueryHandler>()).HandleAsync();

        snapshot.ConnectionCount.Should().Be(2);
        var row = snapshot.Queues.Should().ContainSingle().Subject;
        row.Connection.Should().Be(_first);
        row.Vhost.Should().Be(Vhost);
        row.Queue.Name.Should().Be("payments-dlq");
        row.Sources.Should().Equal("billing.payments.q", "order-events.q");
        row.DeadLetterExchanges.Should().Equal("billing.retry.dlx");
        snapshot.Errors.Should().ContainSingle().Which.Connection.Should().Be(_second);
        snapshot.Errors[0].Error.Should().Be("Management API rejected the credentials");
    }

    [Fact]
    public async Task Replay_is_destructive_and_audits_the_counts()
    {
        Operations.ReplayDeadLettersAsync(Secret, Vhost, "payments-dlq", new ReplayRequest(100, 5), Arg.Any<CancellationToken>())
            .Returns(new ReplayOutcome(8, 1, 1, null, null));

        var result = await new ReplayDeadLettersCommandHandler(Operations, Connections, Audit, Log<ReplayDeadLettersCommandHandler>())
            .HandleAsync(new ReplayDeadLettersCommand(ConnectionId, "rabbit-uk-prod", Vhost, "payments-dlq", new ReplayRequest(100, 5)));

        result.Value.Should().Be(new ReplayOutcome(8, 1, 1, null, null));
        await Audit.Received(1).RecordAsync("rabbitmq.message.replay", "rabbit-uk-prod//orders/payments-dlq", ActionRisk.Destructive, true,
            "8 replayed · 1 skipped (no origin) · 1 skipped (poison, ≥5 deaths)", CancellationToken.None);
    }

    [Fact]
    public async Task A_run_that_stopped_early_is_still_a_result_but_audited_as_failed_with_its_counts()
    {
        Operations.ReplayDeadLettersAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ReplayOutcome(3, 0, 0, "billing.direct/payment.capture is unroutable — the broker returned the message", 4));

        var result = await new ReplayDeadLettersCommandHandler(Operations, Connections, Audit, Log<ReplayDeadLettersCommandHandler>())
            .HandleAsync(new ReplayDeadLettersCommand(ConnectionId, "rabbit-uk-prod", Vhost, "payments-dlq", new ReplayRequest(100, 5)));

        result.IsSuccess.Should().BeTrue();
        await Audit.Received(1).RecordAsync("rabbitmq.message.replay", Arg.Any<string>(), ActionRisk.Destructive, false,
            "3 replayed · 0 skipped (no origin) · 0 skipped (poison, ≥5 deaths) · stopped at message 4: billing.direct/payment.capture is unroutable — the broker returned the message",
            CancellationToken.None);
    }

    [Fact]
    public async Task A_replay_that_throws_is_a_friendly_failure_and_audited()
    {
        Operations.ReplayDeadLettersAsync(default!, default!, default!, default!, default).ThrowsAsyncForAnyArgs(Forbidden);

        var result = await new ReplayDeadLettersCommandHandler(Operations, Connections, Audit, Log<ReplayDeadLettersCommandHandler>())
            .HandleAsync(new ReplayDeadLettersCommand(ConnectionId, "rabbit-uk-prod", Vhost, "payments-dlq", new ReplayRequest(100, 5)));

        result.Error.Should().Be(ForbiddenText);
        await Audit.Received(1).RecordAsync("rabbitmq.message.replay", Arg.Any<string>(), ActionRisk.Destructive, false, ForbiddenText, CancellationToken.None);
    }
}
```

`HandlerTestBase.Queue(name, ready)` creates a queue in vhost `/orders`; `QueueSummary` is a record, so `with { DeadLetterExchange = … }` works. `FriendlyRabbitError` maps a 401 without a vhost reason to "Management API rejected the credentials".

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~DeadLetterHandlerTests"`
Expected: build FAILS.

- [ ] **Step 3: Implement the handlers**

`DeadLetters/ListDeadLettersQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.DeadLetters;

public sealed record DeadLetterQueueRow(
    ConnectionInfo Connection, string Vhost, QueueSummary Queue, IReadOnlyList<string> Sources, IReadOnlyList<string> DeadLetterExchanges);

public sealed record DeadLetterConnectionError(ConnectionInfo Connection, string Error);

public sealed record DeadLetterSnapshot(IReadOnlyList<DeadLetterQueueRow> Queues, IReadOnlyList<DeadLetterConnectionError> Errors, int ConnectionCount);

/// <summary>
/// Every DLQ on every RabbitMQ connection, every vhost (design spec §2). Reads each connection's
/// broker snapshot fresh -- the host hooks' 60 s cache would hide the effect of a replay the user
/// just ran. A connection that fails becomes one error entry; the others still render. Never throws.
/// </summary>
public sealed class ListDeadLettersQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<ListDeadLettersQueryHandler> logger)
{
    public async Task<DeadLetterSnapshot> HandleAsync(CancellationToken ct = default)
    {
        var all = await connections.ListAsync("rabbitmq", ct);
        var results = await Task.WhenAll(all.Select(async connection =>
        {
            try
            {
                var secret = await connections.GetSecretAsync(connection.Id, ct);
                if (secret is null)
                {
                    return (connection, Snapshot: (RabbitMqPlugin.BrokerSnapshot?)null, Error: (string?)HandlerRunner.ConnectionNotFound);
                }

                return (connection, Snapshot: await RabbitMqPlugin.FetchSnapshotAsync(operations, secret, ct), Error: (string?)null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading dead-letter queues for connection {ConnectionId} failed.", connection.Id);
                return (connection, Snapshot: (RabbitMqPlugin.BrokerSnapshot?)null, Error: (string?)FriendlyRabbitError.From(ex));
            }
        }));

        var rows = new List<DeadLetterQueueRow>();
        var errors = new List<DeadLetterConnectionError>();
        foreach (var (connection, snapshot, error) in results)
        {
            if (snapshot is null)
            {
                errors.Add(new DeadLetterConnectionError(connection, error!));
                continue;
            }

            foreach (var queue in snapshot.Queues.Where(q => RabbitMqPlugin.IsDeadLetterQueue(snapshot, q)))
            {
                var topology = snapshot.TopologyByVhost[queue.Vhost];
                var sources = topology.SourcesOf(queue.Name);
                var dlxs = sources
                    .Select(s => topology.RouteOut(s)?.Exchange)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                rows.Add(new DeadLetterQueueRow(connection, queue.Vhost, queue, sources, dlxs));
            }
        }

        var ordered = rows
            .OrderByDescending(r => r.Queue.Ready)
            .ThenBy(r => r.Connection.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Vhost, StringComparer.Ordinal)
            .ThenBy(r => r.Queue.Name, StringComparer.Ordinal)
            .ToList();
        return new DeadLetterSnapshot(ordered, errors, all.Count);
    }
}
```

`DeadLetters/ReplayDeadLettersCommandHandler.cs`:

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.DeadLetters;

public sealed record ReplayDeadLettersCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Queue, ReplayRequest Request);

/// <summary>
/// One audit row per run (design spec §2.1). A run that stopped early still returns its outcome --
/// the messages it moved did move -- but its audit row is marked failed, with the counts and the
/// reason in the detail. Uses ResolveSecretAsync + AuditQuietlyAsync directly because the audit
/// detail depends on the outcome.
/// </summary>
public sealed class ReplayDeadLettersCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ReplayDeadLettersCommandHandler> logger)
{
    private const string Action = "rabbitmq.message.replay";

    public async Task<PluginResult<ReplayOutcome>> HandleAsync(ReplayDeadLettersCommand cmd, CancellationToken ct = default)
    {
        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Queue);
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<ReplayOutcome>.Fail(HandlerRunner.ConnectionNotFound);
        }

        ReplayOutcome outcome;
        try
        {
            outcome = await operations.ReplayDeadLettersAsync(secret, cmd.Vhost, cmd.Queue, cmd.Request, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlyRabbitError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult<ReplayOutcome>.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive,
            succeeded: outcome.StoppedReason is null, Describe(outcome, Math.Max(1, cmd.Request.PoisonThreshold)));
        return PluginResult<ReplayOutcome>.Ok(outcome);
    }

    public static string Describe(ReplayOutcome outcome, int poisonThreshold)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{outcome.Replayed:N0} replayed · {outcome.SkippedNoOrigin:N0} skipped (no origin) · {outcome.SkippedPoison:N0} skipped (poison, ≥{poisonThreshold} deaths)");
        return outcome.StoppedReason is { } reason
            ? string.Create(CultureInfo.InvariantCulture, $"{text} · stopped at message {outcome.StoppedAtIndex}: {reason}")
            : text;
    }
}
```

Register in `RabbitMqPlugin.ConfigureServices`:

```csharp
        services.AddScoped<DeadLetters.ListDeadLettersQueryHandler>();
        services.AddScoped<DeadLetters.ReplayDeadLettersCommandHandler>();
```

and in `RabbitPageTestBase`:

```csharp
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.DeadLetters.ListDeadLettersQueryHandler>();
        Services.AddSingleton<SbConsole.Plugins.RabbitMq.DeadLetters.ReplayDeadLettersCommandHandler>();
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/DeadLetters src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs tests/SbConsole.Plugins.RabbitMq.Tests/Handlers/DeadLetterHandlerTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Components/RabbitPageTestBase.cs
git commit -m "feat(rabbitmq): cross-connection dead-letter overview and replay command

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Dead-letter page, replay dialog, nav badge move, contribution

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/DeadLetters.razor` (route `/p/rabbitmq/dead-letter`)
- Create: `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ReplayDialog.razor`
- Modify: `src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs` (nav items, badge href, contribution)
- Test: create `tests/SbConsole.Plugins.RabbitMq.Tests/Pages/DeadLettersPageTests.cs`, `Pages/ReplayDialogTests.cs`; modify `RabbitMqPluginTests.cs`, `RabbitMqPluginDashboardTests.cs`

**Interfaces:**
- Consumes: `ListDeadLettersQueryHandler`, `DeadLetterSnapshot`, `DeadLetterQueueRow`, `ReplayDeadLettersCommandHandler`, `ReplayDeadLettersCommand`, `ReplayDeadLettersCommandHandler.Describe` (Task 10); `RabbitMqPlugin.BuildQueueDetailLink`.
- Produces: CSS hooks on the page `.dead-letter-summary`, `.show-empty-dlqs`, `button.refresh-dead-letters`, `.dead-letter-connection-error`, `.dlq-row`, `.dlq-connection`, `.dlq-link`, `.dlq-ready`, `.dlq-unacked`, `.dlq-sources`, `.dlq-dlx`, `a.browse-dlq`, `button.replay-dlq`, `.dead-letter-empty`; `ReplayDialog` parameters `Connection` (`ConnectionInfo`), `Vhost`, `Queue`, `Ready` (long); dialog hooks `.replay-count` (`MudSelect<int>`), `.replay-threshold`, `button.replay-submit`, `.replay-result`, `.replay-stopped`, `.replay-error`, `button.replay-close`.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ReplayDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class ReplayDialogTests : RabbitPageTestBase
{
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync(bool prod = false, long ready = 214)
    {
        var cut = RenderDialog<ReplayDialog>(new Dictionary<string, object?>
        {
            [nameof(ReplayDialog.Connection)] = prod ? Prod : Dev,
            [nameof(ReplayDialog.Vhost)] = "/orders",
            [nameof(ReplayDialog.Queue)] = "payments-dlq",
            [nameof(ReplayDialog.Ready)] = ready,
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task SelectCountAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int value)
    {
        var select = cut.FindComponents<MudSelect<int>>().Single(s => (s.Instance.Class ?? "").Split(' ').Contains("replay-count"));
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    [Fact]
    public async Task Replay_confirms_with_the_count_and_the_server_side_prod_flag_then_shows_the_outcome()
    {
        Confirmation.ConfirmAsync("Replay", "payments-dlq", true, 100, Arg.Any<CancellationToken>()).Returns(true);
        Operations.ReplayDeadLettersAsync(ProdSecret, "/orders", "payments-dlq", new ReplayRequest(100, 5), Arg.Any<CancellationToken>())
            .Returns(new ReplayOutcome(97, 2, 1, null, null));
        var cut = await RenderAsync(prod: true);

        await SelectCountAsync(cut, 100);
        await cut.Find("button.replay-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".replay-result").TextContent.Should().Contain("97 replayed · 2 skipped (no origin) · 1 skipped (poison, ≥5 deaths)");
        cut.FindAll("button.replay-submit").Should().BeEmpty();
    }

    [Fact]
    public async Task All_is_capped_at_1000_and_the_threshold_is_sent()
    {
        Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(true);
        Operations.ReplayDeadLettersAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(new ReplayOutcome(0, 0, 0, null, null));
        var cut = await RenderAsync(ready: 5000);

        await SelectCountAsync(cut, ReplayDialog.All);
        await cut.Find(".replay-threshold input").InputAsync(new ChangeEventArgs { Value = "3" });
        await cut.Find("button.replay-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).ReplayDeadLettersAsync(DevSecret, "/orders", "payments-dlq", new ReplayRequest(1000, 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stopped_run_shows_where_and_why()
    {
        Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(true);
        Operations.ReplayDeadLettersAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ReplayOutcome(3, 0, 0, "billing.direct/payment.capture is unroutable — the broker returned the message", 4));
        var cut = await RenderAsync();

        await cut.Find("button.replay-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".replay-stopped").TextContent.Should().Contain("Stopped at message 4: billing.direct/payment.capture is unroutable");
    }

    [Fact]
    public async Task Declining_the_confirmation_replays_nothing()
    {
        Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(false);
        var cut = await RenderAsync();

        await cut.Find("button.replay-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().ReplayDeadLettersAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_bad_threshold_blocks_submit()
    {
        var cut = await RenderAsync();

        await cut.Find(".replay-threshold input").InputAsync(new ChangeEventArgs { Value = "0" });

        cut.Find("button.replay-submit").HasAttribute("disabled").Should().BeTrue();
    }
}
```

`tests/SbConsole.Plugins.RabbitMq.Tests/Pages/DeadLettersPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class DeadLettersPageTests : RabbitPageTestBase
{
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    private static QueueSummary Q(string name, long ready, string? dlx = null, string? dlrk = null, long unacked = 0) =>
        SeededTopology.Queue(name, dlx) with { Ready = ready, Unacked = unacked, DeadLetterRoutingKey = dlrk };

    private void SeedDev(long dlqReady = 10)
    {
        Operations.ListNodesAsync(DevSecret, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListQueuesAsync(DevSecret, null, Arg.Any<CancellationToken>()).Returns([
            Q("billing.payments.q", 0, "billing.retry.dlx", "dlq.payments"),
            Q("payments-dlq", dlqReady, unacked: 2),
            Q("empty-dlq", 0),
            Q("work.q", 0, "work.dlx", "dead"),
        ]);
        Operations.ListBindingsAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns([
            SeededTopology.Binding("billing.retry.dlx", "payments-dlq", "dlq.payments"),
            SeededTopology.Binding("work.dlx", "empty-dlq", "dead"),
        ]);
        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns([
            SeededTopology.Exchange("billing.retry.dlx", "direct"),
            SeededTopology.Exchange("work.dlx", "direct"),
        ]);
    }

    private void ProdFails() =>
        Operations.ListNodesAsync(ProdSecret, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(401, "GET", "/api/nodes", null));

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<DeadLetters>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Lists_dlqs_with_ready_and_unacked_apart_sources_and_links()
    {
        SeedDev();
        Operations.ListQueuesAsync(ProdSecret, null, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListNodesAsync(ProdSecret, Arg.Any<CancellationToken>()).Returns([]);
        var cut = await RenderPageAsync();

        var row = cut.Find(".dlq-row");
        row.QuerySelector(".dlq-connection")!.TextContent.Should().Contain("rabbit-dev");
        row.QuerySelector(".dlq-ready")!.TextContent.Should().Be("10");
        row.QuerySelector(".dlq-unacked")!.TextContent.Should().Be("2");
        row.QuerySelector(".dlq-sources")!.TextContent.Should().Contain("billing.payments.q");
        row.QuerySelector(".dlq-dlx")!.TextContent.Should().Contain("billing.retry.dlx");
        row.QuerySelector(".dlq-link")!.GetAttribute("href").Should().Be($"/p/rabbitmq/queues/payments-dlq?connectionId={Dev.Id}&vhost=%2Forders");
        row.QuerySelector("a.browse-dlq")!.GetAttribute("href").Should().Be($"/p/rabbitmq/queues/payments-dlq/get?connectionId={Dev.Id}&vhost=%2Forders");
        cut.Find(".dead-letter-summary").TextContent.Should().Contain("1 DLQ · 10 ready across 2 RabbitMQ connections");
    }

    [Fact]
    public async Task Empty_dlqs_are_hidden_until_the_switch_is_on()
    {
        SeedDev();
        ProdFails();
        var cut = await RenderPageAsync();
        cut.FindAll(".dlq-row").Should().ContainSingle();

        await cut.Find(".show-empty-dlqs input").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.FindAll(".dlq-row").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failing_connection_is_one_error_line_and_the_others_render()
    {
        SeedDev();
        ProdFails();
        var cut = await RenderPageAsync();

        cut.Find(".dead-letter-connection-error").TextContent.Should().Contain("rabbit-uk-prod").And.Contain("Management API rejected the credentials");
        cut.FindAll(".dlq-row").Should().ContainSingle();
    }

    [Fact]
    public async Task No_backlog_anywhere_shows_the_empty_state()
    {
        SeedDev(dlqReady: 0);
        Operations.ListQueuesAsync(ProdSecret, null, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListNodesAsync(ProdSecret, Arg.Any<CancellationToken>()).Returns([]);
        var cut = await RenderPageAsync();

        cut.Find(".dead-letter-empty").TextContent.Should().Contain("No dead-letter queues hold messages across 2 RabbitMQ connections.");
    }

    [Fact]
    public async Task Replay_opens_the_dialog_with_the_server_side_connection()
    {
        SeedDev();
        ProdFails();
        var dialogs = Substitute.For<MudBlazor.IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await cut.Find("button.replay-dlq").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<ReplayDialog>(
            "Replay — payments-dlq",
            Arg.Is<MudBlazor.DialogParameters>(p =>
                ((SbConsole.Sdk.ConnectionInfo)p[nameof(ReplayDialog.Connection)]!).Id == Dev.Id
                && (string)p[nameof(ReplayDialog.Queue)]! == "payments-dlq"
                && (long)p[nameof(ReplayDialog.Ready)]! == 10),
            Arg.Any<MudBlazor.DialogOptions>());
    }
}
```

In `RabbitMqPluginTests.Declares_the_nav_items_in_order`, expect:

```csharp
            ("Overview", "/p/rabbitmq/overview"),
            ("Exchanges", "/p/rabbitmq/exchanges"),
            ("Queues", "/p/rabbitmq/queues"),
            ("Dead-letter", "/p/rabbitmq/dead-letter"),
            ("Shovels & policies", "/p/rabbitmq/shovels"),
            ("Connections", "/p/rabbitmq/connections"));
```

In `RabbitMqPluginDashboardTests.cs`: replace `"/p/rabbitmq/queues"` with `"/p/rabbitmq/dead-letter"` in the three `ComputeNavBadge` assertions (lines ~271, ~277, ~285), add

```csharp
    [Fact]
    public void The_queues_nav_item_no_longer_carries_the_dead_letter_badge()
    {
        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/queues", Snapshot(ordersReady: 100)).Should().BeNull();
    }
```

(using the file's existing `Snapshot(...)` helper — check its parameters and pass one that yields a DLQ backlog; the line-271 test's arrangement is the one to copy), and change `Contribution_counts_six_pages_and_thirteen_actions` to

```csharp
    public void Contribution_counts_eight_pages_and_seventeen_actions()
    {
        new RabbitMqPlugin().Contribution.Should().Be(new PluginContribution(PageCount: 8, ActionCount: 17));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests --filter "FullyQualifiedName~DeadLettersPageTests|FullyQualifiedName~ReplayDialogTests|FullyQualifiedName~RabbitMqPlugin"`
Expected: build FAILS — `DeadLetters` page / `ReplayDialog` not found.

- [ ] **Step 3: Implement `ReplayDialog.razor`**

```razor
@using System.Globalization
@using SbConsole.Plugins.RabbitMq.Client
@using SbConsole.Plugins.RabbitMq.DeadLetters
@inject ReplayDeadLettersCommandHandler ReplayHandler
@inject IConfirmationService Confirmation

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-3">
            Each message is republished to the exchange and routing key it was first dead-lettered from, then removed from
            <span style="font-family:monospace">@Queue</span>. Messages with no dead-letter history are left in place.
            Skipped messages return marked redelivered. At-least-once: a connection drop can duplicate one message.
        </MudText>
        <div class="d-flex gap-3 align-start">
            <MudSelect T="int" Class="replay-count" @bind-Value="_count" Label="Messages" Disabled="@(_outcome is not null || _busy)" Style="max-width:200px">
                <MudSelectItem Value="10">10</MudSelectItem>
                <MudSelectItem Value="100">100</MudSelectItem>
                <MudSelectItem Value="1000">1,000</MudSelectItem>
                <MudSelectItem Value="All">All (@RabbitFormat.Count(Ready))</MudSelectItem>
            </MudSelect>
            <MudTextField T="string" Class="replay-threshold" @bind-Value="_threshold" Immediate="true" Label="Poison threshold"
                          Disabled="@(_outcome is not null || _busy)" Style="max-width:200px"
                          Error="@(!ThresholdValid)" ErrorText="A whole number of 1 or more"
                          HelperText="Skip messages that have already died this many times from their origin — replaying them feeds the loop" />
        </div>
        @if (_count == All && Ready > DeadLetterMax)
        {
            <MudText Typo="Typo.caption" Class="mud-text-secondary d-block">Replays at most 1,000 per run.</MudText>
        }

        @if (_busy)
        {
            <MudProgressLinear Class="mt-3" Color="Color.Primary" Indeterminate="true" />
        }
        @if (_outcome is { } outcome)
        {
            <MudAlert Class="replay-result mt-3" Severity="@(outcome.StoppedReason is null ? Severity.Success : Severity.Warning)" Dense="true">
                @ReplayDeadLettersCommandHandler.Describe(outcome with { StoppedReason = null }, ThresholdValue)
            </MudAlert>
            @if (outcome.StoppedReason is { } reason)
            {
                <MudText Class="replay-stopped mud-error-text d-block mt-1" Typo="Typo.body2">
                    Stopped at message @outcome.StoppedAtIndex?.ToString(CultureInfo.InvariantCulture): @reason
                </MudText>
            }
        }
        @if (_error is not null)
        {
            <MudAlert Class="replay-error mt-3" Severity="Severity.Error" Dense="true">@_error</MudAlert>
        }
    </DialogContent>
    <DialogActions>
        <MudText Typo="Typo.caption" Class="mud-text-secondary mr-auto ml-4">Logged to audit</MudText>
        @if (_outcome is null)
        {
            <MudButton OnClick="Cancel" Disabled="_busy">Cancel</MudButton>
            <MudButton Class="replay-submit" Color="Color.Error" Variant="Variant.Filled" Disabled="@(_busy || !ThresholdValid || Ready == 0)" OnClick="ReplayAsync">Replay</MudButton>
        }
        else
        {
            <MudButton Class="replay-close" Color="Color.Primary" OnClick="Close">Close</MudButton>
        }
    </DialogActions>
</MudDialog>

@code {
    /// <summary>The "All" choice in the count select.</summary>
    public const int All = -1;

    private const int DeadLetterMax = 1000;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    /// <summary>The server-side ConnectionInfo (from IConnectionProvider) -- the source of IsProd.</summary>
    [Parameter] public ConnectionInfo Connection { get; set; } = default!;
    [Parameter] public string Vhost { get; set; } = "/";
    [Parameter] public string Queue { get; set; } = "";
    [Parameter] public long Ready { get; set; }

    private int _count = 100;
    private string? _threshold = "5";
    private bool _busy;
    private ReplayOutcome? _outcome;
    private string? _error;

    private bool ThresholdValid => int.TryParse(_threshold, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t >= 1;
    private int ThresholdValue => ThresholdValid ? int.Parse(_threshold!, NumberStyles.None, CultureInfo.InvariantCulture) : 5;

    private int RequestedCount => _count == All ? (int)Math.Min(Ready, DeadLetterMax) : _count;

    private async Task ReplayAsync()
    {
        if (!ThresholdValid)
        {
            return;
        }

        if (!await Confirmation.ConfirmAsync("Replay", Queue, Connection.IsProd, RequestedCount))
        {
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            var result = await ReplayHandler.HandleAsync(new ReplayDeadLettersCommand(
                Connection.Id, Connection.Name, Vhost, Queue, new ReplayRequest(RequestedCount, ThresholdValue)));
            if (result.IsSuccess)
            {
                _outcome = result.Value;
            }
            else
            {
                _error = result.Error;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => MudDialog.Cancel();

    private void Close() => MudDialog.Close(DialogResult.Ok(true));
}
```

The count select test uses `ReplayDialog.All` (public const) — that's why it's public. For `_count == 100` with Ready 214 the confirmation count is 100, matching the first test.

- [ ] **Step 4: Implement `DeadLetters.razor`**

```razor
@page "/p/rabbitmq/dead-letter"
@using System.Globalization
@using SbConsole.Plugins.RabbitMq.Client
@using SbConsole.Plugins.RabbitMq.DeadLetters
@inject ListDeadLettersQueryHandler ListHandler
@inject IDialogService DialogService

<PageTitle>RabbitMQ dead-letter</PageTitle>
<div class="d-flex align-center">
    <h1>Dead-letter</h1>
    <MudSpacer />
    <MudButton Class="refresh-dead-letters" Variant="Variant.Outlined" Size="Size.Small" StartIcon="@Icons.Material.Filled.Refresh"
               Disabled="_loading" OnClick="LoadAsync">Refresh</MudButton>
</div>
<MudText Typo="Typo.body2" Class="mud-text-secondary mb-3">
    A dead-letter queue is a queue some other queue's dead-letter exchange routes to. Every RabbitMQ connection, every vhost.
</MudText>

@if (_snapshot is null)
{
    <div class="dead-letters-loading">
        @for (var i = 0; i < 3; i++) { <MudSkeleton Class="my-2" /> }
    </div>
}
else
{
    var snapshot = _snapshot;
    var visible = snapshot.Queues.Where(r => _showEmpty || r.Queue.Ready > 0).ToList();
    var withBacklog = snapshot.Queues.Where(r => r.Queue.Ready > 0).ToList();

    @foreach (var error in snapshot.Errors)
    {
        <MudAlert Class="dead-letter-connection-error mb-2" Severity="Severity.Warning" Dense="true">
            <strong>@error.Connection.Name</strong>: @error.Error
        </MudAlert>
    }

    <div class="d-flex align-center gap-3 my-2">
        <MudText Class="dead-letter-summary mud-text-secondary" Typo="Typo.body2">
            @Plural(withBacklog.Count, "DLQ") · @RabbitFormat.Count(withBacklog.Sum(r => r.Queue.Ready)) ready across @Plural(snapshot.ConnectionCount, "RabbitMQ connection")
        </MudText>
        <MudSpacer />
        <MudSwitch T="bool" Class="show-empty-dlqs" @bind-Value="_showEmpty" Color="Color.Primary" Label="Show empty DLQs" />
    </div>

    @if (visible.Count == 0)
    {
        <MudPaper Class="dead-letter-empty pa-6 my-2" Outlined="true">
            <MudText Typo="Typo.subtitle1">No dead-letter queues hold messages across @Plural(snapshot.ConnectionCount, "RabbitMQ connection").</MudText>
        </MudPaper>
    }
    else
    {
        <MudSimpleTable Dense="true" Elevation="0">
            <thead>
                <tr><th>Connection</th><th>Vhost</th><th>DLQ</th><th style="text-align:right">Ready</th><th style="text-align:right">Unacked</th><th>Sources</th><th>DLX</th><th></th></tr>
            </thead>
            <tbody>
                @foreach (var row in visible)
                {
                    <tr class="dlq-row">
                        <td class="dlq-connection">
                            @row.Connection.Name
                            @if (row.Connection.IsProd)
                            {
                                <MudChip T="string" Size="Size.Small" Color="Color.Error" Variant="Variant.Outlined">prod</MudChip>
                            }
                        </td>
                        <td style="font-family:monospace">@RabbitFormat.VhostLabel(row.Vhost)</td>
                        <td><a class="dlq-link" href="@RabbitMqPlugin.BuildQueueDetailLink(row.Connection.Id, row.Queue)" style="font-family:monospace">@row.Queue.Name</a></td>
                        <td style="text-align:right;font-family:monospace"><span class="dlq-ready">@RabbitFormat.Count(row.Queue.Ready)</span></td>
                        <td style="text-align:right;font-family:monospace"><span class="dlq-unacked">@RabbitFormat.Count(row.Queue.Unacked)</span></td>
                        <td class="dlq-sources" style="font-family:monospace">@SourcesText(row.Sources)</td>
                        <td>
                            @foreach (var dlx in row.DeadLetterExchanges)
                            {
                                <MudChip T="string" Class="dlq-dlx" Size="Size.Small" Variant="Variant.Outlined">@dlx</MudChip>
                            }
                        </td>
                        <td style="white-space:nowrap">
                            <MudButton Class="browse-dlq" Size="Size.Small" Variant="Variant.Text" Href="@BrowseLink(row)">Browse</MudButton>
                            <MudButton Class="replay-dlq" Size="Size.Small" Variant="Variant.Text" Color="Color.Error"
                                       Disabled="@(row.Queue.Ready == 0)" OnClick="@(() => ReplayAsync(row))">Replay…</MudButton>
                        </td>
                    </tr>
                }
            </tbody>
        </MudSimpleTable>
    }
}

@code {
    private DeadLetterSnapshot? _snapshot;
    private bool _loading;
    private bool _showEmpty;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _snapshot = await ListHandler.HandleAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private static string Plural(int n, string noun) => $"{RabbitFormat.Count(n)} {noun}{(n == 1 ? "" : "s")}";

    private static string SourcesText(IReadOnlyList<string> sources) =>
        sources.Count <= 3
            ? string.Join(", ", sources)
            : $"{string.Join(", ", sources.Take(3))} +{(sources.Count - 3).ToString(CultureInfo.InvariantCulture)} more";

    private static string BrowseLink(DeadLetterQueueRow row) =>
        $"/p/rabbitmq/queues/{Uri.EscapeDataString(row.Queue.Name)}/get?connectionId={row.Connection.Id}&vhost={Uri.EscapeDataString(row.Vhost)}";

    private async Task ReplayAsync(DeadLetterQueueRow row)
    {
        var parameters = new DialogParameters<ReplayDialog>
        {
            // The ConnectionInfo came from IConnectionProvider.ListAsync server-side -- the source of IsProd.
            { x => x.Connection, row.Connection },
            { x => x.Vhost, row.Vhost },
            { x => x.Queue, row.Queue.Name },
            { x => x.Ready, row.Queue.Ready },
        };
        var dialog = await DialogService.ShowAsync<ReplayDialog>($"Replay — {row.Queue.Name}", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadAsync();
        }
    }
}
```

`MudButton` with `Href` renders an `<a>` carrying the `browse-dlq` class, which is what `a.browse-dlq` in the test selects. The `@page` class is `SbConsole.Plugins.RabbitMq.Pages.DeadLetters`; inside it, the namespace `SbConsole.Plugins.RabbitMq.DeadLetters` is only referenced through `@using` (types `DeadLetterSnapshot` etc.), the same arrangement `Queues.razor` has with the `…Queues` namespace.

- [ ] **Step 5: Nav, badge and contribution in `RabbitMqPlugin.cs`**

1. `NavItems`:
   ```csharp
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Overview", "/p/rabbitmq/overview"),
        new("Exchanges", "/p/rabbitmq/exchanges"),
        new("Queues", "/p/rabbitmq/queues"),
        new("Dead-letter", DeadLetterNavHref),
        new("Shovels & policies", "/p/rabbitmq/shovels"),
        new("Connections", "/p/rabbitmq/connections"),
    ];
   ```
2. Replace the comment and value of `Contribution`:
   ```csharp
    // Pages: Overview, Exchanges, Queues, QueueDetail, GetMessages, ShovelsPolicies, BrokerConnections,
    // DeadLetters (8). Actions (one per command handler): Create/Delete exchange, Create/Delete/Purge
    // queue, Add/Remove binding, Create/Delete/Restart shovel, Publish, Republish (also backs the Get
    // messages page's Requeue), Consume, Put/Delete policy, Close connection, Replay dead letters (17).
    // Dialogs are not counted as pages.
    public PluginContribution Contribution => new(PageCount: 8, ActionCount: 17);
   ```
3. Add `private const string DeadLetterNavHref = "/p/rabbitmq/dead-letter";` next to `OverviewNavHref`, and in `ComputeNavBadge` replace `QueuesNavHref => DeadLetteredTotal(snapshot),` with `DeadLetterNavHref => DeadLetteredTotal(snapshot),`.
4. In `GetNavBadgeAsync`, replace `navItemHref is OverviewNavHref or QueuesNavHref` with `navItemHref is OverviewNavHref or DeadLetterNavHref`.
5. `QueuesNavHref` is still used by `BuildQueueDetailLink` — keep it.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/SbConsole.Plugins.RabbitMq.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.RabbitMq/Pages/DeadLetters.razor src/Plugins/SbConsole.Plugins.RabbitMq/Pages/ReplayDialog.razor src/Plugins/SbConsole.Plugins.RabbitMq/RabbitMqPlugin.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/DeadLettersPageTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/Pages/ReplayDialogTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/RabbitMqPluginTests.cs tests/SbConsole.Plugins.RabbitMq.Tests/RabbitMqPluginDashboardTests.cs
git commit -m "feat(rabbitmq): dead-letter page with safe bulk replay; DLQ badge moves to it

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Local stack, docs, full gate, manual verification

**Files:**
- Modify: `docker/rabbitmq/definitions.json`
- Modify: `docker/README.md` (RabbitMQ section)
- Modify: `docs/design.md` (§6.8 addendum)
- Modify: `docs/superpowers/specs/2026-09-26-rabbitmq-phase2-design.md` (deviations)
- Modify: `CLAUDE.md` only if a statement there became false (it lists plugins, not pages — expected: no change)

- [ ] **Step 1: Seed an exchange-to-exchange binding**

In `docker/rabbitmq/definitions.json`, append to the `bindings` array:

```json
    {
      "source": "order-events",
      "vhost": "/orders",
      "destination": "audit.fanout",
      "destination_type": "exchange",
      "routing_key": "order.#",
      "arguments": {}
    }
```

Validate: `python3 -c "import json; json.load(open('docker/rabbitmq/definitions.json'))"` → no output.

In `docker/README.md`'s RabbitMQ topology paragraph, after the sentence listing the exchanges, add: "`order-events` is also bound to `audit.fanout` (key `order.#`) — an exchange-to-exchange binding, so the Publish preview shows a two-hop path."

- [ ] **Step 2: Record the design**

Append to `docs/design.md`, directly after the §6.8 "**Local stack:**" paragraph:

```markdown
**Phase 2 (2026-09-26).** Spec: `docs/superpowers/specs/2026-09-26-rabbitmq-phase2-design.md`; plan:
`docs/superpowers/plans/2026-09-26-rabbitmq-phase2.md`. Four additions:
- *Dead-letter page* (`/p/rabbitmq/dead-letter`): every DLQ on every RabbitMQ connection, read fresh
  (not through the 60 s hook cache, so a replay's effect shows at once). The DLQ nav badge moved here
  from Queues. **Replay** moves messages back to where they were first dead-lettered from: the origin
  is the `x-death` record matching `x-first-death-queue`/`-exchange` (x-death is most-recent-first,
  so never "the first entry"). Each message is acked only after its republished copy is confirmed;
  skipped messages (no history, or ≥ the poison threshold of deaths) are held unacked so `basic.get`
  moves past them and are requeued at the end on every exit path. Nothing can be lost; a connection
  drop between confirm and ack can duplicate one message (at-least-once). Capped at 1,000 per run
  because skipped messages are held.
- *Policies*: create/edit/delete (`PUT`/`DELETE /api/policies/{vhost}/{name}`), typed fields for the
  common keys plus an extra-keys JSON object; edit round-trips unknown keys untouched.
- *Connections & channels* (`/p/rabbitmq/connections`): force-close sends `X-Reason: Closed from
  SbConsole`. Closing another user's connection needs the administrator tag — the seeded `sbconsole`
  user can't. Direct (shovel/federation) connections can't be closed over HTTP and are marked internal.
- *Exchange-to-exchange bindings*: bind/unbind from the Exchanges page; `RoutingMatcher.ResolveDeep`
  follows exchange hops (cycle-safe, 8 hops) for the Publish preview and `DeadLetterTopology`.
```

In the phase-2 spec, add a section at the end:

```markdown
## 10. Deviations recorded during planning (2026-09-26)

1. §5: new `AddExchangeBindingAsync`/`RemoveExchangeBindingAsync` operations instead of changing the
   existing binding signatures; the commands carry a `DestinationType`.
2. §5: Unbind from the Exchanges dialog confirms through `IConfirmationService` with the connection's
   `IsProd` — exactly what queue detail's Unbind already does (typed on prod), not two-button everywhere.
3. §5: the Exchanges "Bindings" count and "no bindings" warning stay outgoing-only — an exchange that
   only receives from another exchange and binds nowhere still drops messages.
4. §2: the Dead-letter page reads snapshots uncached (`RabbitMqPlugin.FetchSnapshotAsync`).
5. §4: Direct (shovel/federation) connections are listed with an "internal" chip and can't be closed.
6. §3: the dead-letter-exchange and alternate-exchange inputs are free-text fields, not pickers.
7. §4: the close reason is fixed (`Closed from SbConsole`) rather than edited per close.
8. Page classes are `BrokerConnections` and `DeadLetters` (routes unchanged) to avoid clashing with
   the `Connections` handler namespace.
```

- [ ] **Step 3: Full gate**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

Run: `dotnet test`
Expected: every test project passes (Core, Web, and all plugin test projects — nothing outside the RabbitMQ plugin should have changed behaviour).

- [ ] **Step 4: Manual verification against the local broker**

```bash
docker compose --profile rabbitmq down
docker compose --profile rabbitmq up -d
```

(`down` without `-v` keeps nothing RabbitMQ-specific — the broker has no volume — and re-imports `definitions.json` on start.) Restart the app (`dotnet run --project src/SbConsole.Web --launch-profile http`, or the `sbconsole-web` preview config), sign in, and with the `local-rabbitmq` connection, vhost `/orders`:

1. Exchanges → `order-events` → Bindings: "Routes to — 4" includes `audit.fanout` (type exchange); open `audit.fanout`'s Bindings: "Receives from — 1" lists `order-events`.
2. Exchanges → `order-events` → Publish with key `order.uk.created`: the preview lists `order-events.q` and `audit.sink`; `audit.sink` shows no path (direct `#` binding wins first); publish succeeds.
3. Shovels & policies → Policies → New policy `phase2-test`, pattern `^legacy\.`, apply to queues, `max-length` 1000: preview "Matches 1 queue · 0 exchanges"; save; edit it (prefilled, name locked, warning shows); delete it (confirmation) — each appears in the Audit page.
4. Connections: the two shovel connections show "internal" with Close disabled. Leave the page open, run a Peek on Queues → `payments-dlq` in another tab, and see (on refresh) a short-lived "this console" connection if it's still open; Close on a non-internal connection as `sbconsole` shows the administrator-tag message (expected — the seeded user lacks the tag).
5. Dead-letter: `payments-dlq` is listed with ready 10 (or the seeded count), sources `billing.payments.q`, `order-events.q`. Replay… → 10 → Replay → confirm. Expected outcome: the seeded payments carry x-death with origin `billing.direct` / `payment.capture` (or `refund.issue`), so they are replayed into `billing.payments.q`; if that queue's TTL expires them again they return to the DLQ with a higher count, and a second replay with threshold 1 skips them as poison. Record the actual outcome in the task report.
6. The nav badge on "Dead-letter" shows the DLQ backlog; "Queues" shows none.

If any step disagrees with the expectation, stop and report it (do not "fix" the expectation).

- [ ] **Step 5: Commit**

```bash
git add docker/rabbitmq/definitions.json docker/README.md docs/design.md docs/superpowers/specs/2026-09-26-rabbitmq-phase2-design.md docs/superpowers/plans/2026-09-26-rabbitmq-phase2.md
git commit -m "docs(rabbitmq): record phase 2; seed an exchange-to-exchange binding

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
