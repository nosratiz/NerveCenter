# Dashboard redesign — Design

Status: draft, 2026-09-14.

## 1. Context

`Home.razor` (route `/`, titled "Dashboard") already implements the
triage-first shape `docs/design.md` §5.1 calls for: stat tiles, a "Needs
attention" section, and a recent-activity feed. But "Needs attention" today
only ever reports one problem kind — unreachable connections — because
`IPlugin` has no way for a plugin to report its own problems. `docs/design.md`
§5.1 explicitly wants "growing dead-letter backlogs" and "disabled
subscriptions" in that section too, both **plugin-reported** (its own words),
and neither exists yet.

This pass closes that gap and reshapes the page layout to match: a generic
plugin problem-reporting API, the `azure-servicebus` plugin using it to report
DLQ backlogs and disabled subscriptions, and a two-column Home.razor layout
(Needs attention + a compact Activity feed) with a one-line header summary and
per-connection namespace chips.

Out of scope for this pass (separate future spec): the ops-wallboard/chart
view (`docs/design.md` §5.1 defers "charts/wallboard views" past v1.1).

## 2. Plugin SDK: `GetDashboardProblemsAsync`

New optional `IPlugin` method, same "default no-op" convention as
`GetDashboardMetricsAsync`/`GetNavBadgeAsync`/`GetResourceMetricsAsync`:

```csharp
// SbConsole.Sdk/PluginDashboardProblem.cs
public sealed record PluginDashboardProblem(
    string Severity,    // "Error" | "Warning" -- matches MudBlazor's Severity names used elsewhere
    string Title,        // e.g. "payments-dlq", or "notify-fanout / sms"
    string Detail,        // e.g. "214 dead-lettered, +38 in the last hour" -- the host prefixes the
                            // connection name when rendering (§4), so Detail itself never repeats it
    string? LinkHref);     // e.g. "/p/azure-servicebus/dead-letter" -- rendered as a "Peek →"/"Open →" link when present

// IPlugin.cs addition
Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
    Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
    Task.FromResult<IReadOnlyList<PluginDashboardProblem>>([]);
```

`connectionId` and `store` are new params relative to the other per-connection
`IPlugin` methods — necessary because DLQ-trend detection (§3) reads
per-connection, per-resource metric history, which is keyed by connection ID
and lives in the plugin's own `IPluginStore`. The host passes both explicitly
(the same way `MetricsCollectorService` already passes a store into
`MetricHistoryStore.AppendAsync` from outside the plugin instance, since
plugins are constructed via a parameterless `new()` with no DI container of
their own).

The host calls this once per connection per plugin (mirroring the existing
`GetDashboardMetricsAsync` loop in `Home.razor`) and merges the results with
the existing host-level "unreachable connection" problems into one list,
sorted Errors before Warnings, each list stable-sorted otherwise.

## 3. `MetricHistoryStore.ReadAsync`

Today `MetricHistoryStore` only has `AppendAsync`; `Queues.razor` reads a
resource's history back by calling `Store.GetAsync` + deserializing the JSON
itself. This pass needs the same read in a second place
(`ServiceBusPlugin.GetDashboardProblemsAsync`), so add the missing read-side
helper and point `Queues.razor` at it too, rather than a second copy of the
same deserialization:

```csharp
public static async Task<IReadOnlyList<MetricSnapshotPoint>> ReadAsync(
    IPluginStore store, Guid connectionId, string resourceName, CancellationToken ct = default)
{
    var json = await store.GetAsync(MetricHistoryKey.For(connectionId, resourceName), ct);
    return json is null ? [] : JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(json) ?? [];
}
```

## 4. ServiceBusPlugin: DLQ backlog problems

`GetDashboardProblemsAsync` lists queues (`ops.ListQueuesAsync` — already
fetched today in `GetDashboardMetricsAsync`; same call, separate invocation
since these are two different `IPlugin` methods). For every queue with
`DeadLetterMessageCount > 0`:

- Emit `PluginDashboardProblem("Warning", queue.Name, "{connectionName} · {count} dead-lettered", DeadLetterNavHref)`.
- If `MetricHistoryStore.ReadAsync` has a point at or before (now − 1h), append
  `", +{delta} in the last hour"` to `Detail` when `delta > 0` (current
  `DeadLetterMessageCount` minus that point's `DeadLetterCount`). No point
  ≥ 1h old (e.g. connection added recently) ⇒ no delta suffix, just the count.
- No minimum-count threshold: any non-zero DLQ is surfaced, growing or not.
  `docs/design.md`'s "growing... backlogs" phrasing describes the common case,
  not a gate — hiding a steady (non-growing) backlog would be the wrong
  default for an ops tool. The delta is an annotation on top of that, not a
  filter.

`connectionName` isn't known inside the plugin (it only gets a connection
string) — the host fills in `{connectionName}` when rendering (see §6), so
`Detail` from the plugin omits it and is just `"{count} dead-lettered[, +N in
the last hour]"`; the host prefixes the connection name when building the
card, consistent with how it already prefixes/labels other host-rendered
problem rows.

## 5. ServiceBusPlugin: disabled-subscription problems

`SubscriptionSummary` gains a `Status` field:

```csharp
public sealed record SubscriptionSummary(
    string Name, long ActiveMessageCount, long DeadLetterMessageCount, long TotalMessageCount,
    string Status); // "Active" | "Disabled" | "ReceiveDisabled"
```

`AzureServiceBusOperations.ListSubscriptionsAsync` currently only calls
`GetSubscriptionsRuntimePropertiesAsync` (message counts). Status is a
*config* property, from the separate `GetSubscriptionsAsync` call. Merge both
by subscription name into one `ListSubscriptionsAsync` result (one extra
admin-client round trip per topic, same pattern already used elsewhere in
this file for split runtime/config data).

`GetDashboardProblemsAsync` lists topics (`ops.ListTopicsAsync`), then for
each topic lists its subscriptions (`ops.ListSubscriptionsAsync`) and emits
`PluginDashboardProblem("Warning", "{topic}/{subscription}", "subscription {status}", topicsNavHref)`
for every subscription whose `Status != "Active"`.

## 6. ServiceBusPlugin: Dead-lettered dashboard tile

`GetDashboardMetricsAsync` gains one more entry, reusing the queues already
fetched in that same call:

```csharp
new PluginDashboardMetric("Dead-lettered", queues.Sum(q => q.DeadLetterMessageCount))
```

This feeds the header summary bar's "312 dlq" (§7) the same way the existing
Queues/Topics/Subscriptions metrics already do — no new data path.

## 7. Home.razor layout

- **Header** — the current `h4` "Dashboard" + Refresh row becomes a compact
  single line: instance name/title, then a summary built from the existing
  tile values (`"{conn} conn · {q} q · {t} t / {sub} sub · {dlq} dlq"`),
  `refreshed HH:mm:ss`, and the Refresh button, all smaller and inline —
  no new data, just a denser header rendering of what `Tiles` already holds.
- **Needs attention (left column, primary)** — the merged, severity-sorted
  problem list from §2, rendered as cards:
  - Host-level unreachable-connection problems keep their existing shape
    exactly (Retry button + busy spinner + "Edit connection" link) — this
    entire pass changes none of that behavior, only where it sits in the
    merged list and how the card is styled.
  - Plugin-reported problems render `Title` as the card heading and
    `"{connectionName} · {Detail}"` as the body (host fills in the connection
    name — see §4), with a "Peek →" link to `LinkHref` when present.
  - Zero problems ⇒ existing "All clear" `MudAlert` (unchanged).
- **Namespace chips** — below Needs attention, one chip per connection: green
  if it has zero open problems (any severity), red if it has at least one.
  Purely derived from data already loaded (connections + merged problem list
  grouped by connection) — no new query.
- **Activity (right column, secondary)** — the same `_recentActivity` data
  (`ListAuditEntriesQueryHandler`, unchanged), rendered condensed: icon +
  action + target + relative time, no table headers/chrome. Keeps an "All →"
  link to `/audit`. This replaces the `MudTable` rendering, not the data
  source.
- Two columns stack to one on narrow viewports (existing MudBlazor
  breakpoint/flex conventions used elsewhere in the app, e.g. Queues' toolbar).

## 8. Testing

- `HomeTests.cs`: extend with a fake `IPlugin` (as `Services.AddSingleton<
  IEnumerable<IPlugin>>` currently registers `Array.Empty<IPlugin>()`) that
  returns canned `PluginDashboardProblem`s, asserting they render in Needs
  attention alongside an unreachable-connection problem, correctly
  severity-sorted. Existing tests (`Shows_all_clear_when_nothing_needs_
  attention`, `Unreachable_connection_shows_an_alert_with_a_working_retry_
  button`, tile tests) must keep passing — none of their asserted behavior
  changes, only surrounding layout.
- `ServiceBusPluginTests.cs`: new cases for `GetDashboardProblemsAsync` —
  zero problems when no DLQ/disabled subscriptions exist; a DLQ-backlog
  problem with no delta when there's no hour-old history point; a DLQ-backlog
  problem with a `+N` delta when a suitable history point exists (seed via
  `MetricHistoryStore.AppendAsync` against a `FakeTimeProvider`-backed clock,
  same pattern `MetricsCollectorService`'s own tests likely already use);
  a disabled-subscription problem for `Status != "Active"`.
- New `MetricHistoryStoreTests` case (or extend existing) for `ReadAsync`:
  empty list when no history key exists, round-trips what `AppendAsync` wrote.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before commit,
  per `docs/design.md` §8.

## 9. Manual verification

Run the app, seed a connection with a queue that has dead-lettered messages
and a topic with a disabled subscription (or fall back to reasoning about
rendered markup if no live Service Bus namespace is available), and confirm:

- Needs attention shows both problem cards with correct text, sorted after
  any unreachable-connection error.
- The DLQ card shows a `+N in the last hour` delta after two `MetricsCollector
  Service` ticks with a growing count, and no delta on the very first tick.
- Namespace chips go green/red correctly as problems appear/clear.
- Header summary line matches the tile values exactly.
- Activity panel still reflects real audit entries and its "All →" link works.
- Layout stacks to one column at narrow widths without overlap.

## 10. Out of scope / future phases

- The ops-wallboard/chart view (throughput chart, big live numbers,
  `Platform Ops` framing) — separate future spec (Piece B), deferred per
  `docs/design.md` §5.1.
- Any problem kinds beyond DLQ backlogs and disabled subscriptions (e.g.
  oldest-message-age warnings) — not shown in the approved mockup, not added
  here.
- A second plugin implementing `GetDashboardProblemsAsync` — the SDK method is
  generic on purpose, but `azure-servicebus` is still the only plugin.
- Configurable DLQ thresholds/snooze/dismiss for Needs-attention cards — no
  such control exists in the mockup or `docs/design.md`; every open problem is
  always shown.
