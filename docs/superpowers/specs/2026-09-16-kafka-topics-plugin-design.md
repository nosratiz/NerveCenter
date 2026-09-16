# Kafka plugin — Topics + message browse/produce — Design

Status: draft, 2026-09-16.

## 1. Context

SbConsole's plugin architecture (`docs/design.md` §1–§5) has one real plugin
so far: Azure Service Bus (`docs/design.md` §6), built as a sequence of
plans — Queues (the full vertical slice that proved the architecture),
Topics & Subscriptions, Dead-letter overview, filter rules. This spec starts
the same sequence for a second messaging system, Apache Kafka, and is scoped
to the same role Queues played: prove the plugin shape out end-to-end for
Kafka on the simplest possible entity (a topic), covering topic management
plus message browse/produce.

Full target scope for the Kafka plugin, decided but **not** all built here:

1. **This plan** — topic management, message browse (peek) and produce.
2. Consumer group management (list groups, per-topic/partition lag, offset
   reset) — the Kafka analogue of Topics & Subscriptions. Separate plan.
3. Dead-letter handling via the common DLQ-topic convention (Kafka has no
   native DLQ) — the Kafka analogue of the Dead-letter overview. Separate
   plan.

Each later item gets its own spec/plan cycle once this one ships, exactly as
Service Bus's did.

**Client library:** `Confluent.Kafka` (the standard .NET client, wrapping
librdkafka), added only to the new plugin project — same isolation rule
`Azure.Messaging.ServiceBus` follows today (`docs/design.md` §6, Global
Constraints of the Queues plan).

## 2. Connection model

SbConsole gives every plugin exactly one opaque secret string end-to-end:
one textbox in `AddEditConnectionDialog.razor` → `CreateConnectionCommand`'s
`string Secret` → `ISecretProtector`-encrypted blob → `IConnectionProvider.
GetSecretAsync` → `string secret` on `IPlugin.TestConnectionAsync` and every
per-connection SDK method. There is no structured/multi-field secret type
and no plugin hook to add custom form fields — Service Bus is the only
precedent, and it just treats the whole string as its native Azure
connection string.

Kafka needs several fields (bootstrap servers, security protocol, SASL
mechanism, username/password, and/or TLS cert paths for mTLS), so the
connection secret is a **librdkafka config string**: semicolon-delimited
`key=value` pairs using librdkafka's own property names, e.g.

```
bootstrap.servers=broker1:9092,broker2:9092;security.protocol=SASL_SSL;sasl.mechanism=PLAIN;sasl.username=alice;sasl.password=s3cr3t
```

Plaintext/no-auth local dev omits every key but `bootstrap.servers` and sets
`security.protocol=PLAINTEXT` (or omits it — PLAINTEXT is librdkafka's
default). mTLS sets `security.protocol=SSL` plus `ssl.certificate.location`
/ `ssl.key.location` / `ssl.ca.location` (filesystem paths on the host
running SbConsole). No new schema is invented — every key is a real
librdkafka/`Confluent.Kafka.ClientConfig` property, so the string is
forward-compatible with any config option a user's cluster needs without
this plugin having to special-case it.

`Client/KafkaConfigParser.cs` — a static helper, `ParseAsync`-free (pure,
synchronous): splits on `;`, then each segment on the first `=`, into a
`Dictionary<string, string>`. Empty segments (e.g. a trailing `;`) are
skipped. This dictionary is handed directly to `AdminClientConfig`/
`ProducerConfig`/`ConsumerConfig`, all three of which are `ClientConfig`
subclasses backed by exactly this kind of property dictionary in
`Confluent.Kafka` — no field-by-field mapping code needed. A malformed
segment (no `=`) is ignored rather than throwing; the resulting config
simply won't have `bootstrap.servers`, and every operation already surfaces
that as a friendly "broker unreachable"-shaped error (§5) rather than a
parser exception, so there's no separate validation path to build.

## 3. Plugin shell

New project `src/SbConsole.Plugins.Kafka`, referencing `SbConsole.Sdk` only.

```csharp
public sealed class KafkaPlugin : IPlugin
{
    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Send (4).
    // Pages: Topics, Peek.
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 4);

    public void ConfigureServices(IServiceCollection services) { /* §4 */ }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(
        string connectionString, CancellationToken ct = default)
    {
        var topics = await new ConfluentKafkaOperations().ListTopicsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Partitions", topics.Sum(t => t.PartitionCount)),
        ];
    }

    // GetNavBadgeAsync, GetDashboardProblemsAsync, GetOldestDeadLetterAsync,
    // GetResourceMetricsAsync: SDK defaults (§6, Out of scope) -- nothing to
    // report until consumer-group lag / DLQ-topic support exist.
}
```

`IKafkaOperations` — the substitutable seam (mirrors `IServiceBusOperations`
exactly), every method taking the config string as a parameter:

```csharp
public interface IKafkaOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string config, CancellationToken ct = default);

    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string config, CancellationToken ct = default);
    Task CreateTopicAsync(string config, CreateTopicRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default);

    Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, int maxMessages, CancellationToken ct = default);
    Task ProduceMessageAsync(
        string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default);
}

public sealed record TopicSummary(string Name, int PartitionCount, int ReplicationFactor, long ApproximateMessageCount);
public sealed record CreateTopicRequest(string Name, int PartitionCount, int ReplicationFactor);
public sealed record KafkaMessageSummary(
    int Partition, long Offset, DateTimeOffset Timestamp, string? Key, string Value, bool ValueIsBase64);

public enum PeekStart { Earliest, Latest, Offset }
```

One real implementation, `Client/ConfluentKafkaOperations.cs`, built against
`Confluent.Kafka`'s `IAdminClient` (via `AdminClientBuilder`),
`IProducer<string, string>`, and `IConsumer<byte[], byte[]>` (consumer reads
raw bytes so `PeekMessagesAsync` can decide UTF-8-vs-base64 itself rather
than throwing on non-UTF-8 payloads — see `KafkaMessageSummary.
ValueIsBase64`). `KafkaPlugin` constructs it directly with `new()`, same
reason as `ServiceBusPlugin` (§6 of `docs/design.md`): plugins are
constructed via a parameterless `new()`, so there's no DI container to pull
a registered instance from at that layer.

## 4. Topic management

- `ListTopicsAsync` → `AdminClient.GetMetadata(timeout)` for topic names and
  each topic's partition/replica layout, then — per topic, per partition —
  `Consumer.QueryWatermarkOffsets` to get `(low, high)`. `TopicSummary.
  ApproximateMessageCount` is `Σ(high − low)` across the topic's partitions.
  **This is not the same thing Service Bus's counts mean**: Kafka doesn't
  remove messages on consumption, so this number is "currently retained by
  the topic's retention policy," not "unprocessed backlog." The design spells
  this out explicitly and the UI column is labelled "Messages (approx)" —
  not "Active" — to avoid the Service Bus association.
  `ReplicationFactor` is read from partition 0's replica count; a topic with
  non-uniform per-partition replication (possible after a manual reassignment
  outside this tool) just shows that partition's count — not worth a special
  UI for an edge case this plugin doesn't create.
- Eager aggregation on page load — one `GetMetadata` call plus one watermark
  query per partition, for every topic, every time the page loads. Same
  accepted-cost choice `docs/design.md` §6.2 made for Topics & Subscriptions
  ("can't show real numbers on a collapsed row otherwise"); at this
  console's scale (small/medium clusters, admin tool, not a monitoring
  system) this is fine. A cluster with hundreds of topics × many partitions
  each would need a lazier approach, explicitly out of scope here.
- `CreateTopicAsync` → `AdminClient.CreateTopicsAsync([new TopicSpecification
  { Name, NumPartitions, ReplicationFactor }])`. `ActionRisk.Mutating` (same
  risk level as create queue — reversible via delete, not destructive).
- `DeleteTopicAsync` → `AdminClient.DeleteTopicsAsync([topicName])`.
  `ActionRisk.Destructive`, typed-confirm-on-prod via the existing
  `IConnectionProvider`-sourced `IsProd` flag and `IConfirmationService` —
  identical gate to Service Bus's delete queue / purge dead-letter, same
  "reads `IsProd` server-side, never from a client-suppliable parameter"
  rule (`docs/design.md` §6.1).

`Topics.razor` at `/p/kafka/topics` — one `MudTable`: `Name | Partitions |
Replication | Messages (approx) | Actions`. Actions per row: **Peek**, **Send**,
**Delete**. A "Create topic" button above the table opens `CreateTopicDialog`
(Name, Partition count — default 1, Replication factor — default 1), styled
like `CreateSubscriptionDialog.razor`.

## 5. Message browse (Peek) and produce (Send)

**Peek is partition-scoped, not merged across partitions.** Kafka only
guarantees ordering within a partition, never topic-wide, so a merged
timestamp-interleaved view would imply an ordering guarantee Kafka doesn't
have — every mainstream Kafka GUI (Offset Explorer, Conduktor, Kafka UI)
scopes browsing to one partition at a time for the same reason. `Peek.razor`
at `/p/kafka/topics/{topicName}/peek`: partition dropdown (populated from
`TopicSummary.PartitionCount`), a "start from" selector (**Earliest** /
**Latest** — last N messages / **Offset** — explicit number), a max-messages
field, and a Fetch button.

`PeekMessagesAsync`: builds a `ConsumerConfig` from the parsed connection
dictionary plus `GroupId = Guid.NewGuid().ToString()` (a fresh, never-reused
group every call — no consumer group is ever persisted or shared) and
`EnableAutoCommit = false` (peeking never advances any offset, exactly like
Service Bus's non-destructive peek). Resolves the starting offset for
`start` (Earliest → `Offset.Beginning`; Latest → high watermark minus
`maxMessages`, clamped to `Offset.Beginning`; Offset → the caller's literal
value), `Assign`s the consumer to that single `TopicPartitionOffset`, then
polls in a loop up to `maxMessages` or a **bounded wall-clock cap** (mirrors
`docs/design.md` §6's "never spins forever against an unreachable
namespace" rule for Service Bus — a topic/partition with fewer messages
than requested must return early with what it got, not hang until a
generic per-call timeout). The consumer is disposed after every call — nothing
about it outlives the request.

Each returned `KafkaMessageSummary`'s `Value` is decoded as UTF-8 when the
bytes are valid UTF-8; otherwise it's base64-encoded and `ValueIsBase64 =
true`, and `Peek.razor` renders it in a monospace block with a "binary
(base64)" badge instead of attempting to display raw bytes as text. `Key`
follows the same rule, or is `null` when the message has no key.

**Send** — `ProduceMessageDialog.razor` (topic pre-filled from the row it
was opened from): optional Key field (when set, Kafka's default partitioner
hashes it to choose a partition — the natural "same key → same partition"
behavior producers rely on), a required Value field (multi-line text), and
an optional explicit Partition override (bypasses the key-hash partitioner
when set). `ProduceMessageAsync` → `IProducer<string, string>.
ProduceAsync(new TopicPartition(topicName, partition ?? Partition.Any),
new Message<string, string> { Key = key, Value = value })`, awaited so send
failures (e.g. unknown topic, message-too-large) surface synchronously
through the existing `FriendlyError` path rather than only in a background
delivery-report callback. `ActionRisk.Mutating`, no confirmation — matches
Service Bus's Send.

## 6. Handlers

New `Topics/` and `Messages/` folders under the plugin project, following
Core's `XxxQueryHandler`/`XxxCommandHandler` naming convention (`docs/
design.md` §6):

- `Topics.ListTopicsQueryHandler`
- `Topics.CreateTopicCommandHandler` — audits `topic.create`, `Mutating`,
  target `{connectionName}/{topicName}`.
- `Topics.DeleteTopicCommandHandler` — audits `topic.delete`, `Destructive`,
  same target shape.
- `Messages.PeekMessagesQueryHandler`
- `Messages.SendMessageCommandHandler` — audits `message.send`, `Mutating`,
  target `{connectionName}/{topicName}`.

Every handler: resolve the connection's secret via `IConnectionProvider.
GetSecretAsync`, call the matching `IKafkaOperations` method, catch and map
exceptions through `FriendlyError` (§7), and — for commands — report via
`IAuditScope.RecordAsync`. Identical shape to every Service Bus handler; no
new pattern introduced. All five registered in `KafkaPlugin.
ConfigureServices`, plus `IKafkaOperations` as a singleton and
`IPluginStore` pre-bound to `Id`, matching `ServiceBusPlugin.
ConfigureServices` line for line in structure.

## 7. Error handling

`Client/FriendlyKafkaError.cs` — same role as Service Bus's `FriendlyError`
(`SbConsole.Sdk`): caps and collapses exception text before it reaches the
UI or an audit row, logging the full exception server-side. `Confluent.
Kafka` failures surface as `KafkaException`/`ProduceException`, both
carrying an `Error` with an `ErrorCode`; this plan maps the cases every
operation here can actually hit to distinct readable messages —
`Local_Transport`/`BrokerNotAvailable`/`AllBrokersDown` → "broker(s)
unreachable," `SaslAuthenticationError`/`TopicAuthorizationFailed` →
"authentication failed," `UnknownTopicOrPart` → "topic not found," and a
generic fallback for anything else (`ex.Error.Reason`, capped) — verified
against the installed `Confluent.Kafka` version's actual `ErrorCode` enum at
implementation time, same "confirmed by decompilation, not assumed" bar
`docs/design.md` §6 held Service Bus to. `TestConnectionAsync` and every
handler route through this helper; no operation ever lets a raw `ex.
Message` reach a snackbar or the audit log.

Bounded operations (§4's watermark aggregation, §5's peek poll loop) use
tightened timeouts on the `AdminClient`/`Consumer` configs so an
unreachable-broker connection fails fast with a clear message rather than
hanging the page — same rule Service Bus's `RetryOptions`/`TryTimeout`
tightening followed.

## 8. Testing

Same layered approach as the Service Bus plugin (`docs/design.md` §8):

- Unit tests only, against a substitute `IKafkaOperations` (NSubstitute) —
  no Testcontainers, no real broker traffic, for this plan. `docs/design.md`
  §8 will gain a line noting the same deferral applies to Kafka, for the
  same "prove the shape out first" reasoning Service Bus used.
- `ConfluentKafkaOperations` itself gets light coverage by necessity (most
  of its methods can't be meaningfully unit-tested without a real or
  emulated broker) — its value is being a substitutable seam, not its own
  test count, exactly as `docs/design.md` §6 says about
  `AzureServiceBusOperations`.
- `KafkaConfigParser` gets full unit coverage — pure function, no I/O,
  cheap to test exhaustively (empty string, single key, trailing `;`,
  malformed segment without `=`, duplicate keys — last one wins, matching
  `Dictionary` insertion semantics).
- Handler tests: connection-not-found, happy path, and exception-to-
  `FriendlyKafkaError` mapping for each of the five handlers (§6).
- Component tests (bUnit): `Topics.razor` (list rendering, Create/Delete
  actions, confirmation gating on a prod-tagged connection), `Peek.razor`
  (partition selection, the three start modes, base64-badge rendering for
  non-UTF-8 values), `ProduceMessageDialog.razor`.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every
  commit.

Integration testing (Testcontainers running a real Kafka broker, exercising
actual produce/consume/admin calls over the wire) is deferred past this
plan, mirroring Service Bus's own deferral — picked up once the Kafka
plugin's shape has proven out across this plan and the consumer-group plan
alike, same trigger condition `docs/design.md` §8 used for Service Bus.

## 9. Out of scope (this plan)

- Consumer group management (list, lag, offset reset) — separate plan (§1,
  item 2).
- Dead-letter / DLQ-topic convention support — separate plan (§1, item 3).
- `GetNavBadgeAsync`, `GetDashboardProblemsAsync`, `GetOldestDeadLetterAsync`,
  `GetResourceMetricsAsync` — all stay at their SDK default (no-op)
  implementations; there is no lag or DLQ signal to report yet.
- Cross-partition merged peek view (§5) — partition-scoped only.
- Topic configuration beyond partition count/replication factor (retention.
  ms, cleanup.policy, min.insync.replicas, etc.) — `CreateTopicRequest` only
  exposes the two fields every Kafka admin tool treats as mandatory at
  creation time; broker-default config applies to everything else. Adding
  arbitrary topic-config overrides is easy to bolt on later (`Create
  TopicRequest` gains a `Dictionary<string,string>? Configs` field) but isn't
  needed to prove the plugin shape out.
- Schema Registry integration (Avro/Protobuf deserialization) — messages are
  always treated as raw bytes/UTF-8 text (§5); a schema-aware view is a
  meaningfully separate feature, not a natural extension of this plan.
- Integration tests against a real/emulated Kafka broker (§8).
