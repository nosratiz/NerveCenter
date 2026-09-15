# Ops wallboard — Design

Status: draft, 2026-09-15.

## 1. Context

The Dashboard redesign (`docs/superpowers/specs/2026-09-14-dashboard-redesign-design.md`,
implemented) explicitly deferred an ops-wallboard/kiosk view — big live numbers,
a throughput chart, per-namespace backlog — per `docs/design.md` §5.1's own
"charts/wallboard views" deferral. This is that follow-up: a separate
`/wallboard` page, not a rework of `/` (Home.razor).

**Data-availability constraint that shaped this design:** the approved mockup
shows separate "Incoming/min" and "Completed/min" tiles. Azure Service Bus's
management API (what this app's `AzureServiceBusOperations` talks to) has no
"messages sent this minute" counter — only point-in-time counts
(`ActiveMessageCount`, `DeadLetterMessageCount`) via `ListQueuesAsync`, which
`MetricsCollectorService` already samples every 60s into `MetricHistoryStore`.
From two snapshots you can compute `ΔActive` and `ΔDeadLetter`, but **not**
separately recover "incoming" vs. "completed" — a shrinking Active count is
consistent with any mix of completions and dead-letter moves, and there is no
second equation to solve for both. Getting real separate rates would require
Azure Monitor's metrics API (a different Azure API, a different credential
model — Monitoring Reader via Azure AD, not the Service Bus connection string
this app authenticates with today). Approved resolution: merge the mockup's
two lines/tiles into one honest **Active-count trend**, computed from data
already being collected. No new Azure permissions, no invented numbers.

## 2. New SDK method: `GetOldestDeadLetterAsync`

The throughput/growth charts need no new SDK surface at all — see §3. But
"oldest message: 4h12m, payments-dlq · 214 msgs" needs a real peek; there is
no honest way to approximate a single message's enqueue time from aggregate
counts. New optional `IPlugin` method, same default-no-op convention as the
rest of the SDK:

```csharp
// IPlugin.cs addition
Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
    Guid connectionId, string connectionString, CancellationToken ct = default) =>
    Task.FromResult<OldestDeadLetterEntry?>(null);

// New SbConsole.Sdk/OldestDeadLetterEntry.cs
public sealed record OldestDeadLetterEntry(string ResourceName, DateTimeOffset EnqueuedTime, long DeadLetterCount);
```

`ServiceBusPlugin` implements it: list queues (`ops.ListQueuesAsync`, same
call already made elsewhere), for every queue with `DeadLetterMessageCount >
0`, peek its dead-letter sub-queue for exactly the first message
(`ops.PeekMessagesAsync(connectionString, queue.Name, fromDeadLetter: true,
maxMessages: 1, fromSequenceNumber: null, ct)`) **in parallel** via
`Task.WhenAll` — one peek per DLQ-bearing queue, never serial (Piece A's
review flagged exactly this kind of per-resource fan-out; this pass builds it
parallel from the start). The queue whose peeked message has the earliest
`EnqueuedTime` wins; return its `(ResourceName, EnqueuedTime,
DeadLetterMessageCount)`. `connectionId` is accepted for signature symmetry
with `GetDashboardProblemsAsync` but unused by this implementation — no
per-connection state is needed for a peek. The host (`Wallboard.razor`) calls
this once per connection and keeps the overall-minimum `EnqueuedTime` across
every connection.

This is the only per-page-load Azure call this feature adds beyond what
`Wallboard.razor`'s reuse of existing data already costs (§3-§5) — bounded by
"one peek per DLQ-bearing queue," which is exactly the same queue set Piece A
already enumerates for DLQ-backlog problems.

## 3. Throughput chart: aggregate Active + Dead-lettered trend

Built entirely from existing infrastructure — no new SDK method:

1. For each connection, call the existing `plugin.GetResourceMetricsAsync(secret)`
   (pre-dates this feature; already used by `MetricsCollectorService`) to get
   the connection's current resource names.
2. For each resource, call the existing `MetricHistoryStore.ReadAsync(store,
   connectionId, resourceName)` (Piece A) to get its full retained history
   (up to 24h, per `MetricHistoryStore`'s existing retention constant —
   unchanged, no new storage).
3. A new pure, synchronous helper, `WallboardAggregator.BucketAndSum`
   (`src/SbConsole.Web/Wallboard/WallboardAggregator.cs`), takes every
   resource's `IReadOnlyList<MetricSnapshotPoint>`, a bucket size, and a
   window, and returns one combined series: for each bucket, the sum of every
   resource's most-recent-point-at-or-before-the-bucket-end `ActiveCount` and
   `DeadLetterCount`. Bucket size is 1 minute for the "1h" tab, 5 minutes for
   "24h" (both draw from the same stored history; the tab only changes the
   window and bucket width, not what's collected).
4. Rendered as two `MudBlazor.Charts` `Line` series (already part of the
   installed MudBlazor package — no new dependency) — "Active" and
   "Dead-lettered" — sharing one time axis.

**Event markers**: vertical annotations on the chart, from real audit-log
data — `ListAuditEntriesQueryHandler` with `From`/`To` set to the chart's
visible window and a generous `PageSize` (e.g. 200; this is a narrow
diagnostic read, not the paginated Audit page, so no new filter parameter is
added to `AuditQuery`), filtered client-side to `Action == "connection.test"
&& Succeeded == false`. Each becomes a marker labeled with the connection name
and time, matching the mockup's "09:14 sb-eu-prod 401" annotation with real
data instead of a placeholder.

## 4. Dead-letter growth bar chart

Per-hour **backlog total** (not a delta) across all resources/connections,
last 12h, from the same bucketed history §3 already assembles (reused at
1-hour bucket width). A bar is the summed `DeadLetterCount` at that hour's
bucket — an honest snapshot that visually grows as the backlog grows, and
can't misleadingly dip negative the way a delta-per-bucket would on a bucket
where messages were purged.

**Scope-down from the mockup, flagged explicitly**: the mockup's narrative
text reads "Accelerating since 06:00 — payments-dlq accounts for 69% of the
rise," which implies inflection-point/trend-change detection. This design
does not attempt to detect *when* growth started accelerating — that needs
real trend analysis this spec isn't going to fake. Instead, a new pure helper
(`WallboardAggregator.SummarizeGrowth`) compares the first and last bucket of
the 12h window per-resource and produces either:
- `"+{totalDelta} in the last 12h — {topResourceName} accounts for {pct}% of the rise."`
  when the total delta across all resources is positive (percentage from the
  single resource with the largest individual delta), or
- `"No change in the last 12h."` when the total delta is zero or negative.

## 5. Backlog by namespace

No new data source. `PluginDashboardProblem.Detail` is a display string, not
structured data, so this section doesn't try to re-derive counts from it.
Instead it reuses `GetDashboardMetricsAsync`'s existing per-connection
`"Dead-lettered"` metric (Piece A, `ServiceBusPlugin.GetDashboardMetricsAsync`
— already a summed count across that connection's queues).
`Wallboard.razor`'s `LoadAsync` already calls `GetDashboardMetricsAsync` per
connection for the tiles anyway (§6), so grouping that same per-connection
number by connection *is* the namespace backlog — zero additional plugin
calls beyond what the tiles already need.

Rendered as a horizontal stacked bar (proportional segments, one per
connection with a non-zero count) plus a list of rows (connection name,
count), with a "Fix →" link to `/connections` on any connection that's also
in the unreachable-connections list (existing host-level detection, reused
as-is from Home.razor's `_unreachable`).

## 6. Tiles

Tile values are computed once per page load, independent of which chart tab
(`1h`/`24h`) is selected — switching tabs changes the chart's window and
bucket width only, never the tile numbers.

- **Active** (was "Incoming/min" + "Completed/min" in the mockup) — current
  total Active count across all resources/connections (the same raw history
  §3 reads, independently bucketed to 1 minute for this tile regardless of
  which chart tab is selected), plus its net change over the last minute as
  a small `+N`/`-N` annotation — not a rate claim, a literal count and its
  most recent delta.
- **Dead-lettered** — total across all connections (sum of each connection's
  `GetDashboardMetricsAsync` `"Dead-lettered"` metric — same source as §5),
  plus `+N / 1h` computed the same way Piece A's per-queue delta already
  works (`DashboardProblems`' 1h-baseline logic), just summed across all
  queues instead of shown per-queue.
- **Oldest message** — from §2's `GetOldestDeadLetterAsync`, minimum across
  connections; renders as a humanized duration (e.g. "4h 12m") plus the
  resource name and its current DLQ count.

## 7. Page, route, and layout

New `src/SbConsole.Web/Components/Pages/Wallboard.razor` at `/wallboard`,
`@layout EmptyLayout` (the existing minimal layout — `ThemedRoot` + `@Body`,
already used for Login — reused as-is, no new layout component needed) for
the mockup's full-bleed kiosk chrome instead of the sidebar `MainLayout`. A
link/icon button on `Home.razor`'s header (next to Refresh) opens it. Time
range is a 2-way `1h` / `24h` toggle — **no `7d` tab**: `MetricHistoryStore`
retains 24h of history (existing constant, unchanged by this pass), so a 7d
view would have no data behind it; shipping a control with nothing behind it
would be the same kind of dishonesty this whole design has been avoiding.

## 8. Testing

- `WallboardAggregator.BucketAndSum` and `SummarizeGrowth`: pure, synchronous,
  directly unit-tested (same leverage-point pattern as Piece A's
  `DashboardProblems`) — bucket alignment, multi-resource summation, the
  positive/zero/negative-delta branches of the growth summary.
- `ServiceBusPlugin.GetOldestDeadLetterAsync`: no dedicated unit test (talks
  to the real Azure SDK via `AzureServiceBusOperations`, same established
  convention as `TestConnectionAsync`/`GetDashboardMetricsAsync` — untestable
  without a live broker); the parallel-peek wiring is thin enough that its
  correctness rests on the (tested) minimum-selection logic being pulled out
  as its own pure function where practical.
- `Wallboard.razor`: bUnit tests mirroring `HomeTests.cs`'s conventions —
  fake `IPlugin` substitutes for the metrics/history/oldest-entry calls, real
  `ListAuditEntriesQueryHandler` against `TestDb` for the event markers,
  asserting rendered tile values, chart data presence, and the namespace
  backlog list/links.

## 9. Out of scope

- True separate incoming/completed rates (would need Azure Monitor + a second
  credential model — a substantially bigger change, not attempted here).
- The `7d` time range (no data behind it under the current 24h retention;
  extending retention is a separate, unasked-for storage-growth decision).
- Trend/inflection-point detection for the growth narrative ("accelerating
  since HH:MM") — replaced with a directly-computable delta+top-contributor
  summary (§4).
- Subscription-level (as opposed to queue-level) dead-letter data in the
  oldest-message tile and the growth chart — consistent with Piece A's own
  DLQ-backlog problems being queue-only.
