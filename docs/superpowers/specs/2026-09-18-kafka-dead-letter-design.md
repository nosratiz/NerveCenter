# Kafka plugin — dead-letter handling — Design

Status: draft, 2026-09-18.

## 1. Context

Third and final slice of the Kafka plugin's original full scope (`docs/superpowers/specs/
2026-09-16-kafka-topics-plugin-design.md` §1, item 3): dead-letter handling via the common
DLQ-topic convention, since Kafka has no native dead-letter queue. Topics/Peek/Produce (item 1)
and consumer group management (item 2, `docs/superpowers/specs/2026-09-17-kafka-consumer-groups-
design.md`) already shipped; this plan reuses their conventions directly — the `IKafkaOperations`
seam, `ConfluentKafkaOperations`'s eager-aggregation pattern, `FriendlyKafkaError`, and the
"one seam, reused by the page, the badge, and the dashboard problem" principle Service Bus's
`ListDeadLetterEntriesAsync` established (`docs/design.md` §6.3).

**Kafka has no native DLQ.** A "dead-letter topic" here means only a topic whose name follows a
naming convention — there is no broker-level concept tying it to any other topic. This plan
recognizes exactly one convention: a topic named `{original}-dlq` is the dead-letter topic for
`{original}`. Detection is a pure suffix check, `name.EndsWith("-dlq", StringComparison.Ordinal)`
— same shape as `Topics.razor`'s existing `IsInternal` (`name.StartsWith("__", ...)`), just a
suffix instead of a prefix.

Because Kafka has no per-message ack/nack and no way to delete an individual message or a message
range from a topic, this plan is deliberately **read-only**: list DLQ topics and their retained
counts, and peek into them (reusing the existing `Peek.razor` unmodified — a DLQ topic is just a
topic to that page, nothing about it is peek-aware). Resubmit and Purge, which Service Bus's
Dead-letter overview has, are explicitly out of scope (§9) — Kafka can't delete individual
messages, so "purge" has no clean analogue, and "resubmit" would need new message-header plumbing
that doesn't exist anywhere in this plugin yet.

## 2. Data type and `IKafkaOperations` addition

```csharp
// Client/DeadLetterTopicSummary.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ApproximateMessageCount carries the same caveat TopicSummary's does (design spec 2026-09-16
/// §4): "currently retained by the topic's retention policy," not "unprocessed backlog" -- Kafka
/// has no such concept. OldestMessageTimestamp is null when every partition is empty (nothing
/// retained right now); otherwise the earliest message's timestamp across all of this topic's
/// partitions.
/// </summary>
public sealed record DeadLetterTopicSummary(
    string DlqTopicName, string OriginalTopicName, int PartitionCount,
    long ApproximateMessageCount, DateTimeOffset? OldestMessageTimestamp);
```

One new `IKafkaOperations` method:

```csharp
Task<IReadOnlyList<DeadLetterTopicSummary>> ListDeadLetterTopicsAsync(string config, CancellationToken ct = default);
```

This is the one seam every other part of this plan reuses: the overview page, `GetNavBadgeAsync`,
`GetDashboardProblemsAsync`, and `GetOldestDeadLetterAsync` all call it — same "one seam, reused"
principle `docs/design.md` §6.3 states for Service Bus's `ListDeadLetterEntriesAsync`.

## 3. `ConfluentKafkaOperations.ListDeadLetterTopicsAsync`

1. `AdminClient.GetMetadata(AttemptTimeout)`, filter `metadata.Topics` to those whose name ends
   with `-dlq`. Empty filter result returns `[]` immediately (same early-return `ListConsumerGroups
   Async` already uses) — no consumer is built at all when there's nothing to inspect.
2. Per matching topic, per partition (same eager-aggregation acceptance as Topics'/consumer
   groups' own per-partition walks — `docs/superpowers/specs/2026-09-16-kafka-topics-plugin-
   design.md` §4): `QueryWatermarkOffsets` gives `(low, high)`; `count = high - low`, summed into
   the topic's `ApproximateMessageCount`. For a partition with `count > 0`, `Assign` a single
   throwaway consumer (same `Guid.NewGuid()` never-reused `group.id` convention every read-only
   operation in this class already follows) to `TopicPartitionOffset(tp, new Offset(low))`, `Consume`
   once (bounded by `AttemptTimeout`), and if a real message came back (not a poll timeout, not
   `IsPartitionEOF`), take its timestamp as a candidate for the topic's `OldestMessageTimestamp`
   (the minimum across the topic's partitions). `Unassign` before moving to the next partition —
   one consumer instance is reused sequentially across every partition of every DLQ topic in this
   call, never committing anything, exactly like `PeekMessagesAsync`'s consumer is never shared
   across separate calls but *is* the single non-committing instrument for its own call.
3. `OriginalTopicName` is the DLQ name with its trailing `-dlq` (4 characters) removed —
   `dlqName[..^4]`. No validation that a topic by that original name actually exists; this plan
   doesn't need it to (the overview page shows it as informational text, not a link).

This is a genuinely more expensive walk than `ListTopicsAsync`'s (one extra `Consume` per nonempty
partition), but it only runs over DLQ-suffixed topics, which are expected to be a small minority
of a cluster's topics — same "fine at this console's scale, not a monitoring system" acceptance
already made twice in this plugin.

## 4. UI

New nav item:

```csharp
new("Dead-letter", "/p/kafka/dead-letter"),
```

`Pages/DeadLetterOverview.razor` at `/p/kafka/dead-letter` — **cross-connection**, no cluster
picker, matching Service Bus's Dead-letter overview shape rather than this plugin's own Topics/
Consumer-Groups per-connection pattern (dead-letter monitoring is a "check everything at once"
task, not a per-cluster one). One `MudTable`: `Connection | DLQ Topic | Original Topic | Messages
(approx) | Actions`. Actions column: **Peek** link, reusing `Topics.razor`'s existing `PeekUrl`
shape (`/p/kafka/topics/{topicName}/peek?connectionId={id}&partitionCount={n}`) unmodified — the
DLQ topic name is passed as `{TopicName}`, nothing on `Peek.razor` needs to know it's a DLQ topic.

`Topics.razor` gains a small addition: a topic whose name matches the DLQ suffix gets a `DLQ` chip
next to its name, same visual slot the existing `internal`/`RF 1` chips already use (mutually
exclusive with those — a topic is internal, low-replication, or a DLQ topic, not stacked badges,
matching the existing `if`/`else if` structure). This is the only change to an existing file in
this plan.

## 5. Handler

New `DeadLetter/` folder, one handler — cross-connection, so its shape differs from every other
handler in this plugin (which all take a single `connectionId`):

```csharp
public sealed record DeadLetterOverviewEntry(
    Guid ConnectionId, string ConnectionName, string DlqTopicName, string OriginalTopicName,
    long ApproximateMessageCount);

public sealed class ListDeadLetterOverviewQueryHandler(
    IKafkaOperations operations, IConnectionProvider connections, ILogger<ListDeadLetterOverviewQueryHandler> logger)
{
    public async Task<IReadOnlyList<DeadLetterOverviewEntry>> HandleAsync(CancellationToken ct = default)
    {
        var kafkaConnections = await connections.ListAsync("kafka", ct);
        var result = new List<DeadLetterOverviewEntry>();
        foreach (var connection in kafkaConnections)
        {
            try
            {
                var secret = await connections.GetSecretAsync(connection.Id, ct);
                if (secret is null)
                {
                    continue;
                }

                var topics = await operations.ListDeadLetterTopicsAsync(secret, ct);
                result.AddRange(topics.Select(t => new DeadLetterOverviewEntry(
                    connection.Id, connection.Name, t.DlqTopicName, t.OriginalTopicName, t.ApproximateMessageCount)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing dead-letter topics for connection {ConnectionId} failed; skipping it.", connection.Id);
            }
        }

        return result;
    }
}
```

No `PluginResult` wrapper — deliberately best-effort per connection (one unreachable cluster
shouldn't blank the whole page), same shape and same reasoning as Service Bus's own
`ListDeadLetterOverviewQueryHandler`. Nothing here writes an audit row: this is a query, and unlike
Service Bus's dead-letter page there is no Resubmit/Purge command in this plan to audit.

## 6. Dashboard problems, nav badge, oldest dead-letter

`KafkaPlugin.GetDashboardProblemsAsync` gains dead-letter problems, merged alongside the existing
consumer-group-lag problems (both share one method, same as Service Bus's own `GetDashboardProblems
Async` merges dead-letter-backlog and disabled-subscription problems into one list):

```csharp
var dlqTopics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
problems.AddRange(dlqTopics
    .Where(t => t.ApproximateMessageCount > 0)
    .Select(t => new PluginDashboardProblem(
        "Warning",
        $"Dead-letter topic '{t.DlqTopicName}' has messages",
        $"{t.ApproximateMessageCount:N0} messages retained",
        "/p/kafka/dead-letter")));
```

**Threshold is `> 0`, not a large magnitude** — unlike consumer-group lag (where some lag is
normal under load, so only a large backlog is worth a warning), any nonzero dead-letter count is
inherently abnormal: something failed permanently. This matches Service Bus's own
`DeadLetterMessageCount > 0` semantics exactly, not the fixed-large-threshold approach the
consumer-groups plan used for lag.

`GetNavBadgeAsync` gains a branch for the new nav href:

```csharp
public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
    navItemHref switch
    {
        ConsumerGroupsNavHref => GetConsumerGroupBadgeAsync(connectionString, ct),
        "/p/kafka/dead-letter" => GetDeadLetterBadgeAsync(connectionString, ct),
        _ => Task.FromResult<int?>(null),
    };

private static async Task<int?> GetDeadLetterBadgeAsync(string connectionString, CancellationToken ct)
{
    var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
    var total = topics.Sum(t => t.ApproximateMessageCount);
    return total > 0 ? (int)total : null;
}
```

Same `null`-not-`0` rule as the consumer-group badge (§6 of the consumer-groups design) and as
Service Bus's own `GetDeadLetterBadgeAsync`.

New `GetOldestDeadLetterAsync` override (currently the SDK no-op default):

```csharp
public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
    Guid connectionId, string connectionString, CancellationToken ct = default)
{
    var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
    var oldest = topics
        .Where(t => t.OldestMessageTimestamp is not null)
        .OrderBy(t => t.OldestMessageTimestamp)
        .FirstOrDefault();

    return oldest is null
        ? null
        : new OldestDeadLetterEntry(oldest.DlqTopicName, oldest.OldestMessageTimestamp!.Value, oldest.ApproximateMessageCount);
}
```

`ResourceName` is the DLQ topic's own name (not the original topic) and `DeadLetterCount` is that
topic's own `ApproximateMessageCount` — matching how Service Bus's implementation reports a single
queue's name and that queue's own count, not an aggregate.

## 7. Error handling

No new `FriendlyKafkaError` mappings — `ListDeadLetterTopicsAsync` only calls operations
(`GetMetadata`, `QueryWatermarkOffsets`, `Consume`) already covered by the existing mapped error
codes (broker unreachable, authentication failed, topic not found).

## 8. Testing

Same layered approach as both prior plans:

- `ConfluentKafkaOperations.ListDeadLetterTopicsAsync` itself gets light coverage by necessity —
  can't be meaningfully unit-tested without a real or emulated broker. The one pure, extractable
  piece is the DLQ-name/original-name pair: `internal static bool IsDlqTopic(string name)` and
  `internal static string OriginalTopicName(string dlqTopicName)`, both `internal static` for the
  same testability reason `ComputeLag`/`IsEndOfPartition`/`Decode` already are in this class —
  fully unit tested (suffix match, suffix strip, a name that's just `"-dlq"` with nothing before
  it, a name containing but not ending with `-dlq`).
- `ListDeadLetterOverviewQueryHandler`: unit tested against a substitute `IKafkaOperations` and
  `IConnectionProvider` — zero connections, one connection with DLQ topics, one connection whose
  `ListDeadLetterTopicsAsync` throws (skipped, logged, other connections' results still returned).
- `GetDashboardProblemsAsync`/`GetNavBadgeAsync`/`GetOldestDeadLetterAsync` threshold/selection
  logic: same approach the consumer-groups plan used for `HasHighLag` — extract the `> 0` filter
  and the "pick the minimum-timestamp topic" selection as their own `internal static` helpers,
  unit tested directly against fixed `DeadLetterTopicSummary` lists.
- Component tests (bUnit): `DeadLetterOverview.razor` (cross-connection rendering, a connection
  whose call fails doesn't blank the page, Peek link shape), `Topics.razor`'s new DLQ chip
  (extending the existing `TopicsPageTests.cs`, not a new test file).
- **This plan's real-broker risk, learned the hard way from the consumer-groups plan:** that
  plan's unit-test suite passed 393/393 while shipping a genuine bug (`ResetConsumerGroupOffset
  Async` passing sentinel offset values to an API that rejects them) that only manual, live testing
  against a real broker caught — because every unit test necessarily substitutes `IKafkaOperations`
  and therefore can't exercise the real `Confluent.Kafka` call shape. This plan's riskiest new
  real-broker code is the repeated `Assign`/`Consume`/`Unassign` sequence on one reused consumer
  instance across every DLQ topic's partitions (§3) — a pattern nothing else in this plugin does
  (every existing `Assign` call happens once per consumer, never repeated). **Before this plan is
  considered done, manually verify `ListDeadLetterTopicsAsync` against a real broker** (produce a
  message to a topic like `orders-dlq`, confirm the overview page shows the right count and oldest
  timestamp, confirm a second DLQ topic's partitions aren't affected by the first's `Assign`/
  `Unassign` cycle) — the same kind of live check that caught the offset-reset bug, not a
  substitute for it.
- Gate: `dotnet build SbConsole.slnx -warnaserror` and `dotnet test` green before every commit.

## 9. Out of scope (this plan)

- **Resubmit** — re-producing a DLQ message onto its original topic. Needs new message-header
  plumbing (`IKafkaOperations`, `ConfluentKafkaOperations`, `KafkaMessageSummary` all currently
  carry no headers at all — confirmed by grep, nothing produces or consumes `Message.Headers`
  anywhere in this plugin) that doesn't exist yet, and wouldn't remove the original from the DLQ
  topic the way Service Bus's resubmit does (Kafka can't delete an individual message) — a
  meaningfully different feature, not a natural extension of this plan.
- **Purge** — Kafka has no way to delete an individual message or a message range from a topic.
  The closest analogue (deleting and recreating the DLQ topic, or shortening its retention) is a
  much more destructive, differently-scoped action than Service Bus's purge and needs its own
  design if ever built.
- Any DLQ naming convention other than the `-dlq` suffix (no `.DLQ`, no configurable pattern).
- Alerting/trend history for dead-letter counts — same fixed-snapshot-threshold reasoning already
  applied to consumer-group lag (§6 of the consumer-groups design): no persistence layer for
  historical Kafka metrics exists yet.
- Validating that a DLQ topic's derived `OriginalTopicName` actually exists as a real topic.
