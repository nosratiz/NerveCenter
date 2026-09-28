# RabbitMQ plugin, phase 2 — Design

Status: approved in brainstorming, 2026-09-26.

## 1. Context

Phase 1 (`2026-09-25-rabbitmq-plugin-design.md`) built the whole mockup surface (frames 1a–1h).
Phase 2 adds four features the mockup did not cover, all inside `SbConsole.Plugins.RabbitMq`, all
following phase 1's conventions unchanged: operations on `IRabbitOperations`, thin handlers through
`HandlerRunner`, the connection + vhost picker, the shared management-API error model, stale data
kept visible on refresh failure, theming only via `--mud-palette-*`.

| # | Feature | Where |
|---|---|---|
| §2 | Dead-letter page with safe bulk replay | `Pages/DeadLetters.razor` (+ `ReplayDialog.razor`), new nav item |
| §3 | Policy create / edit / delete | Policies tab of `Pages/ShovelsPolicies.razor` (+ `PolicyDialog.razor`) |
| §4 | Connections & channels, force-close | `Pages/Connections.razor`, new nav item |
| §5 | Exchange-to-exchange bindings, unbind from Exchanges | `AddBindingDialog`, `ExchangeBindingsDialog`, `RoutingMatcher` |

Build order is §5 → §3 → §4 → §2: §5 changes `RoutingMatcher`, which §2's origin resolution and the
Publish preview both depend on; §2 is the only one adding an AMQP operation, so it goes last on a
settled seam.

No SDK or Core change is needed. Plugins still depend on `SbConsole.Sdk` only.

## 2. Dead-letter page

**Route** `/p/rabbitmq/dead-letter`, nav item "Dead-letter" after Queues. The DLQ-ready nav badge
moves from Queues to this item (`ComputeNavBadge` keys on the href); Queues keeps no badge.

**Scope: every RabbitMQ connection, every vhost** — like the Service Bus and Kafka dead-letter
pages, not the per-page picker. Data comes from the existing 60 s cached `BrokerSnapshot` (the one
the badge and dashboard hooks use) plus `ListBindingsAsync` / `ListExchangesAsync` per vhost that
has at least one DLX, fed to `DeadLetterTopology.Compute`. A connection whose snapshot fails shows
one inline error row ("{connection}: {friendly error}") and the others still render.

**Rows (one per DLQ):** Connection (prod chip), Vhost, DLQ (link to queue detail), Ready, Unacked
(separate columns — never summed), Sources (the queues whose DLX routes here, from
`DeadLetterTopology`; `+{n} more` after three), DLX chip. Sorted by Ready descending. Empty state:
"No dead-letter queues hold messages across {n} RabbitMQ connections." A "Show empty DLQs" switch
(default off) includes DLQs with Ready = 0.

Death *reasons* are not listed: reading them requires `basic.get`, a side effect (the phase-1 purge
dialog made the same call). **Browse** links to the existing Get page in Peek mode, which shows
them.

**Row actions:** **Browse** (`/p/rabbitmq/queues/{dlq}/get?connectionId=&vhost=&mode=peek`),
**Replay…**.

### 2.1 Replay

**New operation:**

```csharp
Task<ReplayOutcome> ReplayDeadLettersAsync(
    string secret, string vhost, string queue, ReplayRequest request, CancellationToken ct = default);

public sealed record ReplayRequest(int MaxMessages, int PoisonThreshold);
public sealed record ReplayOutcome(
    int Replayed, int SkippedNoOrigin, int SkippedPoison, string? StoppedReason, int? StoppedAtIndex);
```

**Algorithm (AMQP, one channel, publisher confirms on):** for i in 1..MaxMessages:

1. `basic.get(queue, autoAck: false)`. Empty → stop, success.
2. Resolve the origin (pure, `Routing/ReplayOrigin.cs`). `x-death` is ordered most-recent-first,
   so the origin is **not** "the first entry": it is the `DeathRecord` whose `Queue` equals
   `x-first-death-queue` and whose `Exchange` equals `x-first-death-exchange`. Exchange = that
   record's `Exchange`, routing key = its `RoutingKeys[0]` (`""` when the list is empty).
   - No `x-death`, no `x-first-death-*` headers, or no matching record → `SkippedNoOrigin`,
     keep the delivery tag held (see step 5), continue.
   - That record's `Count` ≥ `PoisonThreshold` → `SkippedPoison`, held, continue.
   - Origin exchange is `""` (the message was published straight to its queue): republish to the
     default exchange with routing key = `x-first-death-queue`.
3. `basic.publish(origin exchange, routing key, mandatory: true)` with the message's original
   properties, headers (including `x-death`) and body, unchanged.
4. Await the confirm. Ack'd and not returned → `basic.ack(tag)`, `Replayed++`. Nack'd, returned
   (unroutable), or threw → **stop**: `StoppedReason` = friendly error, `StoppedAtIndex` = i, and
   this message is also held.
5. At the end (success, stop, or cancellation) every held tag is released with one
   `basic.nack(multiple: false, requeue: true)` per tag; if the channel is already broken, closing
   it requeues them. Skipped messages therefore stay in the DLQ, marked redelivered.

Messages are never acked before the confirm for their republished copy arrives, so a failure at
any point loses nothing (it may *duplicate* at most the one message whose confirm raced a
connection drop — at-least-once, stated in the dialog).

Because skipped messages are held until the end, a run over a DLQ that is mostly unreplayable
holds up to `MaxMessages` unacked; `MaxMessages` is capped at 1000 for that reason.

**Replay dialog** (`Pages/ReplayDialog.razor`): count select 10 / 100 / 1000 / "All ({ready})"
(All capped to 1000 with the caption "Replays at most 1,000 per run"); poison threshold numeric
(default 5, min 1) with the caption "Skip messages that have already died this many times from
their origin — replaying them feeds the loop"; body text: "Each message is republished to the
exchange and routing key it was first dead-lettered from, then removed from {dlq}. Messages with
no dead-letter history are left in place. Skipped messages return marked redelivered.
At-least-once: a connection drop can duplicate one message." `ActionRisk.Destructive`: confirmed
through `IConfirmationService.ConfirmAsync("Replay", dlq, isProd, count)`, `IsProd` from the
server-side connection lookup. Result panel: "{n} replayed · {k} skipped (no origin) · {p} skipped
(poison, ≥{t} deaths)" and, when stopped, "Stopped at message {i}: {reason}". The row refreshes
(bypassing the snapshot cache for that vhost) after a run.

**Audit:** `rabbitmq.message.replay`, target `{connection}/{vhost}/{dlq}`, detail = the outcome
counts. One row per run, written even when the run stopped early.

## 3. Policy management

Policies tab of `/p/rabbitmq/shovels` gains **+ New policy** and per-row **Edit** / **Delete**.

**New operations:**

```csharp
Task PutPolicyAsync(string secret, string vhost, PolicyInfo policy, CancellationToken ct = default);
Task DeletePolicyAsync(string secret, string vhost, string name, CancellationToken ct = default);
```

`PUT /api/policies/{vhost}/{name}` with `{pattern, apply-to, priority, definition}`;
`DELETE /api/policies/{vhost}/{name}`. Both segments escaped with the existing `Seg`.

**Dialog** (`Pages/PolicyDialog.razor`, create and edit): Name (read-only on edit — RabbitMQ has
no rename), Pattern (mono), Apply to (queues / exchanges / all / classic_queues / quorum_queues /
streams), Priority (int, default 0). Typed definition fields, each optional (blank = key absent):

| Key | Input |
|---|---|
| `dead-letter-exchange` | exchange picker (existing exchanges, free text allowed) |
| `dead-letter-routing-key` | text |
| `message-ttl`, `expires` | ms, integer ≥ 0 |
| `max-length`, `max-length-bytes` | integer ≥ 0 |
| `overflow` | drop-head / reject-publish / reject-publish-dlx |
| `queue-mode` | default / lazy |
| `delivery-limit` | integer ≥ 0 |
| `alternate-exchange` | exchange picker |

Plus **Extra keys (JSON object)** for anything else (`ha-mode`, `federation-upstream`, …). On edit,
known keys populate the typed fields and the rest round-trip through the JSON box unchanged.

**Pure logic** (`Policies/PolicyDefinitionBuilder.cs`, unit-tested): typed fields + JSON → the
definition dictionary, and the reverse split. Validation errors: pattern fails to compile (100 ms
timeout, same as `PolicyMatcher`); JSON not an object; a JSON key duplicates a typed field;
definition empty ("A policy with no keys does nothing").

**Live preview** under Pattern: "Matches {n} queues · {m} exchanges" via `PolicyMatcher`, honouring
apply-to, from the queue and exchange lists the page already loads. On edit, a warning when
n + m > 0: "Applies to {n} queues now. Changes take effect immediately and silently override queue
arguments."

**Risk / audit:** create `rabbitmq.policy.create` and edit `rabbitmq.policy.update` are Mutating
(no confirmation — same as create queue); delete `rabbitmq.policy.delete` is Destructive, confirmed
through `IConfirmationService`, and the dialog text names the matched count.

## 4. Connections & channels

**Route** `/p/rabbitmq/connections`, nav item "Connections" after Shovels & policies. Connection +
vhost picker header, auto-refresh Off / 10s / 30s / 60s persisted in `IPluginStore` like Queues.

**New operations:**

```csharp
Task<IReadOnlyList<ClientConnectionInfo>> ListClientConnectionsAsync(string secret, string vhost, CancellationToken ct = default);
Task<IReadOnlyList<ChannelInfo>> ListChannelsAsync(string secret, string vhost, CancellationToken ct = default);
Task CloseClientConnectionAsync(string secret, string name, string? reason, CancellationToken ct = default);
```

`GET /api/vhosts/{vhost}/connections`, `GET /api/vhosts/{vhost}/channels`,
`DELETE /api/connections/{name}` with header `X-Reason` when a reason is given.

`ClientConnectionInfo(Name, User, ClientProvidedName, Product, ProductVersion, Platform, Protocol,
bool Tls, State, int Channels, double? RecvRate, double? SendRate, DateTimeOffset? ConnectedAt,
string PeerHost, int PeerPort)`; `ChannelInfo(Name, ConnectionName, int Number, int Prefetch,
long Unacked, int Consumers, bool ConfirmMode, bool Transactional, State)`. Fixtures captured from
3.13.7 with the local stack.

**Table:** Connection (`{peer_host}:{peer_port}`, client-provided name beneath when set), User,
Client (`{product} {version}`), Protocol, TLS chip, State chip, Channels, Recv /s, Send /s,
Connected (relative). Rows whose `ClientProvidedName == "sbconsole"` get a "this console" chip —
the plugin's own short-lived Get/Publish connections. Row expands to its channels (matched by
`ConnectionName`): #, Prefetch, Unacked, Consumers, Confirm, State. A channel with Unacked > 0 and
Prefetch 0 gets an "unbounded prefetch" chip.

Summary line: "{n} connections · {c} channels · {u} unacked". Filter box (substring over
connection name, user, client). Empty: "No client connections to {vhost}."

**Close…** per row: dialog with optional reason (default "Closed from SbConsole by {user}",
max 200 chars); `ActionRisk.Destructive`, **always** confirmed through `IConfirmationService`
(typed on prod, two-button otherwise). Audit `rabbitmq.connection.close`, target
`{connection}/{vhost}/{peer}`. A 403 maps to a specific friendly message: "Closing another user's
connection needs the administrator tag on {user}." A 404 (already gone) is reported as success
with the snackbar "Connection had already closed".

## 5. Exchange-to-exchange bindings

**Operations change** (breaking only inside the plugin): `AddBindingAsync` and `RemoveBindingAsync`
take `BindingDestinationType DestinationType` (`Queue` / `Exchange`) and a `destination` name
instead of `queue`; the path segment becomes `q` or `e`. Existing callers pass `Queue`.

**Add binding dialog** gains a Destination type toggle (Queue / Exchange) when opened from the
Exchanges page; opened from queue detail it stays fixed to that queue as today. An exchange
destination can't equal the source exchange ("An exchange can't be bound to itself").

**Exchange bindings dialog** lists two sections: "Routes to — {n}" (source = this exchange;
destination with a queue/exchange chip) and "Receives from — {n}" (destination = this exchange,
exchange sources only). Each row gains **Unbind**: Mutating (not Destructive), confirmed with the
plain two-button dialog on every connection, prod included — the same rule as Unbind on queue
detail. Plus **+ Add binding**.

**`RoutingMatcher` follows exchange destinations.** Matching an exchange binding recurses into the
destination exchange with the same routing key and headers, depth cap 8, visited-set cycle guard
(a cycle is not an error — each exchange is evaluated once, as the broker does). Result rows carry
the path: `order-events → audit.fanout → audit.sink`. Queues reached by several paths appear once
(the broker delivers one copy per queue). An exchange's alternate exchange is still honoured at
each hop only when that hop matched nothing. `DeadLetterTopology` target queues and the Replay
origin checks use the same recursive resolution, so a DLX bound onward to another exchange is
followed.

Exchanges page: the Bindings count column counts both destination types; the "no bindings" warning
applies only when neither exists.

## 6. Handlers and audit

| Command | Action | Risk |
|---|---|---|
| Replay dead letters | `rabbitmq.message.replay` | Destructive |
| Create / update policy | `rabbitmq.policy.create` / `.update` | Mutating |
| Delete policy | `rabbitmq.policy.delete` | Destructive |
| Close client connection | `rabbitmq.connection.close` | Destructive |
| Add / remove binding (either type) | `rabbitmq.binding.add` / `.remove` (existing) | Mutating |

Queries (no audit): `ListDeadLetterOverviewQueryHandler`, `ListClientConnectionsQueryHandler`
(returns connections + channels together). All through `HandlerRunner`.

## 7. Local stack

`docker/rabbitmq/definitions.json` gains an exchange-to-exchange binding (`order-events` →
`audit.fanout`, key `order.#`) so the recursive preview has data. `init-messages.sh` is unchanged;
the existing `payments-dlq` messages (with x-death from `billing.payments.q`) exercise Replay. On
the Connections page the stack's live clients are the `legacy-migrate` shovel's connections and
SbConsole's own.

## 8. Testing

- `ManagementApiClient`: new endpoints against captured 3.13.7 JSON through the existing fake
  `HttpMessageHandler` — paths, escaping, `X-Reason`, the PUT body shape, 403/404 mapping.
- Replay: extract the loop into `DeadLetterReplayer` over a small internal `IReplayChannel` seam
  (get / publish-and-confirm / ack / nack) so it is unit-tested without a broker: ordering,
  ack-only-after-confirm, stop on return/nack/throw, held tags released on every exit path
  including cancellation, poison and no-origin skips, the 1000 cap.
- `RoutingMatcher`: e2e recursion, cycle, depth cap, alternate exchange per hop, de-duplication.
- `PolicyDefinitionBuilder`: round-trip, validation errors.
- `ReplayOrigin`: most-recent-first x-death ordering, default-exchange origin, missing headers.
- Handlers: substitute `IRabbitOperations`, assert audit rows and that no audit is written on
  queries.
- Pages: bUnit for DeadLetters (multi-connection, one failing), PolicyDialog (edit round-trip,
  validation), Connections (expand, this-console chip, close confirm), ExchangeBindingsDialog
  (both sections, unbind).
- No broker in tests (house rule); the local stack is for manual verification only.

## 9. Docs

Append a dated addendum under `design.md` §6.8 recording: replay's ack-after-confirm and
held-until-end semantics and the at-least-once caveat; the badge move to Dead-letter; recursive
routing; the administrator-tag requirement for closing others' connections.
