# Kafka plugin — Consumer group management — Design

Status: draft, 2026-09-17.

## 1. Context

Second slice of the Kafka plugin's full target scope (`docs/superpowers/specs/
2026-09-16-kafka-topics-plugin-design.md` §1, item 2): list consumer groups,
show per-topic/partition lag, and allow offset reset — the Kafka analogue of
Service Bus's Topics & Subscriptions (`docs/design.md` §6.2). Topic
management, peek, and produce (item 1) already shipped; this plan reuses
their conventions line-for-line (handler shape, `FriendlyKafkaError`, eager
aggregation, `Guid.NewGuid()` throwaway `group.id` for read-only ops) rather
than introducing new patterns.

Dead-letter handling (item 3 of the original scope) remains a separate,
later plan.

## 2. Data types

New files under `Client/`, alongside the existing `TopicSummary`/
`PeekResult`/etc.:

```csharp
public sealed record ConsumerGroupSummary(string GroupId, string State, int MemberCount, long TotalLag);

public sealed record ConsumerGroupMember(string ClientId, string? Host, IReadOnlyList<TopicPartitionRef> AssignedPartitions);

public sealed record TopicPartitionRef(string TopicName, int Partition);

public sealed record ConsumerGroupPartitionLag(
    string TopicName, int Partition, long CommittedOffset, long HighWatermark, long Lag,
    string? ConsumerClientId, string? ConsumerHost);

public sealed record ConsumerGroupDetail(
    string GroupId, string State,
    IReadOnlyList<ConsumerGroupPartitionLag> Partitions,
    IReadOnlyList<ConsumerGroupMember> Members);

public enum OffsetResetMode { Earliest, Latest, Offset, Timestamp }
```

`OffsetResetMode` is deliberately separate from `PeekStart` (Earliest/
Latest/Offset) even though they overlap — Peek never needs a timestamp mode,
and coupling the two would force Peek's UI to handle a mode it can't use.

`IKafkaOperations` gains three methods:

```csharp
Task<IReadOnlyList<ConsumerGroupSummary>> ListConsumerGroupsAsync(string config, CancellationToken ct = default);
Task<ConsumerGroupDetail> GetConsumerGroupDetailAsync(string config, string groupId, CancellationToken ct = default);
/// <summary>Destructive. Fails if the group is not Empty.</summary>
Task ResetConsumerGroupOffsetAsync(
    string config, string groupId, string topicName, int partition, OffsetResetMode mode,
    long? offset, DateTimeOffset? timestamp, CancellationToken ct = default);
```

`offset` is required (and only meaningful) when `mode == Offset`; `timestamp`
is required (and only meaningful) when `mode == Timestamp`. Both are ignored
for `Earliest`/`Latest`. Validated in the command handler the same way
`PeekMessagesQueryHandler` already validates `PeekStart.Offset` needing a
value.

## 3. Lag computation

`ListConsumerGroupsAsync`:

1. `AdminClient.ListConsumerGroupsAsync()` → `ConsumerGroupListing.GroupId`/
   `State` for every group visible to the cluster.
2. Eagerly, per group (same accepted-cost choice the Topics plan made for
   per-partition watermark queries, `docs/superpowers/specs/2026-09-16-
   kafka-topics-plugin-design.md` §4): `ListConsumerGroupOffsetsAsync` with a
   `ConsumerGroupTopicPartitions(groupId, topicPartitions: null)` — passing
   no partition filter returns every topic-partition the group has a
   committed offset for, matching `kafka-consumer-groups.sh --describe`'s
   default behavior. For each returned partition, `Consumer.
   QueryWatermarkOffsets` (bounded by the existing `AttemptTimeout`) gives
   the high watermark; `Lag = high - committedOffset`, clamped to `>= 0`
   (a negative value from an offset momentarily ahead of a just-moved
   watermark is clamped, not surfaced as negative lag). `TotalLag` is the
   sum across the group's partitions.
3. `MemberCount` comes from `DescribeConsumerGroupsAsync` (batched — all
   group IDs from step 1 in a single call) — `Members.Count` per group; an
   `Empty`-state group has 0 members but can still have committed offsets
   and therefore nonzero lag (the common "consumer died, backlog growing"
   case this feature exists to surface).

**This is a real cost, same caveat as Topics** (§4 of the Topics design):
one `ListConsumerGroupOffsetsAsync` plus N watermark queries per group,
every page load, for every group in the cluster. Fine at this console's
scale (admin tool, small/medium clusters); a cluster with hundreds of active
groups would need a lazier approach, explicitly out of scope.

`GetConsumerGroupDetailAsync(groupId)` does the same per-partition work for
one group, plus attributes each partition to a member: `DescribeConsumerGroups
Async([groupId])`, then for each `MemberDescription`, its `Assignment.
TopicPartitions` maps partitions to that member's `ClientId`/`Host`. A
partition with a committed offset but no current assignment (idle group, or
a partition an active member simply isn't assigned) shows `ConsumerClientId
= null`.

## 4. Offset reset

`ResetConsumerGroupOffsetAsync`:

1. Resolve the target offset for `mode`: `Earliest` → `Offset.Beginning`;
   `Latest` → `Offset.End`; `Offset` → the caller's literal value;
   `Timestamp` → `Consumer.OffsetsForTimes([new TopicPartitionTimestamp(tp,
   new Timestamp(timestamp.Value))], AttemptTimeout)`, taking the resolved
   offset from the single result (falls back to `Offset.End` if the broker
   reports no message at or after that timestamp, mirroring `OffsetsForTimes`'
   own documented `-1` convention).
2. `AdminClient.AlterConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartition
   Offsets(groupId, [new TopicPartitionOffset(topicPartition, resolvedOffset)])])`.
3. The broker rejects this when the group is not `Empty` (an active member
   holds the partition). This surfaces through `FriendlyKafkaError` like any
   other operation (§6) — the UI's client-side guard (§5) is the primary
   defense, this is the backstop for a state change between page load and
   click.

`ActionRisk.Destructive`, typed-confirm-on-prod — same gate as Delete Topic.
Resetting offsets can silently skip unprocessed messages (fast-forward) or
force reprocessing (rewind); both are effectively irreversible from the
consumer's perspective once it resumes.

## 5. UI

New nav item, `KafkaPlugin.NavItems`:

```csharp
new("Consumer Groups", "/p/kafka/consumer-groups"),
```

`Pages/ConsumerGroups.razor` at `/p/kafka/consumer-groups` — `MudTable`,
styled like `Topics.razor`: cluster picker (`MudMenu`), filter textbox,
columns `Group ID | State | Members | Total Lag | Actions`. A row whose
`TotalLag` exceeds the dashboard problem threshold (§6) gets the same kind
of warning chip `Topics.razor` uses for low replication factor. Actions
column: **View** (link to detail page).

`Pages/ConsumerGroupDetail.razor` at `/p/kafka/consumer-groups/{GroupId}`:

- Header: group ID, a state chip (color-coded: `Stable`/`Empty` neutral,
  `PreparingRebalance`/`CompletingRebalance` informational, `Dead` error).
- Members table: `Client ID | Host | Assigned Partitions`.
- Partitions table: `Topic | Partition | Committed Offset | High Watermark |
  Lag | Consumer | Actions`. `Consumer` column shows `{ClientId}@{Host}` or
  "idle" when unassigned. Actions column: **Reset** button, opening
  `ResetOffsetDialog.razor`.
- **Reset is disabled (with a tooltip explaining why) whenever the group's
  `State != "Empty"`** — the client-side guard from §4. The page's own
  loaded `ConsumerGroupDetail.State` drives this, refreshed on every page
  load/Fetch, same "read the authoritative value fresh, don't trust stale
  client state for a safety gate" spirit as `IsProd` always being resolved
  server-side (`docs/design.md` §6.1).

`ResetOffsetDialog.razor` (topic/partition pre-filled from the row it was
opened from): a mode selector (`Earliest`/`Latest`/`Offset`/`Timestamp`)
with the matching input revealed per mode (numeric offset field; date/time
picker for timestamp). Confirms via `IConfirmationService.ConfirmAsync`
before calling the command handler, same as Delete Topic.

## 6. Dashboard problems and nav badge

`KafkaPlugin.GetDashboardProblemsAsync` (currently SDK default no-op):

```csharp
private const long LagProblemThreshold = 10_000;

public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
    Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
{
    var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
    return groups
        .Where(g => g.TotalLag > LagProblemThreshold)
        .Select(g => new PluginDashboardProblem(
            "Warning",
            $"Consumer group '{g.GroupId}' has high lag",
            $"{g.TotalLag:N0} messages behind",
            $"/p/kafka/consumer-groups/{g.GroupId}"))
        .ToList();
}
```

`"Warning"` matches Service Bus's only-ever-used severity value (`docs/
design.md` §6.2's `DashboardProblems` helper never emits `"Error"`); nothing
here rises to that level. No `MetricHistoryStore`/trend tracking — this is a
fixed absolute threshold on the current snapshot, not a growth signal (no
history persistence exists for Kafka groups yet, and building one is out of
scope for this slice).

`KafkaPlugin.GetNavBadgeAsync`:

```csharp
public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
    navItemHref == "/p/kafka/consumer-groups"
        ? GetConsumerGroupBadgeAsync(connectionString, ct)
        : Task.FromResult<int?>(null);

private static async Task<int?> GetConsumerGroupBadgeAsync(string connectionString, CancellationToken ct)
{
    var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
    var problemCount = groups.Count(g => g.TotalLag > LagProblemThreshold);
    return problemCount > 0 ? problemCount : null;
}
```

Returning `null` (never `0`) when there's nothing to flag is required, not
stylistic: `NavMenu.razor` renders a visible "0" badge for a literal `0` and
only omits the badge for `null` — confirmed against `ServiceBusPlugin`'s
identical `GetDeadLetterBadgeAsync` pattern.

## 7. Handlers

New `ConsumerGroups/` folder, same shape as `Topics/`/`Messages/`:

- `ListConsumerGroupsQueryHandler.HandleAsync(connectionId, ct)`.
- `GetConsumerGroupDetailQueryHandler.HandleAsync(connectionId, groupId, ct)`.
- `ResetConsumerGroupOffsetCommandHandler` — `record
  ResetConsumerGroupOffsetCommand(ConnectionId, ConnectionName, IsProd,
  GroupId, TopicName, Partition, OffsetResetMode Mode, long? Offset,
  DateTimeOffset? Timestamp)`; audits `kafka.consumergroup.resetoffset`,
  `ActionRisk.Destructive`, target `{connectionName}/{groupId}/{topicName}-
  {partition}` (same `{connectionName}/{entity}` shape every other handler's
  audit target uses).

All three registered in `KafkaPlugin.ConfigureServices`, alongside the
existing five. `Contribution` becomes `new(PageCount: 4, ActionCount: 5)`
(two new pages: list + detail; one new action: reset).

## 8. Error handling

`FriendlyKafkaError` gains mappings for the error codes consumer-group
operations can actually hit — confirmed against the installed `Confluent.
Kafka` 2.15.1 `ErrorCode` enum at implementation time (same "decompiled, not
assumed" bar the original plan held itself to, §7 of the Topics design):
group-not-found, and whatever `ErrorCode` the broker returns for "cannot
alter offsets of a non-Empty group" (surfaced as a plain readable message —
the UI-level guard in §5 is expected to prevent most users from hitting this
path at all). Unmapped codes fall through to the existing generic fallback
(`ex.Error.Reason`, capped).

## 9. Testing

Same layered approach as the Topics plan (`docs/superpowers/specs/2026-09-
16-kafka-topics-plugin-design.md` §8):

- `KafkaConfigParser`/pure-logic pieces: none new here beyond what §3/§4
  already have covered.
- Handler tests (NSubstitute `IKafkaOperations`): connection-not-found,
  happy path, `FriendlyKafkaError` mapping, and — for the reset handler —
  offset/timestamp-required validation, for all three handlers.
- `GetDashboardProblemsAsync`/`GetNavBadgeAsync` threshold logic: unit
  tested directly against `KafkaPlugin` with a substitute `IKafkaOperations`
  returning fixed `ConsumerGroupSummary` lists (above/below/exactly-at
  threshold).
- Component tests (bUnit): `ConsumerGroups.razor` (list rendering, warning
  chip threshold), `ConsumerGroupDetail.razor` (partition table rendering,
  Reset button disabled when `State != "Empty"`), `ResetOffsetDialog.razor`
  (mode switching reveals the right input, confirmation gating on a
  prod-tagged connection).
- `ConfluentKafkaOperations`'s three new methods get light coverage by
  necessity, same reasoning as every other real-SDK-backed method in this
  plugin (§8 of the Topics design) — value is being a substitutable seam.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every
  commit.

Integration testing against a real broker stays deferred, same trigger
condition as the Topics plan (§8 there): picked up once the Kafka plugin's
shape has proven out across both this plan and the dead-letter plan alike.

## 10. Out of scope (this plan)

- Delete group (`DeleteGroupsAsync`) and delete-group-offsets
  (`DeleteConsumerGroupOffsetsAsync`) — the original scope line (§1) says
  "list groups, per-topic/partition lag, offset reset" only.
- Bulk multi-partition reset in one action — each partition's Reset button
  makes one call; no multi-select-and-reset-all convenience.
- Lag trend/history and a "growing" signal — no persistence layer for Kafka
  group lag exists; the dashboard problem is a fixed-threshold snapshot, not
  a rate-of-change signal like Service Bus's `MetricHistoryStore`-backed
  dead-letter growth detection.
- ACL/authorized-operations display (`ConsumerGroupDescription.
  AuthorizedOperations`) — not needed for this feature's purpose.
- Filtering out "internal" consumer groups — unlike topics, Kafka has no
  fixed naming convention (like `__`) for internal groups across all
  tooling (Connect/ksqlDB/etc. use varying conventions), so no filter is
  applied; every group the broker reports is listed.
- Dead-letter handling (DLQ-topic convention) — separate plan (§1, item 3
  of the original Topics design's full scope list).
