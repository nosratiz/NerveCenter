# RabbitMQ plugin — Design

Status: draft, 2026-09-25.

## 1. Context

SbConsole's fourth plugin, `SbConsole.Plugins.RabbitMq`, for RabbitMQ 3.12+ brokers. Source
design material: `~/Desktop/UI mockups for NerveCenter/SbConsole RabbitMQ.dc.html`, eight frames
`1a`–`1h`:

| Frame | Screen | Built as |
|---|---|---|
| 1a | Connection form — idle / testing / success / split failure | `RabbitConnectionFields` + `TestConnectionAsync` checks |
| 1b | Broker overview — nodes, alarms, rates | `Pages/Overview.razor` |
| 1c | Exchanges — type, bindings, rate | `Pages/Exchanges.razor` (+ create/bindings dialogs) |
| 1d | Queues — ready / unacked split | `Pages/Queues.razor` (+ create dialog) |
| 1e | Queue detail — routes in, route out | `Pages/QueueDetail.razor` (+ add-binding dialog) |
| 1f | Get messages — peek / consume | `Pages/GetMessages.razor` (+ republish dialog) |
| 1g | Publish with routing preview; destructive purge | `Pages/PublishDialog.razor`; purge via `IConfirmationService` |
| 1h | Shovels & policies | `Pages/ShovelsPolicies.razor` (+ new-shovel dialog) |

Unlike the AWS plugin (built over three plans), the whole mockup surface is built in one plan:
RabbitMQ's surface is read-mostly over one HTTP API, and every frame shares the same
connection/vhost picker and the same management-API error model, so slicing it would mostly
re-plan the same plumbing.

The three RabbitMQ-specific decisions the mockup's introduction calls out are load-bearing here:

1. **Two endpoints, two results.** AMQP moves messages; the management HTTP API is the only
   source of rates, depths, bindings, nodes, shovels and policies. Either can succeed while the
   other fails (most commonly: AMQP open, management plugin not enabled). Test connection reports
   them as separate `ConnectionCheck`s, and every page degrades by endpoint, not all-or-nothing.
2. **Ready / Unacked are never summed.** Ready backlog = throughput problem; unacked backlog =
   consumer problem. Every table and tile keeps them in separate columns.
3. **Vhost sits in the page picker beside the connection**, not in host chrome. The connection's
   configured vhost is the default selection.

Dead-lettering is a routing convention in RabbitMQ, not a broker object: a "DLQ" is an ordinary
queue bound to an exchange that some other queue names in `x-dead-letter-exchange` (§6).

**Client libraries:** `RabbitMQ.Client` 7.x (AMQP 0-9-1, async API) for AMQP; plain `HttpClient`
+ `System.Text.Json` for the management API (no third-party management client — the API is small
and stable). Both only in the plugin project, same isolation rule as the other plugins.

## 2. Connection model

One opaque secret string, the house `key=value;` convention (`RabbitConfigParser`, same
parse/serialize/`SafeEcho` shape as `AwsConfigParser`), **with every value percent-encoded**
(`Uri.EscapeDataString` on serialize, `Uri.UnescapeDataString` on parse). Encoding is new relative
to Kafka/AWS and is required: a RabbitMQ password or vhost can legally contain `;` or `=`, which
would otherwise split the string.

```
host=rabbit-01.uk.internal;amqpPort=5671;managementUrl=https%3A%2F%2Frabbit-01.uk.internal%3A15671;vhost=%2Forders;username=sbconsole;password=...;tls=true;verifyCert=true
```

| Key | Default when absent |
|---|---|
| `host` | required |
| `amqpPort` | `5671` if `tls=true`, else `5672` |
| `managementUrl` | `https://{host}:15671` if `tls=true`, else `http://{host}:15672` |
| `vhost` | `/` |
| `username` / `password` | required |
| `tls` | `false` |
| `verifyCert` | `true` (only meaningful with TLS; applies to both AMQP and management HTTPS) |

`RabbitConnectionSettings.From(string secret)` resolves defaults into one immutable record; every
operation takes the raw secret and builds settings itself (same "no instance pre-configured for
one connection" rule as `ISqsOperations`).

`SafeEcho` allowlists `host`, `amqpPort`, `managementUrl`, `vhost`, `username` — never `password`.
`GetConnectionSummary` returns `{"Host": host, "Vhost": vhost}` for the Connections-list chips.

**Connection form (1a):** `Client/RabbitConnectionFields.razor`, hosted via
`IPlugin.ConnectionFormComponentType` exactly like `AwsConnectionFields` (read `InitialSecret` once
in `OnInitialized`, emit the serialized secret through `SecretChanged` on every field change).
Fields in mockup order: Host, AMQP port, Management URL (with caption "Rates, depths and bindings
are read from here — not over AMQP."), Virtual host, Username, Password (with caption "Stored in
the host secret store, never in the plugin config file."), TLS switch, "Verify certificate chain"
switch (visible only with TLS). The Management URL field shows the computed default as its
placeholder so leaving it blank is obviously safe. With `IsProd` and `tls=false`, a warning:
"Plain AMQP on a prod connection sends credentials unencrypted."

**Test connection (1a's split result)** — `TestConnectionAsync` runs both probes concurrently,
each with its own timeout (10 s):

- AMQP: open a connection + channel on the configured vhost, then close. Check label
  `"AMQP {port}"` plus `" · TLS verified"` / `" · TLS (unverified)"` / `" · plain"`; Detail on
  success is the latency (`"41 ms"`), on failure the friendly error.
- Management: `GET /api/overview` then `GET /api/whoami`. Label `"Management API {port}"`; Detail
  on success the latency.

`Identity` (success only): `"RabbitMQ {version} · vhost {vhost} · {n} exchanges · {m} queues ·
tags {tags}"` when management succeeded; `"vhost {vhost}"` when only AMQP did.

Outcome mapping onto the existing `ConnectionTestResult` shape (no SDK change needed — the host's
`ConnectionEditor` already renders `Checks` with a warning icon per failed check):

- both pass → `Success=true`, two Passed checks.
- one passes → `Success=true`, one Failed check whose Detail explains the consequence
  (management failed: `"{error} — messages will send and receive, but queue depths, rates and
  bindings stay blank until the management plugin is reachable."`; AMQP failed: `"{error} — the
  console can read the broker but cannot publish or get messages."`).
- both fail → `Success=false`, `ErrorMessage` = the AMQP error (credentials failures surface
  there first), checks still attached.

## 3. Plugin shell

```csharp
public sealed class RabbitMqPlugin : IPlugin
{
    Id => "rabbitmq"; ConnectionKind => "rabbitmq";
    DisplayName => "RabbitMQ"; ConnectionKindDisplayName => "RabbitMQ";
    NavItems => [ Overview /p/rabbitmq/overview, Exchanges /p/rabbitmq/exchanges,
                  Queues /p/rabbitmq/queues, Shovels & policies /p/rabbitmq/shovels ];
    ConnectionFormComponentType => typeof(RabbitConnectionFields);
}
```

Registered in `Program.cs` with `AddSbConsolePlugin<RabbitMqPlugin>()` after AWS; referenced from
`SbConsole.Web.csproj`; new projects added to `SbConsole.slnx`. Namespace root
`SbConsole.Plugins.RabbitMq`. Own `PluginResult`/`PluginResult<T>` (copy of the AWS one, routing
through `FriendlyRabbitError`).

## 4. Operations seam

One wrapper interface, `Client/IRabbitOperations`, implemented by `RabbitOperations`, which
composes two internal collaborators so each transport is testable on its own:

- `ManagementApiClient` — `HttpClient` over the management API. Constructed with a
  `Func<RabbitConnectionSettings, HttpMessageHandler>` (default: a cached `SocketsHttpHandler` per
  `(managementUrl, verifyCert)` — never one per call, the pages poll) so unit tests feed canned
  JSON through a fake handler. Basic auth header set per request. Non-2xx →
  `ManagementApiException(int StatusCode, string Method, string Path, string? Reason)`. Vhost and
  names are always `Uri.EscapeDataString`-escaped into path segments (`/` → `%2F`).
- `AmqpClient` — `RabbitMQ.Client` 7.x; a connection per operation (open, do, close), with
  `ClientProvidedName = "sbconsole"`. Test-only surface: none beyond the pure mapping helpers
  (`AmqpMessageMapper`), which are unit-tested; the transport itself is verified manually against
  the docker broker (§10).

```csharp
public interface IRabbitOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    // management API — reads
    Task<IReadOnlyList<string>> ListVhostsAsync(string secret, CancellationToken ct = default);
    Task<BrokerOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<NodeSummary>> ListNodesAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeSummary>> ListExchangesAsync(string secret, string vhost, CancellationToken ct = default);
    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string vhost, CancellationToken ct = default);
    Task<QueueDetails> GetQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);
    Task<IReadOnlyList<BindingInfo>> ListBindingsAsync(string secret, string vhost, CancellationToken ct = default);
    Task<IReadOnlyList<ShovelInfo>> ListShovelsAsync(string secret, string vhost, CancellationToken ct = default);
    Task<IReadOnlyList<PolicyInfo>> ListPoliciesAsync(string secret, string vhost, CancellationToken ct = default);

    // management API — writes
    Task CreateExchangeAsync(string secret, string vhost, CreateExchangeRequest request, CancellationToken ct = default);
    Task DeleteExchangeAsync(string secret, string vhost, string exchange, CancellationToken ct = default);
    Task CreateQueueAsync(string secret, string vhost, CreateQueueRequest request, CancellationToken ct = default);
    Task DeleteQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);
    Task PurgeQueueAsync(string secret, string vhost, string queue, CancellationToken ct = default);
    Task AddBindingAsync(string secret, string vhost, string exchange, string queue, string routingKey, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);
    Task RemoveBindingAsync(string secret, string vhost, string exchange, string queue, string propertiesKey, CancellationToken ct = default);
    Task CreateShovelAsync(string secret, string vhost, CreateShovelRequest request, CancellationToken ct = default);
    Task DeleteShovelAsync(string secret, string vhost, string name, CancellationToken ct = default);
    Task RestartShovelAsync(string secret, string vhost, string name, CancellationToken ct = default);

    // AMQP
    Task<IReadOnlyList<RabbitMessage>> GetMessagesAsync(string secret, string vhost, string queue, int count, GetMode mode, CancellationToken ct = default);
    Task<PublishOutcome> PublishAsync(string secret, string vhost, PublishRequest request, CancellationToken ct = default);
}

public enum GetMode { Peek, Consume }
public enum PublishOutcome { Routed, Unroutable }
```

Domain records live in `Client/` (one file each, like the AWS plugin). Management JSON is
deserialized into private DTOs inside `ManagementApiClient` and mapped to these records there —
DTOs never leave the client. Missing numeric stats (a fresh broker, a queue with no traffic, stats
collection disabled) map to `0` for counts and `null` for rates, and the UI renders a null rate as
`—`, never `0` (the mockup's "Rates need two samples" loading note).

Key record fields (anything not listed is the implementer's call):

- `BrokerOverview`: `ClusterName`, `RabbitVersion`, `ErlangVersion`, `PublishRate?`,
  `DeliverRate?` (`deliver_get`), `AckRate?`, `UnroutableRate?` (`drop_unroutable` +
  `return_unroutable`), `Connections`, `Channels`, `Queues`, `Exchanges`, `Consumers`,
  `MessagesReady`, `MessagesUnacked`, `ConnectionsOpened`/`ConnectionsClosed`/`ChannelsOpened`
  (from `churn_rates` totals — labelled "since node start", not "last hour": the API exposes
  cumulative counts, and inventing an hour window would be a lie).
- `NodeSummary`: `Name`, `Running`, `MemUsed`, `MemLimit`, `MemAlarm`, `DiskFree`,
  `DiskFreeLimit`, `DiskAlarm`, `FdUsed`, `FdTotal`, `Uptime`.
- `ExchangeSummary`: `Name` (`""` = the AMQP default), `Type`, `Durable`, `AutoDelete`,
  `Internal`, `AlternateExchange?` (from `arguments["alternate-exchange"]`), `PublishInRate?`,
  `PublishOutRate?`, `IsBuiltIn` (`""` or `amq.*`).
- `QueueSummary`: `Name`, `Type` (`classic`/`quorum`/`stream`), `Durable`, `AutoDelete`,
  `Exclusive`, `Ready`, `Unacked`, `Consumers`, `PublishRate?`, `AckRate?`, `RedeliverRate?`,
  `DeadLetterExchange?` / `DeadLetterRoutingKey?` (argument first, then effective policy),
  `MessageTtlMs?`, `MaxLength?`, `Overflow?`, `Lazy` (`x-queue-mode=lazy`), `Policy?`,
  `IdleSince?`, `MemoryBytes`, `ReplicaCount?` (quorum `members` count), `Arguments`
  (raw, `IReadOnlyDictionary<string, object?>`).
- `QueueDetails`: `Summary`, `EffectivePolicyDefinition`, `Consumers` (`ConsumerInfo`: `Tag`,
  `ChannelName`, `Prefetch`, `AckRequired`, `Exclusive`), `Bindings` (this queue's, from
  `/api/queues/{vhost}/{name}/bindings`, **including** the implicit default-exchange binding —
  the UI hides it).
- `BindingInfo`: `Source`, `Destination`, `DestinationType` (`queue`/`exchange`), `RoutingKey`,
  `Arguments`, `PropertiesKey`.
- `ShovelInfo`: `Name`, `State` (`running`/`starting`/`terminated`/`unknown`), `Reason?`,
  `SourceQueue?`/`SourceExchange?`, `SourceUri`, `DestinationQueue?`/`DestinationExchange?`,
  `DestinationUri`, `AckMode`, `Timestamp?` — merged from `/api/shovels/vhost/{vhost}` (status)
  and `/api/parameters/shovel/{vhost}` (definition). A shovel with a definition but no status row
  is `State = "starting"`.
- `PolicyInfo`: `Name`, `Pattern`, `ApplyTo`, `Priority`, `Definition`
  (`IReadOnlyDictionary<string, object?>`).
- `RabbitMessage`: `Index` (position in this get, 0-based), `MessageId?`, `CorrelationId?`,
  `Exchange`, `RoutingKey`, `Redelivered`, `ContentType?`, `ContentEncoding?`, `DeliveryMode`
  (1/2), `Priority?`, `AppId?`, `Timestamp?`, `Headers` (strings decoded from AMQP `byte[]`),
  `Body` (`byte[]`), `Deaths` (`DeathRecord`: `Queue`, `Exchange`, `Reason`, `RoutingKeys`,
  `Count`, `Time?` — parsed from the `x-death` header), `FirstDeathQueue?`,
  `FirstDeathExchange?`, `DeliveryCount?` (quorum `x-delivery-count`).
- `PublishRequest`: `Exchange`, `RoutingKey`, `Persistent`, `Priority?`, `ContentType?`,
  `Headers` (`IReadOnlyDictionary<string, string>`), `Body` (`byte[]`), plus optional
  `MessageId?`, `CorrelationId?`, `Timestamp?` (so a republish can carry the original ids).

**Get (1f) is AMQP `basic.get`, not the management API's `/get`.** RabbitMQ has no true peek:
`basic.get` with manual ack takes the message and the console must hand it back. `GetMessagesAsync`
opens one channel, calls `basic.get(autoAck: false)` up to `count` times (stopping early on empty),
then for `Peek` issues one `basic.nack(multiple: true, requeue: true)` on the last delivery tag and
for `Consume` one `basic.ack(multiple: true)`. Cancellation or any exception before the
ack/nack closes the channel, which requeues everything held — so "Cancelling requeues
immediately" (mockup copy) is literally true. Using AMQP rather than the management `/get` is
what makes "AMQP up, management down" still able to get and publish.

**Publish (1g) is AMQP with `mandatory: true` and publisher confirms** (channel created with
`publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true`). A
`basic.return` surfaces in RabbitMQ.Client 7 as a `PublishException` with `IsReturn = true`,
mapped to `PublishOutcome.Unroutable`; the broker is the final authority on routability even when
the preview (§5) said otherwise.

## 5. Routing preview (1g)

`Routing/RoutingMatcher` — pure, fully unit-tested:

```csharp
static RoutePreview Resolve(ExchangeSummary exchange, IReadOnlyList<BindingInfo> sourceBindings,
                            string routingKey, IReadOnlyDictionary<string, string> headers);
record RoutePreview(IReadOnlyList<BindingInfo> Matched, IReadOnlyList<BindingInfo> Closest,
                    bool HasAlternateExchange);
```

- `direct`: binding key == routing key (ordinal).
- `fanout`: every binding.
- `topic`: AMQP topic semantics — words split on `.`, `*` = exactly one word, `#` = zero or more
  words; implemented as a word-level matcher (not a regex built from the pattern).
- `headers`: `x-match` = `all` (default) / `any` / `all-with-x` / `any-with-x`; binding arguments
  starting `x-` ignored unless `*-with-x`; values compared as strings (ordinal).
- The default exchange (`""`): routes to the queue named by the routing key; `sourceBindings` for
  it are synthesized by the caller from the queue list.
- `Closest` (only when `Matched` is empty): up to 3 bindings ranked by shared leading words, then
  Levenshtein distance of the binding key to the routing key.
- Exchange-to-exchange bindings are matched like any other but rendered as "→ exchange X
  (routing continues there)" and not followed.

The Publish dialog blocks the Publish button when the preview matched nothing **and** the
exchange has no alternate exchange (mockup: "caught before send"), with an explicit
"Publish anyway" link for the case where the preview is wrong (bindings changed since load). When
the management API is unavailable the preview area says so and publish is allowed — the
mandatory flag still catches unroutable messages.

## 6. Dead-letter topology

`Routing/DeadLetterTopology.Compute(queues, bindings)` — pure:

- DLX names = every queue's `DeadLetterExchange` (argument or effective policy).
- A queue is a **DLQ** when it is the destination of a binding from any DLX, or when some queue's
  DLX is `""` (default exchange) and its `DeadLetterRoutingKey` names it.
- For each source queue, **route out** = the DLX, the dead-letter routing key (or "original
  routing key" when unset), and the target queues resolved through `RoutingMatcher` (with the
  DLRK when set; otherwise every binding of the DLX that a message *could* match — i.e. fanout
  all, direct/topic all bindings, flagged "depends on the message's routing key").

Used by: the Queues list (`DLQ` attribute chip), queue detail's "Route out — dead-letter" panel,
the Exchanges list (`DLX` attribute chip), the Get page (default republish target = the
message's `x-first-death-exchange` with its original routing key), and the plugin's nav badge /
dashboard hooks (§9).

## 7. Screens

All pages: `@page "/p/rabbitmq/..."`, connection + vhost picker header (`Components/
ConnectionVhostPicker.razor` — connection `MudMenu` with prod chip exactly like AWS `Queues.razor`,
then a vhost `MudMenu` populated from `ListVhostsAsync`, defaulting to the connection's configured
vhost; if listing vhosts fails, the picker shows only the configured vhost). Selected
connection/vhost carried in the query string (`?connectionId=&vhost=`) so detail links and
dashboard problem links are shareable, same rule as AWS detail pages. Theming only through
`--mud-palette-*` / MudBlazor props, never hex. Numbers formatted `N0` invariant, rates `N0` + `/s`,
`null` rates as `—`.

**Management-API error model (shared).** A failed management read on any list page renders an
inline `MudAlert` naming the endpoint, the status and the likely fix, e.g. `403 · management API —
User sbconsole cannot read this vhost (GET /api/exchanges/%2Forders → 403, missing tag:
monitoring)` with **Retry** and **Edit connection** (`/connections`) actions (1c error frame).
When a page already has data and a *refresh* fails, the last good data stays on screen, dimmed
(`opacity: .55`), with "Refresh failed — {error}. Showing values from {HH:mm}, {n}m ago." and
**Retry now** / **Pause auto-refresh** (1d error frame). Loading = MudTable skeleton rows keeping
the column grid, with the endpoint being read as a caption.

**1b Overview** (`/p/rabbitmq/overview`): header line "{n} nodes · cluster {cluster_name} ·
{version}" + "Refreshed {n}s ago" (auto-refresh every 10 s, pausable). Alarm banner per node in
alarm: "Memory alarm on {node} — publishers are blocked while the alarm holds." (disk likewise).
Four tiles: Publish /s (with "5m avg" dropped — no history; caption = total ready), Deliver /s
(caption "ack {n}/s", and "below publish" warning tint when deliver < publish), Connections
(caption "{n} channels"), Unrouted /s (caption "No alternate exchange" style hint only when > 0:
"dropped or returned"). Nodes table: Node, Memory (bar = used / high watermark, value `2.9G`),
Disk free (value, bar tinted when within 2× of the limit), FDs (`used / total`), State chip
(Running / Mem alarm / Disk alarm / Down). Deepest queues in the selected vhost: top 5 by
`Ready + Unacked` shown as two numbers (never summed in display), linking to queue detail. Churn
since node start: connections opened/closed, channels opened.

**1c Exchanges** (`/p/rabbitmq/exchanges`): filter box (substring), type chips All / topic /
direct / fanout / headers, "{n} exchanges", **+ Create exchange** (dialog: name, type, durable,
auto-delete, internal, alternate exchange). Columns: Exchange (default shown as "(AMQP
default)"), Type (topic chip tinted `Color.Primary` filled, others outlined), Attributes
(`durable`, `auto-delete`, `internal`, `built-in`, `DLX`, `AE → x`), Bindings (count of
bindings whose source is this exchange; for the default exchange, the queue count), In /s,
Out /s, Actions: **Publish** (opens the publish dialog pre-selected), **Bindings** (dialog listing
destination, destination type, routing key, arguments), **Delete** (Destructive, disabled for
built-in exchanges, confirmation through `IConfirmationService`). An exchange with zero bindings
and no alternate exchange gets a warning attribute chip "no bindings" and, when its in-rate is
> 0, the whole row tinted — "publishing with zero bindings is silent message loss". Empty state
(only built-ins): "No exchanges declared in {vhost}. Only the built-in AMQP default is present.
Publishers routing by queue name work without one." Built-in `amq.*` exchanges are hidden by a
"Show built-in" switch (default off); the default exchange is always shown.

**1d Queues** (`/p/rabbitmq/queues`): filter box (substring, client-side), summary "{n} queues ·
{ready} ready · {unacked} unacked", **+ Create queue** (dialog: name, type classic/quorum,
durable, auto-delete, dead-letter exchange + routing key, message TTL, max length, overflow),
auto-refresh (10 s default; Off/10s/30s/60s picker persisted in `IPluginStore` like AWS).
Columns: Queue (link to detail), Ready, Unacked, Cons., Ack /s, Redeliver /s, Attributes
(`durable` / `quorum · 3 replicas` / `DLX` / `DLQ` / `TTL 30s` / `lazy` / `max 500,000` /
`policy ha-orders` / `idle 14d`), Actions: **Get**, **Publish** (default exchange, routing key =
queue name), **Purge** (overflow menu with **Delete**). Row warnings: `Ready > 0 && Consumers ==
0` → "no consumers" chip; `RedeliverRate > 0` → "redelivering" chip. *Deviation from the mockup:*
the mockup's "Nack /s" column is "Redeliver /s" — the management API exposes no per-queue
nack/reject rate; `redeliver` is the signal that actually distinguishes "poison message being
redelivered" (the mockup's own rationale for the column). Empty filter: "No queue matches
{filter}. {n} queues in this vhost. Did you mean {closest}?" + **Clear filter**.

**1e Queue detail** (`/p/rabbitmq/queues/{name}` + `?connectionId=&vhost=`, name route-escaped):
breadcrumb "Queues / {name}", actions **Get messages**, **+ Add binding** (dialog: exchange
picker, routing key, headers arguments for headers exchanges), **Purge**. Tiles: Ready, Unacked,
Consumers, Ack /s, Memory. "Routes in — {n} bindings": rows of exchange (link to Exchanges
filtered), type chip, routing key (`(no key)` when empty), **Unbind** (Mutating, confirmed). The
implicit default-exchange binding is not listed (it cannot be removed). *Deviation:* the
mockup's per-binding rate (`1,140/s`) is not shown — the management API has no per-binding
statistics; the source exchange's out-rate would misattribute traffic. "Route out — dead-letter":
"Rejected or expired → {dlx} · {dlrk or 'original routing key'} → {target queues with ready
counts}", with the caption "Set by x-dead-letter-exchange on the queue|by policy {p}, so it is an
argument rather than a binding — shown here anyway because it is where the messages actually go."
When there is no DLX: "No dead-letter route — rejected and expired messages are dropped."
Arguments table: every declared argument plus `durable`, `x-queue-type`; a key overridden by the
effective policy shows the policy value and "policy {name}" beside it (1h note: "A policy
overrides queue arguments silently"). Consumers: tag, channel, prefetch (first 3, "+{n} more"
expander).

**1f Get messages** (`/p/rabbitmq/queues/{name}/get`): Mode select **Peek** / **Consume**, Count
select 1/5/10/25/50/100 (default 25). Byline changes with mode: Peek — "Peek requeues every
message. Nothing is removed, but each message comes back marked redelivered."; Consume — "Consume
acknowledges and removes every message it gets. Selected messages can be requeued or
republished from this page until you leave it." Consume is Destructive: confirmed through
`IConfirmationService` (count = requested count) and audited. Result: "{n} of {ready} shown ·
oldest first"; list rows: message id (or `#index`), timestamp, death summary chip (`rejected ×5`,
`expired`, `maxlen`), routing key; checkbox selection. Selection bar: "{n} selected",
**Requeue** (Consume mode only — republishes to the default exchange with routing key = this
queue; Mutating), **Republish…** (dialog: exchange + routing key, defaulting to the first
message's `x-first-death-exchange` and its first death routing key; Mutating; in Peek mode the
dialog warns "The original stays in the queue — this publishes a copy."). Detail pane for the
focused message: **Copy** body, **Republish to {first-death exchange}** shortcut; Death history
(exchange, reason, routing key, `×count · time`); Body (content type, size, pretty-printed when
JSON, UTF-8 text otherwise, hex preview for binary); Properties (message_id, correlation_id,
delivery_mode `2 · persistent`, priority, app_id, timestamp, headers, `x-death count`,
`x-first-death-queue`). States: loading "Getting {n} messages… Messages are held unacked until the
page returns them. Cancelling requeues immediately." with Cancel; empty "Queue is empty" +
**Get again** / **Back to queues**; `RESOURCE_LOCKED` → "Cannot get from {queue} —
ACCESS_REFUSED/RESOURCE_LOCKED: queue has an exclusive consumer. Peeking would compete with the
live consumer." + **Retry** / **View consumers** (queue detail). Messages from a Consume are held
only in the page's memory, so while any exist the page shows a persistent warning banner:
"{n} consumed messages exist only on this page — requeue or republish them before leaving."

**1g Publish dialog** (`Pages/PublishDialog.razor`, opened from Exchanges, Queues and queue
detail): Exchange select (+ type chip), Routing key, live preview under it (§5): "Will route to
{n} queues" with queue + matching binding key rows, or the blocked "Matches no binding" panel
("This exchange has no alternate exchange, so the message would be dropped silently." + closest
bindings). Delivery mode Persistent/Transient, Priority, Content type (default
`application/json`), Headers (one `key: value` per line), Body. Footer: "Logged to audit",
**Publish** / **Cancel**. Mutating; on a prod connection publishing is confirmed through
`IConfirmationService` (plain two-button unless the host setting makes it typed). Result
snackbar: "Published — routed" / error "Broker returned the message: unroutable".

**Purge (1g)**: `IConfirmationService.ConfirmAsync("Purge", queue, isProd, count: ready)` — the
host's dialog already implements "type the queue name to confirm" on prod. *Deviation:* the
mockup's extra facts ("Oldest", "Distinct routing keys") would require reading the messages
first, i.e. a hidden `basic.get` pass with side effects; they are not shown.

**1h Shovels & policies** (`/p/rabbitmq/shovels`): tabs **Shovels** / **Policies**.
Shovels: "Dynamic shovels — {n}", **+ New shovel** (dialog: name, source queue, destination
exchange or queue, destination routing key, ack mode on-confirm/on-publish/no-ack, source and
destination URIs default `amqp://` = this broker, "delete after: never/queue-length"), rows
`name  {src} → {dest}  ack-mode  state-chip  [Restart] [Delete]`. A `terminated` shovel gets its
own explanation row: "{name} failed {ago} — {reason}. {ready} messages holding in {src queue}."
*Deviation:* the mockup's per-shovel rate and Pause/Resume are not built — the shovel status API
reports no transfer rate, and pause/resume is `rabbitmqctl`-only (no HTTP endpoint) on 3.13.
Shovel plugin missing (404 on `/api/shovels`) → info alert "The shovel management plugin isn't
enabled on this broker (rabbitmq-plugins enable rabbitmq_shovel_management)." rather than an error.
Policies (read-only): Policy, Pattern (mono), Applies (queues/exchanges/all), Definition
(`k: v · k: v`), Matches (count of queues/exchanges whose name matches the pattern, computed
client-side with a 100 ms regex timeout; `?` on a regex the .NET engine rejects), Prio.

## 8. Handlers and audit

Plain `XxxQueryHandler` / `XxxCommandHandler` per operation, scoped, resolving the secret from
`IConnectionProvider` by connection id and returning `PluginResult`. Commands audit through
`IAuditScope` with action names `rabbitmq.{object}.{verb}`:

| Command | Action | Risk |
|---|---|---|
| Create exchange / queue / shovel | `rabbitmq.exchange.create` etc. | Mutating |
| Delete exchange / queue / shovel | `...delete` | Destructive |
| Purge queue | `rabbitmq.queue.purge` | Destructive |
| Add / remove binding | `rabbitmq.binding.add` / `.remove` | Mutating |
| Restart shovel | `rabbitmq.shovel.restart` | Mutating |
| Publish (incl. requeue, republish) | `rabbitmq.message.publish` | Mutating |
| Consume (get, Consume mode) | `rabbitmq.message.consume` | Destructive |

Audit target: `{connectionName}/{vhost}/{name}`. Peek is a query (no audit row) — its
side effect (the redelivered flag) is disclosed in the byline instead.

Confirmation happens in the page (same as AWS), sourcing `IsProd` from the server-side
`ConnectionInfo` the page loaded via `IConnectionProvider.ListAsync` — never from a query-string
parameter.

## 9. Host integration hooks

All from one cached (60 s, static `ConcurrentDictionary`, keyed by secret — exactly the AWS
`GetCachedQueuesAsync` pattern) snapshot of `nodes + queues(all vhosts via /api/queues) +
bindings(/api/bindings)`; failures propagate (host call sites already catch).

- `GetNavBadgeAsync`: Overview → number of nodes in memory/disk alarm (null when 0); Queues →
  total `Ready` across DLQs (null when 0).
- `GetDashboardMetricsAsync`: `Queues` (count), `Dead-lettered` (DLQ ready total — the label the
  wallboard sums).
- `GetResourceMetricsAsync`: per queue `{vhost}/{name}`, active = ready, dead-letter = ready if
  DLQ else 0.
- `GetDashboardProblemsAsync`: node alarms (`Error`, "Memory alarm — publishers blocked"), DLQs
  with ready > 0 (`Warning`, "{n} dead-lettered", link to queue detail with `connectionId` and
  `vhost`), queues with ready > 0 and 0 consumers that are not DLQs (`Warning`, "{n} ready, no
  consumers").
- `GetOldestDeadLetterAsync`: SDK default (null) — reading a message's age needs a `basic.get`,
  which has side effects.

## 10. Local stack

`docker-compose.yml` gains a `rabbitmq` service under profile `rabbitmq`
(`rabbitmq:3.13-management`, ports 5672/15672, `rabbitmq_shovel` + `rabbitmq_shovel_management`
enabled via a mounted `enabled_plugins`, and a mounted `definitions.json` loaded at boot that seeds
vhost `/orders`, user `sbconsole` with `management` + `monitoring` tags, the mockup's exchanges
(topic `order-events`, fanout `notify.fanout`, direct `billing.direct`, direct
`billing.retry.dlx`, headers `invoice.headers`, fanout `audit.fanout`, direct `legacy.import`
with no bindings), queues with the mockup's arguments (`order-events.q` with DLX, `billing.retry`
with TTL 30 s, `payments-dlq`, quorum `audit.sink`, …), bindings, and the three policies. A
one-shot `rabbitmq-init` publishes a handful of sample messages, including some that dead-letter
into `payments-dlq`. `docker/README.md` documents the connection values
(`host=localhost;vhost=/orders;username=sbconsole;password=sbconsole`).

## 11. Testing

`tests/SbConsole.Plugins.RabbitMq.Tests` (xUnit, FluentAssertions, NSubstitute, bUnit — same
package set as the AWS tests):

- Pure logic: `RabbitConfigParser` (round-trip incl. `;`/`=`/`%` in password), settings defaults,
  `RoutingMatcher` (topic `*`/`#` edge cases, headers x-match variants, closest ranking),
  `DeadLetterTopology`, policy match counting, `AmqpMessageMapper` (x-death parsing from
  RabbitMQ.Client header shapes: `byte[]` strings, `List<object>` of `Dictionary<string,object>`,
  `AmqpTimestamp`), `FriendlyRabbitError`.
- `ManagementApiClient` against a fake `HttpMessageHandler` with realistic JSON (paths escaped,
  auth header present, DTO → record mapping incl. missing stats, non-2xx → exception).
- `TestConnectionAsync` outcome matrix via fake AMQP/management probes.
- Every handler against a substitute `IRabbitOperations` (success, failure → friendly error,
  audit row for commands).
- Every page/dialog with bUnit: loaded, empty, error, and the frame-specific behaviors (stale data
  kept on refresh failure, unroutable blocks Publish, consume requires confirmation, etc.).
- Plugin hooks: badge / metrics / problems built from canned snapshots; cache reuse/expiry with
  a controlled clock.

Manual verification against the docker broker covers what unit tests can't (AMQP transport,
real management JSON, TLS off).
