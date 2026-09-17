# Kafka consumer group management — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add consumer group management to the Kafka plugin (`SbConsole.Plugins.Kafka`) — list consumer groups, show per-topic/partition lag, and allow offset reset — the second slice of the Kafka plugin's full scope, following Topics/Peek/Produce.

**Architecture:** New `ConsumerGroups/` handler folder and `Client/` data types follow the exact shape Topics/Messages already established; three new `IKafkaOperations` methods implemented in the existing `ConfluentKafkaOperations`; two new Razor pages (`ConsumerGroups.razor` list, `ConsumerGroupDetail.razor` detail) plus `ResetOffsetDialog.razor`; `KafkaPlugin` gains a nav item and its first real `GetDashboardProblemsAsync`/`GetNavBadgeAsync` overrides. Full design: `docs/superpowers/specs/2026-09-17-kafka-consumer-groups-design.md`.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor 9.9.0, `Confluent.Kafka` 2.15.1, xUnit + FluentAssertions 7.x + NSubstitute + bUnit.

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (`Directory.Build.props`, already enforced).
- Gate before every commit: `dotnet build SbConsole.slnx -warnaserror && dotnet test` both green.
- `IKafkaOperations` methods take the connection config string as a parameter, never pre-configured at construction.
- `Confluent.Kafka`'s `AdminClient.ListConsumerGroupsAsync`/`DescribeConsumerGroupsAsync`/`ListConsumerGroupOffsetsAsync`/`AlterConsumerGroupOffsetsAsync` are genuinely `async Task`-returning (unlike `GetMetadata`/`QueryWatermarkOffsets`/`Consume`, which are synchronous/blocking) — await them directly, do not wrap them in `Task.Run`. Only the `IConsumer.QueryWatermarkOffsets` calls need `Task.Run` (same reason `ListTopicsAsync` already wraps its watermark loop).
- Every one of the four new Admin options types (`ListConsumerGroupsOptions`, `DescribeConsumerGroupsOptions`, `ListConsumerGroupOffsetsOptions`, `AlterConsumerGroupOffsetsOptions`) has a `TimeSpan? RequestTimeout` property — set it to `ConfluentKafkaOperations.AttemptTimeout`, same role it plays on `CreateTopicsOptions`/`DeleteTopicsOptions` already.
- `ConsumerGroupTopicPartitions(string group, List<TopicPartition> topicPartitions)`: passing `null` for `topicPartitions` is passed straight through to librdkafka unmodified (confirmed by decompiling `SafeKafkaHandle.ListConsumerGroupOffsets`/`GetCTopicPartitionList`) and is librdkafka's documented way to mean "every partition the group has a committed offset for" (the same behavior `kafka-consumer-groups.sh --describe` uses with no `--topic` filter) — this native-level behavior isn't asserted by any .NET doc comment, so it can't be unit-tested here; flag it with a code comment, not a test.
- Commands write audit rows via `IAuditScope`; queries never write.
- The offset-reset action is `ActionRisk.Destructive`, typed-confirm-on-prod via `IConfirmationService`, same gate as Delete Topic. It is also blocked client-side whenever the loaded group's `State != "Empty"` — the broker itself is the backstop (a state change between page load and click surfaces as a normal `FriendlyKafkaError`).
- Raw `Confluent.Kafka` exception text never reaches a snackbar or an audit row — every catch site routes through `FriendlyKafkaError`.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

---

### Task 1: Data contracts, `IKafkaOperations` extension, `FriendlyKafkaError` mappings

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Client/ConsumerGroupSummary.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/TopicPartitionRef.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/ConsumerGroupMember.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/ConsumerGroupPartitionLag.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/ConsumerGroupDetail.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/OffsetResetMode.cs`
- Modify: `src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs`
- Modify: `src/SbConsole.Plugins.Kafka/Client/FriendlyKafkaError.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/FriendlyKafkaErrorTests.cs`

**Interfaces:**
- Consumes: nothing new (references only `Confluent.Kafka.ErrorCode`, already-existing `FriendlyKafkaError`/`FriendlyError`).
- Produces (used by every later task): the six new record/enum types below; `IKafkaOperations.ListConsumerGroupsAsync`, `GetConsumerGroupDetailAsync`, `ResetConsumerGroupOffsetAsync` signatures; two new `FriendlyKafkaError` mappings (`GroupIdNotFound`, and the "group not idle" cluster of codes).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/FriendlyKafkaErrorTests.cs
// Add these two [InlineData] rows to the existing [Theory] Known_error_codes_map_to_a_fixed_readable_message
// (the method and its other rows already exist — this only adds rows and does not change the method body):
    [InlineData(ErrorCode.GroupIdNotFound, "Consumer group not found")]
    [InlineData(ErrorCode.NonEmptyGroup, "Consumer group has active members; wait until it is idle before resetting offsets")]
```

The full updated theory method (replace the existing one in the file):

```csharp
    [Theory]
    [InlineData(ErrorCode.Local_AllBrokersDown, "Broker(s) unreachable")]
    [InlineData(ErrorCode.Local_Transport, "Broker(s) unreachable")]
    [InlineData(ErrorCode.BrokerNotAvailable, "Broker(s) unreachable")]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "Authentication failed")]
    [InlineData(ErrorCode.TopicAuthorizationFailed, "Authentication failed")]
    [InlineData(ErrorCode.UnknownTopicOrPart, "Topic not found")]
    [InlineData(ErrorCode.GroupIdNotFound, "Consumer group not found")]
    [InlineData(ErrorCode.NonEmptyGroup, "Consumer group has active members; wait until it is idle before resetting offsets")]
    [InlineData(ErrorCode.UnknownMemberId, "Consumer group has active members; wait until it is idle before resetting offsets")]
    [InlineData(ErrorCode.RebalanceInProgress, "Consumer group has active members; wait until it is idle before resetting offsets")]
    public void Known_error_codes_map_to_a_fixed_readable_message(ErrorCode code, string expected)
    {
        var ex = new KafkaException(new Error(code, "raw librdkafka reason text that must never reach the UI"));

        FriendlyKafkaError.From(ex).Should().Be(expected);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter FriendlyKafkaErrorTests`
Expected: FAIL — the new `[InlineData]` rows for `GroupIdNotFound`/`NonEmptyGroup`/`UnknownMemberId`/`RebalanceInProgress` fall through to the generic `ex.Error.Reason` fallback instead of the expected fixed messages.

- [ ] **Step 3: Add the data contracts**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConsumerGroupSummary.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// TotalLag is the sum of (high watermark - committed offset) across every topic-partition this
/// group has a committed offset for, clamped to >= 0 per partition. State is a Confluent.Kafka
/// ConsumerGroupState's ToString() (e.g. "Empty", "Stable", "Dead") -- kept as a plain string here
/// so this type has no Confluent.Kafka dependency at the IKafkaOperations seam, same reasoning
/// TopicSummary/KafkaMessageSummary already follow.
/// </summary>
public sealed record ConsumerGroupSummary(string GroupId, string State, int MemberCount, long TotalLag);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/TopicPartitionRef.cs
namespace SbConsole.Plugins.Kafka.Client;

public sealed record TopicPartitionRef(string TopicName, int Partition);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConsumerGroupMember.cs
namespace SbConsole.Plugins.Kafka.Client;

public sealed record ConsumerGroupMember(string ClientId, string? Host, IReadOnlyList<TopicPartitionRef> AssignedPartitions);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConsumerGroupPartitionLag.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ConsumerClientId/ConsumerHost are null when the partition has a committed offset but no member
/// is currently assigned to it (an idle group, or a partition simply unassigned right now) -- see
/// design spec §3.
/// </summary>
public sealed record ConsumerGroupPartitionLag(
    string TopicName, int Partition, long CommittedOffset, long HighWatermark, long Lag,
    string? ConsumerClientId, string? ConsumerHost);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConsumerGroupDetail.cs
namespace SbConsole.Plugins.Kafka.Client;

public sealed record ConsumerGroupDetail(
    string GroupId, string State,
    IReadOnlyList<ConsumerGroupPartitionLag> Partitions,
    IReadOnlyList<ConsumerGroupMember> Members);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/OffsetResetMode.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Deliberately separate from PeekStart (Earliest/Latest/Offset) even though they overlap -- Peek
/// never needs a Timestamp mode, and coupling the two would force Peek's UI to handle a mode it
/// can't use. See design spec §2.
/// </summary>
public enum OffsetResetMode { Earliest, Latest, Offset, Timestamp }
```

- [ ] **Step 4: Extend `IKafkaOperations`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs
// Add these three methods inside the existing interface, after ProduceMessageAsync:

    Task<IReadOnlyList<ConsumerGroupSummary>> ListConsumerGroupsAsync(string config, CancellationToken ct = default);

    Task<ConsumerGroupDetail> GetConsumerGroupDetailAsync(string config, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Destructive. offset is required (and only read) when mode is OffsetResetMode.Offset;
    /// timestamp is required (and only read) when mode is OffsetResetMode.Timestamp. The broker
    /// rejects this call while the group has an active member holding the partition -- callers are
    /// expected to check ConsumerGroupDetail.State == "Empty" first (see design spec §4-§5), but
    /// this method itself does not re-check state; a rejection surfaces as a normal exception.
    /// </summary>
    Task ResetConsumerGroupOffsetAsync(
        string config, string groupId, string topicName, int partition, OffsetResetMode mode,
        long? offset, DateTimeOffset? timestamp, CancellationToken ct = default);
```

- [ ] **Step 5: Extend `FriendlyKafkaError`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/FriendlyKafkaError.cs
// Replace the existing FromKafkaException method with:

    private static string FromKafkaException(KafkaException ex) => ex.Error.Code switch
    {
        ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Transport or ErrorCode.BrokerNotAvailable => "Broker(s) unreachable",
        ErrorCode.SaslAuthenticationFailed or ErrorCode.TopicAuthorizationFailed => "Authentication failed",
        ErrorCode.UnknownTopicOrPart => "Topic not found",
        ErrorCode.GroupIdNotFound => "Consumer group not found",
        ErrorCode.NonEmptyGroup or ErrorCode.UnknownMemberId or ErrorCode.RebalanceInProgress =>
            "Consumer group has active members; wait until it is idle before resetting offsets",
        _ => FriendlyError.Truncate(ex.Error.Reason) is { Length: > 0 } reason ? reason : FriendlyError.From(ex),
    };
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: FAIL — `ConfluentKafkaOperations` no longer compiles (it doesn't implement the three new `IKafkaOperations` members yet). This is expected; Tasks 2-4 add them.

Because `tests/SbConsole.Plugins.Kafka.Tests` references `SbConsole.Plugins.Kafka` (which now fails to build), `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter FriendlyKafkaErrorTests` cannot produce a green run yet either — the whole test project fails to build for the same reason. Confirm the new `[InlineData]` mappings are correct by inspection against the updated `FromKafkaException` switch instead; the first real green run of `FriendlyKafkaErrorTests` happens in Task 4's Step 4, once `ConfluentKafkaOperations` implements all of `IKafkaOperations` again. (You can still confirm the tests were RED before this step's implementation — capture that output — since at that point only the test file had changed and the interface/error-mapping code was still the old version that compiles.)

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client
git commit -m "$(cat <<'EOF'
feat(kafka): add consumer group data contracts and IKafkaOperations methods

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `ConfluentKafkaOperations.ListConsumerGroupsAsync`

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations`/`ConsumerGroupSummary` (Task 1), `CreateAdminClientConfig`/`CreateConsumerConfig`/`AttemptTimeout` (existing, same file).
- Produces: `ConfluentKafkaOperations.ListConsumerGroupsAsync`; `internal static long ComputeLag(long committedOffset, long highWatermark)` and `internal static async Task<IReadOnlyList<TopicPartitionOffsetError>> GetCommittedOffsetsAsync(IAdminClient admin, string groupId)` — both reused by Task 3's `GetConsumerGroupDetailAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
// Add this test class member (same file as the existing CreateAdminClientConfig/CreateConsumerConfig/
// CreateProducerConfig tests) -- ComputeLag is pure and needs no broker, same reasoning as those three:

    [Theory]
    [InlineData(90, 100, 10)]   // normal case: 10 messages behind
    [InlineData(100, 100, 0)]   // caught up
    [InlineData(105, 100, 0)]   // committed briefly ahead of a just-moved watermark -- clamped, not negative
    public void ComputeLag_clamps_to_zero_and_never_returns_negative(long committedOffset, long highWatermark, long expected)
    {
        ConfluentKafkaOperations.ComputeLag(committedOffset, highWatermark).Should().Be(expected);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ComputeLag_clamps_to_zero_and_never_returns_negative`
Expected: FAIL to compile — `ConfluentKafkaOperations.ComputeLag` does not exist yet.

- [ ] **Step 3: Implement `ListConsumerGroupsAsync` and its helpers**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
// Add these members to the ConfluentKafkaOperations class, after ProduceMessageAsync:

    // Extracted as a pure static function so the "clamp to >= 0" rule (design spec §3 -- a
    // committed offset can be momentarily ahead of a just-moved watermark) is unit-testable
    // without a real broker, same reasoning as IsEndOfPartition/Decode above.
    internal static long ComputeLag(long committedOffset, long highWatermark) => Math.Max(0, highWatermark - committedOffset);

    // Shared by ListConsumerGroupsAsync (below) and GetConsumerGroupDetailAsync (Task 3). Passing
    // null for topicPartitions is librdkafka's documented way to mean "every topic-partition this
    // group has a committed offset for" -- see this plan's Global Constraints; not asserted by any
    // .NET doc comment, so it isn't (and can't be) covered by a unit test.
    internal static async Task<IReadOnlyList<TopicPartitionOffsetError>> GetCommittedOffsetsAsync(IAdminClient admin, string groupId)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(groupId, null)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = AttemptTimeout });
        return results[0].Partitions;
    }

    public async Task<IReadOnlyList<ConsumerGroupSummary>> ListConsumerGroupsAsync(string config, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();

        var listResult = await admin.ListConsumerGroupsAsync(new ListConsumerGroupsOptions { RequestTimeout = AttemptTimeout });
        if (listResult.Valid.Count == 0)
        {
            return [];
        }

        var groupIds = listResult.Valid.Select(g => g.GroupId).ToList();
        var describeResult = await admin.DescribeConsumerGroupsAsync(groupIds, new DescribeConsumerGroupsOptions { RequestTimeout = AttemptTimeout });
        var memberCountByGroup = describeResult.ConsumerGroupDescriptions.ToDictionary(d => d.GroupId, d => d.Members.Count);

        // One throwaway consumer group, reused across every group's watermark lookups below -- same
        // "fresh, never-reused group.id, purely to ask the cluster for watermark offsets" convention
        // ListTopicsAsync already uses.
        using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();

        var summaries = new List<ConsumerGroupSummary>();
        foreach (var listing in listResult.Valid)
        {
            var offsets = await GetCommittedOffsetsAsync(admin, listing.GroupId);
            var totalLag = await Task.Run(() =>
            {
                long total = 0;
                foreach (var partition in offsets)
                {
                    if (partition.Error.IsError)
                    {
                        continue;
                    }

                    var watermarks = consumer.QueryWatermarkOffsets(partition.TopicPartition, AttemptTimeout);
                    total += ComputeLag(partition.Offset.Value, watermarks.High.Value);
                }

                return total;
            }, ct);

            summaries.Add(new ConsumerGroupSummary(listing.GroupId, listing.State.ToString(), memberCountByGroup.GetValueOrDefault(listing.GroupId, 0), totalLag));
        }

        return summaries;
    }
```

`ConfluentKafkaOperations` still won't implement `GetConsumerGroupDetailAsync`/`ResetConsumerGroupOffsetAsync` yet (Tasks 3-4) — the solution won't build clean until Task 4 finishes; that's expected across this multi-task slice, same as the Topics plan's own Task 4→6 sequence.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: PASS, including the 3 new `ComputeLag` cases (compiles despite `GetConsumerGroupDetailAsync`/`ResetConsumerGroupOffsetAsync` still missing, because the test project only references types the test file actually uses — the missing interface members only break `SbConsole.Plugins.Kafka`'s own build, not the test project's ability to run tests targeting members that do exist. If your toolchain instead reports a build failure for the whole solution, that's fine — proceed to Task 3 immediately, then re-run once Task 4 completes).

Run: `dotnet build src/SbConsole.Plugins.Kafka -warnaserror`
Expected: FAIL — `ConfluentKafkaOperations` does not yet implement all of `IKafkaOperations` (`GetConsumerGroupDetailAsync`, `ResetConsumerGroupOffsetAsync`). Confirms Task 2's own code compiles; the remaining errors are exactly the two methods Tasks 3-4 add next.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations.ListConsumerGroupsAsync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `ConfluentKafkaOperations.GetConsumerGroupDetailAsync`

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `ComputeLag`, `GetCommittedOffsetsAsync` (Task 2); `ConsumerGroupDetail`/`ConsumerGroupPartitionLag`/`ConsumerGroupMember`/`TopicPartitionRef` (Task 1).
- Produces: `ConfluentKafkaOperations.GetConsumerGroupDetailAsync`.

This task has no new pure logic worth a dedicated unit test beyond what Task 2 already covers (`ComputeLag`) — `GetConsumerGroupDetailAsync` is pure orchestration of already-tested pieces plus real `Confluent.Kafka` calls that can't run without a broker, same "light coverage by necessity" reasoning the design spec §9 states. Its correctness is exercised end-to-end by Task 6's handler tests (against a substitute `IKafkaOperations`) and, ultimately, by using the real page.

- [ ] **Step 1: Implement `GetConsumerGroupDetailAsync`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
// Add after ListConsumerGroupsAsync:

    public async Task<ConsumerGroupDetail> GetConsumerGroupDetailAsync(string config, string groupId, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();

        var describeResult = await admin.DescribeConsumerGroupsAsync([groupId], new DescribeConsumerGroupsOptions { RequestTimeout = AttemptTimeout });
        var description = describeResult.ConsumerGroupDescriptions[0];

        // Maps each currently-assigned TopicPartition to the member holding it -- a partition with
        // a committed offset but no entry here is idle (design spec §3).
        var assignedTo = new Dictionary<TopicPartition, (string? ClientId, string? Host)>();
        foreach (var member in description.Members)
        {
            foreach (var topicPartition in member.Assignment.TopicPartitions)
            {
                assignedTo[topicPartition] = (member.ClientId, member.Host);
            }
        }

        var offsets = await GetCommittedOffsetsAsync(admin, groupId);

        using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();
        var partitionLags = await Task.Run(() =>
        {
            var result = new List<ConsumerGroupPartitionLag>();
            foreach (var partition in offsets)
            {
                if (partition.Error.IsError)
                {
                    continue;
                }

                var watermarks = consumer.QueryWatermarkOffsets(partition.TopicPartition, AttemptTimeout);
                assignedTo.TryGetValue(partition.TopicPartition, out var member);
                result.Add(new ConsumerGroupPartitionLag(
                    partition.Topic, partition.Partition.Value, partition.Offset.Value, watermarks.High.Value,
                    ComputeLag(partition.Offset.Value, watermarks.High.Value), member.ClientId, member.Host));
            }

            return result;
        }, ct);

        var members = description.Members
            .Select(m => new ConsumerGroupMember(
                m.ClientId, m.Host,
                (IReadOnlyList<TopicPartitionRef>)m.Assignment.TopicPartitions
                    .Select(tp => new TopicPartitionRef(tp.Topic, tp.Partition.Value))
                    .ToList()))
            .ToList();

        return new ConsumerGroupDetail(groupId, description.State.ToString(), partitionLags, members);
    }
```

- [ ] **Step 2: Run tests to verify nothing broke**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: PASS (same tests as Task 2 — this task adds no new test, only implementation).

Run: `dotnet build src/SbConsole.Plugins.Kafka -warnaserror`
Expected: FAIL — `ResetConsumerGroupOffsetAsync` is still missing (Task 4). Confirms this task's own code compiles cleanly.

- [ ] **Step 3: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations.GetConsumerGroupDetailAsync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `ConfluentKafkaOperations.ResetConsumerGroupOffsetAsync`

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations.ResetConsumerGroupOffsetAsync` signature (Task 1).
- Produces: `ConfluentKafkaOperations.ResetConsumerGroupOffsetAsync`; `internal static Offset ResolveTimestampLookupResult(long offsetsForTimesResultValue)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
// Add this test class member:

    [Fact]
    public void ResolveTimestampLookupResult_falls_back_to_Latest_when_no_message_exists_at_or_after_the_timestamp()
    {
        // Kafka's ListOffsets protocol returns -1 when no message exists at/after the requested
        // timestamp -- Kafka's own "not found" sentinel (distinct from, though numerically equal
        // to, librdkafka's Offset.End constant). See design spec §4.
        ConfluentKafkaOperations.ResolveTimestampLookupResult(-1).Should().Be(Offset.End);
    }

    [Fact]
    public void ResolveTimestampLookupResult_returns_the_resolved_offset_when_a_message_was_found()
    {
        ConfluentKafkaOperations.ResolveTimestampLookupResult(4242).Should().Be(new Offset(4242));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ResolveTimestampLookupResult`
Expected: FAIL to compile — `ConfluentKafkaOperations.ResolveTimestampLookupResult` does not exist.

- [ ] **Step 3: Implement `ResetConsumerGroupOffsetAsync`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
// Add after GetConsumerGroupDetailAsync:

    // Extracted as a pure static function, same reasoning as ComputeLag/IsEndOfPartition/Decode --
    // unit-testable without a real broker.
    internal static Offset ResolveTimestampLookupResult(long offsetsForTimesResultValue) =>
        offsetsForTimesResultValue == -1 ? Offset.End : new Offset(offsetsForTimesResultValue);

    public async Task ResetConsumerGroupOffsetAsync(
        string config, string groupId, string topicName, int partition, OffsetResetMode mode,
        long? offset, DateTimeOffset? timestamp, CancellationToken ct = default)
    {
        var topicPartition = new TopicPartition(topicName, new Partition(partition));

        Offset resolvedOffset;
        if (mode == OffsetResetMode.Timestamp)
        {
            using var timestampConsumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();
            resolvedOffset = await Task.Run(() =>
            {
                var results = timestampConsumer.OffsetsForTimes(
                    [new TopicPartitionTimestamp(topicPartition, new Timestamp(timestamp!.Value))], AttemptTimeout);
                return ResolveTimestampLookupResult(results[0].Offset.Value);
            }, ct);
        }
        else
        {
            resolvedOffset = mode switch
            {
                OffsetResetMode.Earliest => Offset.Beginning,
                OffsetResetMode.Latest => Offset.End,
                OffsetResetMode.Offset => new Offset(offset!.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled OffsetResetMode."),
            };
        }

        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.AlterConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitionOffsets(groupId, [new TopicPartitionOffset(topicPartition, resolvedOffset)])],
            new AlterConsumerGroupOffsetsOptions { RequestTimeout = AttemptTimeout });
    }
```

`offset`/`timestamp` are asserted non-null with `!` here because `ResetConsumerGroupOffsetCommandHandler` (Task 5) validates their presence before ever calling this method — mirrors how `CreateTopicAsync`'s callers are trusted to have already validated their inputs.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: PASS, including the 2 new `ResolveTimestampLookupResult` cases.

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS — `ConfluentKafkaOperations` now implements every `IKafkaOperations` member again.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations.ResetConsumerGroupOffsetAsync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Handlers + `KafkaPlugin` wiring

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/ConsumerGroups/ListConsumerGroupsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/ConsumerGroups/GetConsumerGroupDetailQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/ConsumerGroups/ResetConsumerGroupOffsetCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/ListConsumerGroupsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/GetConsumerGroupDetailQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/ResetConsumerGroupOffsetCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations` (Tasks 1-4), `IConnectionProvider`/`IAuditScope`/`ActionRisk` (`SbConsole.Sdk`), `PluginResult`/`PluginResult<T>` (existing).
- Produces: `ListConsumerGroupsQueryHandler.HandleAsync(Guid connectionId, ct)`, `GetConsumerGroupDetailQueryHandler.HandleAsync(Guid connectionId, string groupId, ct)`, `ResetConsumerGroupOffsetCommandHandler.HandleAsync(ResetConsumerGroupOffsetCommand, ct)` and the `ResetConsumerGroupOffsetCommand` record — all three are what Task 6/7's pages inject; `KafkaPlugin.LagProblemThreshold` (`internal const long`, used by Task 6's page and Task 8's dashboard methods).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/ListConsumerGroupsQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class ListConsumerGroupsQueryHandlerTests
{
    [Fact]
    public async Task Returns_groups_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var groups = new[] { new ConsumerGroupSummary("order-processors", "Stable", 3, 120) };
        operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>()).Returns(groups);

        var result = await new ListConsumerGroupsQueryHandler(operations, connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(groups);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListConsumerGroupsQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ConsumerGroupSummary>>(new InvalidOperationException("cluster unreachable")));

        var result = await new ListConsumerGroupsQueryHandler(operations, connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("cluster unreachable");
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/GetConsumerGroupDetailQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class GetConsumerGroupDetailQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_groups_detail_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var detail = new ConsumerGroupDetail("order-processors", "Empty", [], []);
        operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>()).Returns(detail);

        var result = await new GetConsumerGroupDetailQueryHandler(operations, connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(connectionId, "order-processors");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(detail);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetConsumerGroupDetailQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "order-processors");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ConsumerGroupDetail>(new InvalidOperationException("group not found")));

        var result = await new GetConsumerGroupDetailQueryHandler(operations, connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(connectionId, "order-processors");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("group not found");
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/ConsumerGroups/ResetConsumerGroupOffsetCommandHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class ResetConsumerGroupOffsetCommandHandlerTests
{
    [Fact]
    public async Task Resets_the_offset_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(connectionId, "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Earliest, null, null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ResetConsumerGroupOffsetAsync(
            "bootstrap.servers=real:9092", "order-processors", "orders", 2, OffsetResetMode.Earliest, null, null, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("kafka.consumergroup.resetoffset", "kafka-dev/order-processors/orders-2", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Offset_mode_without_an_offset_value_fails_before_calling_the_operation()
    {
        var connections = Substitute.For<IConnectionProvider>();
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(Guid.NewGuid(), "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Offset, null, null));

        result.IsSuccess.Should().BeFalse();
        await operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Timestamp_mode_without_a_timestamp_fails_before_calling_the_operation()
    {
        var connections = Substitute.For<IConnectionProvider>();
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(Guid.NewGuid(), "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Timestamp, null, null));

        result.IsSuccess.Should().BeFalse();
        await operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ResetConsumerGroupOffsetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("group has active members")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(connectionId, "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Latest, null, null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("group has active members");
        await audit.Received(1).RecordAsync("kafka.consumergroup.resetoffset", "kafka-dev/order-processors/orders-2", ActionRisk.Destructive, false, "group has active members", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
// Replace the existing Declares_the_expected_identity_and_connection_kind test's assertions with:

    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new KafkaPlugin();

        plugin.Id.Should().Be("kafka");
        plugin.ConnectionKind.Should().Be("kafka");
        plugin.DisplayName.Should().Be("Apache Kafka");
        plugin.ConnectionKindDisplayName.Should().Be("Apache Kafka");
        plugin.NavItems.Should().Contain(n => n.Title == "Topics" && n.Href == "/p/kafka/topics");
        plugin.NavItems.Should().Contain(n => n.Title == "Consumer Groups" && n.Href == "/p/kafka/consumer-groups");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 5));
    }
```

(The existing `TestConnectionAsync_delegates_to_the_real_Kafka_client_and_never_throws` test is unchanged.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~ConsumerGroups|FullyQualifiedName~KafkaPluginTests"`
Expected: FAIL to compile — `ListConsumerGroupsQueryHandler`/`GetConsumerGroupDetailQueryHandler`/`ResetConsumerGroupOffsetCommandHandler`/`ResetConsumerGroupOffsetCommand` don't exist yet, and `KafkaPluginTests`'s new assertions fail against the current nav items/`Contribution`.

- [ ] **Step 3: Write the three handlers**

```csharp
// src/SbConsole.Plugins.Kafka/ConsumerGroups/ListConsumerGroupsQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed class ListConsumerGroupsQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<ListConsumerGroupsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<ConsumerGroupSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Fail("Connection not found.");
            }

            var groups = await operations.ListConsumerGroupsAsync(secret, ct);
            return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Ok(groups);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing consumer groups for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Fail(ex);
        }
    }
}
```

```csharp
// src/SbConsole.Plugins.Kafka/ConsumerGroups/GetConsumerGroupDetailQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed class GetConsumerGroupDetailQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<GetConsumerGroupDetailQueryHandler> logger)
{
    public async Task<PluginResult<ConsumerGroupDetail>> HandleAsync(Guid connectionId, string groupId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<ConsumerGroupDetail>.Fail("Connection not found.");
            }

            var detail = await operations.GetConsumerGroupDetailAsync(secret, groupId, ct);
            return PluginResult<ConsumerGroupDetail>.Ok(detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Describing consumer group {GroupId} failed.", groupId);
            return PluginResult<ConsumerGroupDetail>.Fail(ex);
        }
    }
}
```

```csharp
// src/SbConsole.Plugins.Kafka/ConsumerGroups/ResetConsumerGroupOffsetCommandHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed record ResetConsumerGroupOffsetCommand(
    Guid ConnectionId, string ConnectionName, string GroupId, string TopicName, int Partition,
    OffsetResetMode Mode, long? Offset, DateTimeOffset? Timestamp);

public sealed class ResetConsumerGroupOffsetCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ResetConsumerGroupOffsetCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(ResetConsumerGroupOffsetCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.GroupId}/{cmd.TopicName}-{cmd.Partition}";

        if (cmd.Mode == OffsetResetMode.Offset && cmd.Offset is null)
        {
            return PluginResult.Fail("An offset value is required for this reset mode.");
        }

        if (cmd.Mode == OffsetResetMode.Timestamp && cmd.Timestamp is null)
        {
            return PluginResult.Fail("A timestamp is required for this reset mode.");
        }

        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.ResetConsumerGroupOffsetAsync(secret, cmd.GroupId, cmd.TopicName, cmd.Partition, cmd.Mode, cmd.Offset, cmd.Timestamp, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resetting offset for {Target} failed.", target);
            await audit.RecordAsync("kafka.consumergroup.resetoffset", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("kafka.consumergroup.resetoffset", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [ ] **Step 4: Wire `KafkaPlugin`**

```csharp
// src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
// Replace the whole file with:
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka;

public sealed class KafkaPlugin : IPlugin
{
    // Shared by ConsumerGroups.razor's warning chip (Task 6) and GetDashboardProblemsAsync/
    // GetNavBadgeAsync (Task 8) so the threshold is defined exactly once. See design spec §6.
    internal const long LagProblemThreshold = 10_000;

    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
        new("Consumer Groups", "/p/kafka/consumer-groups"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Produce, Reset offset (5).
    // Pages: Topics, Peek, ConsumerGroups, ConsumerGroupDetail (4).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 5);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IKafkaOperations, ConfluentKafkaOperations>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
        services.AddScoped<Topics.GetConnectionEchoQueryHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.ProduceMessageCommandHandler>();
        services.AddScoped<ConsumerGroups.ListConsumerGroupsQueryHandler>();
        services.AddScoped<ConsumerGroups.GetConsumerGroupDetailQueryHandler>();
        services.AddScoped<ConsumerGroups.ResetConsumerGroupOffsetCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IKafkaOperations from at this
    // layer -- construct the real implementation directly, same as ServiceBusPlugin does.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var (topicCount, partitionCount) = await new ConfluentKafkaOperations().GetTopicCountsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topicCount),
            new PluginDashboardMetric("Partitions", partitionCount),
        ];
    }

    // GetDashboardProblemsAsync/GetNavBadgeAsync overrides are added in Task 8.
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~ConsumerGroups|FullyQualifiedName~KafkaPluginTests"`
Expected: PASS.

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Kafka tests/SbConsole.Plugins.Kafka.Tests
git commit -m "$(cat <<'EOF'
feat(kafka): add consumer group handlers and register the nav item

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `ConsumerGroups.razor` list page

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Pages/ConsumerGroups.razor`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupsPageTests.cs`

**Interfaces:**
- Consumes: `ListConsumerGroupsQueryHandler` (Task 5), `IConnectionProvider`/`ConnectionInfo` (`SbConsole.Sdk`), `KafkaPlugin.LagProblemThreshold` (Task 5).
- Produces: a routable page at `/p/kafka/consumer-groups`; Task 7's detail page is what its "View" link navigates to.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupsPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;
// KafkaPlugin lives in the parent namespace SbConsole.Plugins.Kafka -- this test file's own
// namespace (SbConsole.Plugins.Kafka.Tests.Pages) does not see it implicitly, same reason the page
// itself needs an explicit @using (see ConsumerGroups.razor below).
using SbConsole.Plugins.Kafka;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ConsumerGroupsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public ConsumerGroupsPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListConsumerGroupsQueryHandler>();
    }

    [Fact]
    public async Task Lists_groups_for_the_first_available_connection()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("order-processors", "Stable", 3, 120) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("order-processors");
        cut.Markup.Should().Contain("Stable");
        cut.Markup.Should().Contain("120");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task A_group_over_the_lag_threshold_shows_the_high_lag_chip()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("laggy-group", "Stable", 1, KafkaPlugin.LagProblemThreshold + 1) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("high-lag-badge");
    }

    [Fact]
    public async Task A_group_at_or_under_the_lag_threshold_shows_no_high_lag_chip()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("healthy-group", "Stable", 1, KafkaPlugin.LagProblemThreshold) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().NotContain("high-lag-badge");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConsumerGroupsPageTests`
Expected: FAIL to compile — `SbConsole.Plugins.Kafka.Pages.ConsumerGroups` does not exist.

- [ ] **Step 3: Write `ConsumerGroups.razor`**

```razor
@page "/p/kafka/consumer-groups"
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.ConsumerGroups
@using SbConsole.Sdk
@* KafkaPlugin lives in the parent namespace SbConsole.Plugins.Kafka -- this page's own generated
   namespace (SbConsole.Plugins.Kafka.Pages) does not see it implicitly, so LagProblemThreshold
   below needs this explicit @using even though _Imports.razor already covers MudBlazor/Authorize. *@
@using SbConsole.Plugins.Kafka
@inject IConnectionProvider Connections
@inject ListConsumerGroupsQueryHandler ListHandler
@inject ISnackbar Snackbar

<PageTitle>Consumer Groups</PageTitle>
<h1>Consumer Groups</h1>

@if (_connections.Count == 0)
{
    <MudAlert Severity="Severity.Info">No connections yet. Add an Apache Kafka connection to get started.</MudAlert>
}
else
{
    <div class="d-flex align-center flex-wrap gap-4 my-4">
        <div class="d-flex align-center gap-2">
            <MudText Typo="Typo.overline" Class="mud-text-secondary">Cluster</MudText>
            <MudMenu Class="cluster-menu">
                <ActivatorContent>
                    <div class="d-flex align-center gap-2" style="cursor:pointer">
                        <MudText Typo="Typo.subtitle1" Style="font-family:monospace">@SelectedConnection?.Name</MudText>
                        @if (SelectedConnection?.IsProd == true)
                        {
                            <MudChip T="string" Color="Color.Warning" Size="Size.Small">prod</MudChip>
                        }
                        <MudIcon Icon="@Icons.Material.Filled.ArrowDropDown" Size="Size.Small" />
                    </div>
                </ActivatorContent>
                <ChildContent>
                    @foreach (var connection in _connections)
                    {
                        <MudMenuItem Class="cluster-option" OnClick="@(() => OnConnectionChanged(connection.Id))">@connection.Name</MudMenuItem>
                    }
                </ChildContent>
            </MudMenu>
        </div>

        <MudTextField T="string" Class="group-filter" Placeholder="Filter groups..." @bind-Value="_filterText" Immediate="true" Style="max-width:220px" Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search" />

        <MudSpacer />
        @if (_loading)
        {
            <MudProgressCircular Class="groups-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudText Class="groups-summary mud-text-secondary mb-2" Typo="Typo.body2">@_groups.Count @(_groups.Count == 1 ? "group" : "groups")</MudText>

    <MudTable Items="FilteredGroups">
        <HeaderContent>
            <MudTh>Group ID</MudTh>
            <MudTh>State</MudTh>
            <MudTh>Members</MudTh>
            <MudTh>Total Lag</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.GroupId</MudTd>
            <MudTd>@context.State</MudTd>
            <MudTd>@context.MemberCount</MudTd>
            <MudTd>
                @context.TotalLag
                @if (context.TotalLag > KafkaPlugin.LagProblemThreshold)
                {
                    <MudChip T="string" Color="Color.Warning" Size="Size.Small" Class="high-lag-badge ms-2">high lag</MudChip>
                }
            </MudTd>
            <MudTd>
                <MudButton Class="view-group" Href="@DetailUrl(context.GroupId)">View</MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<ConsumerGroupSummary> _groups = [];
    private Guid _selectedConnectionId;
    private bool _loading;
    private string _filterText = "";

    private ConnectionInfo? SelectedConnection => _connections.Single(c => c.Id == _selectedConnectionId);

    private IEnumerable<ConsumerGroupSummary> FilteredGroups => string.IsNullOrWhiteSpace(_filterText)
        ? _groups
        : _groups.Where(g => g.GroupId.Contains(_filterText, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("kafka");
        if (_connections.Count > 0)
        {
            _selectedConnectionId = _connections[0].Id;
            await LoadGroupsAsync();
        }
    }

    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        await LoadGroupsAsync();
    }

    private async Task LoadGroupsAsync()
    {
        _loading = true;
        try
        {
            var result = await ListHandler.HandleAsync(_selectedConnectionId);
            if (result.IsSuccess)
            {
                _groups = result.Value!;
            }
            else
            {
                _groups = [];
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private string DetailUrl(string groupId)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/kafka/consumer-groups/{Uri.EscapeDataString(groupId)}?connectionId={connection.Id}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConsumerGroupsPageTests`
Expected: PASS (4 tests).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Pages/ConsumerGroups.razor tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupsPageTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add the Consumer Groups list page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: `ConsumerGroupDetail.razor` + `ResetOffsetDialog.razor`

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Pages/ConsumerGroupDetail.razor`
- Create: `src/SbConsole.Plugins.Kafka/Pages/ResetOffsetDialog.razor`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupDetailPageTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/ResetOffsetDialogTests.cs`

**Interfaces:**
- Consumes: `GetConsumerGroupDetailQueryHandler`, `ResetConsumerGroupOffsetCommandHandler`, `ResetConsumerGroupOffsetCommand` (Task 5), `IConnectionProvider`/`IConfirmationService` (`SbConsole.Sdk`).
- Produces: a routable page at `/p/kafka/consumer-groups/{GroupId}`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupDetailPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ConsumerGroupDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public ConsumerGroupDetailPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<GetConsumerGroupDetailQueryHandler>();
        Services.AddSingleton<ResetConsumerGroupOffsetCommandHandler>();
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
    }

    private Bunit.IRenderedComponent<SbConsole.Plugins.Kafka.Pages.ConsumerGroupDetail> RenderDetailPage() =>
        Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroupDetail>(parameters => parameters
            .Add(p => p.GroupId, "order-processors")
            .Add(p => p.ConnectionId, _connectionId));

    [Fact]
    public async Task Renders_partitions_and_members_for_an_Empty_group()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Empty",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, null, null)],
                []));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("idle");
    }

    [Fact]
    public async Task Reset_button_is_enabled_when_the_group_is_Empty()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Empty",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, null, null)],
                []));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".reset-offset").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Reset_button_is_disabled_when_the_group_is_not_Empty()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Stable",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, "consumer-1", "10.0.0.5")],
                [new ConsumerGroupMember("consumer-1", "10.0.0.5", [new TopicPartitionRef("orders", 0)])]));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".reset-offset").HasAttribute("disabled").Should().BeTrue();
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/ResetOffsetDialogTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ResetOffsetDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public ResetOffsetDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<ResetConsumerGroupOffsetCommandHandler>();
        _connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        _confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    // IMudDialogInstance is normally supplied by MudDialogProvider as a CascadingValue when a
    // dialog is opened through IDialogService -- rendering the dialog component directly (as this
    // test does, following TopicsPageTests'/ProduceMessageDialogTests' own convention for dialog
    // components) needs that cascading value supplied by hand instead.
    private IRenderedComponent<SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog> RenderDialog(Guid connectionId) =>
        Render<SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog>(parameters => parameters
            .Add(p => p.ConnectionId, connectionId)
            .Add(p => p.ConnectionName, "kafka-dev")
            .Add(p => p.IsProd, false)
            .Add(p => p.GroupId, "order-processors")
            .Add(p => p.TopicName, "orders")
            .Add(p => p.Partition, 2)
            .AddCascadingValue<IMudDialogInstance>(Substitute.For<IMudDialogInstance>()));

    [Fact]
    public async Task Confirms_before_calling_the_reset_handler()
    {
        var connectionId = Guid.NewGuid();

        var cut = RenderDialog(connectionId);
        cut.Find(".confirm-reset").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Reset offset for", "orders-2", false, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).ResetConsumerGroupOffsetAsync(
            "bootstrap.servers=real:9092", "order-processors", "orders", 2, OffsetResetMode.Latest, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_confirmation_never_calls_the_reset_handler()
    {
        _confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var connectionId = Guid.NewGuid();

        var cut = RenderDialog(connectionId);
        cut.Find(".confirm-reset").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~ConsumerGroupDetailPageTests|FullyQualifiedName~ResetOffsetDialogTests"`
Expected: FAIL to compile — `ConsumerGroupDetail`/`ResetOffsetDialog` pages don't exist yet.

- [ ] **Step 3: Write `ResetOffsetDialog.razor`**

```razor
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.ConsumerGroups
@using SbConsole.Sdk
@inject IConfirmationService Confirmation
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">@ConnectionName / @GroupId / @TopicName-@Partition</MudText>
        <MudSelect T="OffsetResetMode" Label="Reset to" @bind-Value="_mode" Style="min-width:200px">
            <MudSelectItem Value="OffsetResetMode.Earliest">Earliest</MudSelectItem>
            <MudSelectItem Value="OffsetResetMode.Latest">Latest</MudSelectItem>
            <MudSelectItem Value="OffsetResetMode.Offset">Specific offset</MudSelectItem>
            <MudSelectItem Value="OffsetResetMode.Timestamp">Specific timestamp</MudSelectItem>
        </MudSelect>
        @if (_mode == OffsetResetMode.Offset)
        {
            <MudNumericField T="long" Label="Offset" @bind-Value="_offset" />
        }
        else if (_mode == OffsetResetMode.Timestamp)
        {
            <MudDatePicker Label="Date" @bind-Date="_timestampDate" />
            <MudTimePicker Label="Time (UTC)" @bind-Time="_timestampTime" />
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="reset-offset-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="confirm-reset" Color="Color.Error" Variant="Variant.Filled" Disabled="_busy" OnClick="Save">Reset</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public bool IsProd { get; set; }
    [Parameter] public string GroupId { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public int Partition { get; set; }

    [Inject] private ResetConsumerGroupOffsetCommandHandler ResetHandler { get; set; } = default!;

    private OffsetResetMode _mode = OffsetResetMode.Latest;
    private long _offset;
    private DateTime? _timestampDate = DateTime.UtcNow.Date;
    private TimeSpan? _timestampTime = DateTime.UtcNow.TimeOfDay;
    private bool _busy;

    private DateTimeOffset? ResolvedTimestamp => _timestampDate is { } date
        ? new DateTimeOffset(date.Date + (_timestampTime ?? TimeSpan.Zero), TimeSpan.Zero)
        : null;

    private async Task Save()
    {
        var target = $"{TopicName}-{Partition}";
        var confirmed = await Confirmation.ConfirmAsync("Reset offset for", target, IsProd);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await ResetHandler.HandleAsync(new ResetConsumerGroupOffsetCommand(
                ConnectionId, ConnectionName, GroupId, TopicName, Partition, _mode,
                _mode == OffsetResetMode.Offset ? _offset : null,
                _mode == OffsetResetMode.Timestamp ? ResolvedTimestamp : null));

            if (result.IsSuccess)
            {
                MudDialog.Close(DialogResult.Ok(true));
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
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Write `ConsumerGroupDetail.razor`**

```razor
@page "/p/kafka/consumer-groups/{GroupId}"
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.ConsumerGroups
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject GetConsumerGroupDetailQueryHandler DetailHandler
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Consumer Group — @GroupId</PageTitle>
<h1>Consumer Group: @GroupId</h1>

@if (_loading)
{
    <MudProgressCircular Class="detail-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
}
else if (_detail is null)
{
    <MudAlert Severity="Severity.Error">@_loadError</MudAlert>
}
else
{
    <div class="d-flex align-center gap-2 my-4">
        <MudChip T="string" Class="group-state" Color="@StateColor(_detail.State)" Size="Size.Small">@_detail.State</MudChip>
        @if (!IsResettable)
        {
            <MudText Typo="Typo.caption" Class="reset-blocked-reason mud-text-secondary">Offsets can only be reset while the group is Empty.</MudText>
        }
    </div>

    <MudText Typo="Typo.h6" Class="mt-4 mb-2">Members</MudText>
    <MudTable Items="_detail.Members" Class="members-table">
        <HeaderContent>
            <MudTh>Client ID</MudTh>
            <MudTh>Host</MudTh>
            <MudTh>Assigned Partitions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.ClientId</MudTd>
            <MudTd>@(context.Host ?? "—")</MudTd>
            <MudTd>@string.Join(", ", context.AssignedPartitions.Select(p => $"{p.TopicName}-{p.Partition}"))</MudTd>
        </RowTemplate>
    </MudTable>

    <MudText Typo="Typo.h6" Class="mt-6 mb-2">Partitions</MudText>
    <MudTable Items="_detail.Partitions" Class="partitions-table">
        <HeaderContent>
            <MudTh>Topic</MudTh>
            <MudTh>Partition</MudTh>
            <MudTh>Committed Offset</MudTh>
            <MudTh>High Watermark</MudTh>
            <MudTh>Lag</MudTh>
            <MudTh>Consumer</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.TopicName</MudTd>
            <MudTd>@context.Partition</MudTd>
            <MudTd>@context.CommittedOffset</MudTd>
            <MudTd>@context.HighWatermark</MudTd>
            <MudTd>@context.Lag</MudTd>
            <MudTd>@(context.ConsumerClientId is { } clientId ? $"{clientId}@{context.ConsumerHost}" : "idle")</MudTd>
            <MudTd>
                <MudButton Class="reset-offset" Disabled="@(!IsResettable)" OnClick="@(() => OpenResetAsync(context.TopicName, context.Partition))">Reset</MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    [Parameter] public string GroupId { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }

    private ConsumerGroupDetail? _detail;
    private ConnectionInfo? _connection;
    private bool _loading;
    private string _loadError = "";

    // The client-side half of the "block Reset unless Empty" guard (design spec §5) -- the broker
    // itself is the authoritative backstop: ResetConsumerGroupOffsetAsync surfaces a
    // FriendlyKafkaError if the group's state changed between this page load and the click.
    private bool IsResettable => _detail?.State == "Empty";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            // IsProd (used to gate the Reset confirmation dialog) is always re-resolved here from
            // IConnectionProvider, never trusted off the ConnectionId query parameter alone -- same
            // "read the authoritative value fresh" rule every other destructive action follows
            // (design spec §5; docs/design.md §6.1).
            var connections = await Connections.ListAsync("kafka");
            _connection = connections.SingleOrDefault(c => c.Id == ConnectionId);
            if (_connection is null)
            {
                _detail = null;
                _loadError = "Connection not found.";
                return;
            }

            var result = await DetailHandler.HandleAsync(ConnectionId, GroupId);
            if (result.IsSuccess)
            {
                _detail = result.Value;
            }
            else
            {
                _detail = null;
                _loadError = result.Error!;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static Color StateColor(string state) => state switch
    {
        "Dead" => Color.Error,
        "PreparingRebalance" or "CompletingRebalance" => Color.Info,
        _ => Color.Default,
    };

    private async Task OpenResetAsync(string topicName, int partition)
    {
        var parameters = new DialogParameters<ResetOffsetDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.ConnectionName, _connection!.Name },
            { x => x.IsProd, _connection!.IsProd },
            { x => x.GroupId, GroupId },
            { x => x.TopicName, topicName },
            { x => x.Partition, partition },
        };
        var dialog = await DialogService.ShowAsync<ResetOffsetDialog>("Reset offset", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadAsync();
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~ConsumerGroupDetailPageTests|FullyQualifiedName~ResetOffsetDialogTests"`
Expected: PASS (5 tests total).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Pages/ConsumerGroupDetail.razor src/SbConsole.Plugins.Kafka/Pages/ResetOffsetDialog.razor tests/SbConsole.Plugins.Kafka.Tests/Pages/ConsumerGroupDetailPageTests.cs tests/SbConsole.Plugins.Kafka.Tests/Pages/ResetOffsetDialogTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add the Consumer Group detail page and offset reset dialog

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Dashboard problems + nav badge

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations.ListConsumerGroupsAsync` (Task 2), `KafkaPlugin.LagProblemThreshold` (Task 5), `PluginDashboardProblem`/`IPluginStore` (`SbConsole.Sdk`).
- Produces: `KafkaPlugin.GetDashboardProblemsAsync`/`GetNavBadgeAsync` overrides — this is the last task in the plan; nothing later depends on it.

`KafkaPlugin`'s dashboard/badge methods need their own `IKafkaOperations` to call against, but `KafkaPlugin` is always constructed via a parameterless `new()` (no DI at that layer, same reasoning `TestConnectionAsync`/`GetDashboardMetricsAsync` already follow) — so these tests exercise the real `KafkaPlugin` end-to-end against an unreachable broker address, the same pattern `KafkaPluginTests.TestConnectionAsync_delegates_to_the_real_Kafka_client_and_never_throws` already uses, rather than against a substitute (there's no seam to substitute at this layer).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
// Add these two test class members:

    [Fact]
    public async Task GetDashboardProblemsAsync_never_throws_against_an_unreachable_broker()
    {
        var plugin = new KafkaPlugin();

        var problems = await plugin.GetDashboardProblemsAsync(
            Guid.NewGuid(), "bootstrap.servers=127.0.0.1:1", Substitute.For<IPluginStore>());

        problems.Should().NotBeNull();
    }

    [Fact]
    public async Task GetNavBadgeAsync_returns_null_for_an_unrelated_nav_href()
    {
        var plugin = new KafkaPlugin();

        var badge = await plugin.GetNavBadgeAsync("/p/kafka/topics", "bootstrap.servers=127.0.0.1:1");

        badge.Should().BeNull();
    }
```

(Add `using NSubstitute;` to the file's existing `using` block if not already present — `IPluginStore` needs a substitute here.)

Also add a pure unit test against the threshold logic itself, without touching the network, by testing the private-selection logic indirectly is not possible here (it's embedded in the plugin method) — instead, cover the threshold boundary the same way Task 6's page tests already do, at the one layer that is unit-testable: a dedicated static helper extracted for exactly this reason.

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
// Add this test class member:

    [Theory]
    [InlineData(9_999, false)]
    [InlineData(10_000, false)]
    [InlineData(10_001, true)]
    public void HasHighLag_reflects_the_LagProblemThreshold_boundary(long totalLag, bool expected)
    {
        var group = new ConsumerGroupSummary("g", "Stable", 1, totalLag);

        KafkaPlugin.HasHighLag(group).Should().Be(expected);
    }
```

(Add `using SbConsole.Plugins.Kafka.Client;` to the file's `using` block for `ConsumerGroupSummary`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter KafkaPluginTests`
Expected: FAIL to compile — `KafkaPlugin.HasHighLag` does not exist, and `GetDashboardProblemsAsync`/`GetNavBadgeAsync` still resolve to the `IPlugin` default no-op implementations (the two behavioral tests would otherwise pass trivially against the no-op, but won't compile until `HasHighLag` exists since all three are added together below).

- [ ] **Step 3: Add the overrides**

```csharp
// src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
// Add these members to the KafkaPlugin class, replacing the trailing
// "// GetDashboardProblemsAsync/GetNavBadgeAsync overrides are added in Task 8." comment:

    // Extracted as a pure static function so the threshold boundary is unit-testable without a
    // real broker -- same reasoning as ConfluentKafkaOperations.ComputeLag.
    internal static bool HasHighLag(ConsumerGroupSummary group) => group.TotalLag > LagProblemThreshold;

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
        return groups
            .Where(HasHighLag)
            .Select(g => new PluginDashboardProblem(
                "Warning",
                $"Consumer group '{g.GroupId}' has high lag",
                $"{g.TotalLag:N0} messages behind",
                $"/p/kafka/consumer-groups/{g.GroupId}"))
            .ToList();
    }

    private const string ConsumerGroupsNavHref = "/p/kafka/consumer-groups";

    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref == ConsumerGroupsNavHref
            ? GetConsumerGroupBadgeAsync(connectionString, ct)
            : Task.FromResult<int?>(null);

    private static async Task<int?> GetConsumerGroupBadgeAsync(string connectionString, CancellationToken ct)
    {
        var groups = await new ConfluentKafkaOperations().ListConsumerGroupsAsync(connectionString, ct);
        var problemCount = groups.Count(HasHighLag);
        // null (never 0) when nothing is flagged -- NavMenu.razor renders a visible "0" badge for a
        // literal 0 and only omits the badge for null, confirmed against ServiceBusPlugin's
        // identical GetDeadLetterBadgeAsync pattern (design spec §6).
        return problemCount > 0 ? problemCount : null;
    }
```

Also add `using SbConsole.Plugins.Kafka.Client;`'s `ConsumerGroupSummary` reference is already covered by the existing `using SbConsole.Plugins.Kafka.Client;` at the top of `KafkaPlugin.cs` — no new `using` needed in the plugin file itself.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter KafkaPluginTests`
Expected: PASS (all cases, including the 3 new `HasHighLag` boundary cases and the 2 broker-facing smoke tests).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS — this is the final task in the plan.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/KafkaPlugin.cs tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): surface high consumer-group lag as a dashboard problem and nav badge

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```
