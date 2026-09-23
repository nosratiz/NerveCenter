# AWS plugin — SNS Topics — Design

Status: draft, 2026-09-23.

## 1. Context

`docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md` shipped the AWS
plugin's foundation and SQS Queues, explicitly deferring SNS as "a separate
plan, once this one ships" (§1, §9). This spec is that plan: topics,
subscriptions (including the `PendingConfirmation` lifecycle), and publish —
the AWS analogue of Service Bus's own Topics & Subscriptions plan.

Source design material: `~/Desktop/UI mockups for NerveCenter/SbConsole
AWS.dc.html`, screens `1f` (Topics list), `1g` (Topic → Subscriptions), and
the publish half of `1h` (the redrive/purge halves of `1h` already shipped
with SQS).

`ConnectionKind`/`DisplayName` (`"aws"`/`"AWS SQS/SNS"`) were chosen from the
start to cover this scope — no connection-kind migration, no new secret
format, no new fields in `AwsConfigParser`. Topics use the exact same
connection as Queues.

**Client libraries:** `AWSSDK.SimpleNotificationService` (SNS) and
`AWSSDK.CloudWatch` (for the per-topic delivery-failure metric), added to
the existing `SbConsole.Plugins.Aws` project alongside `AWSSDK.SQS`/
`AWSSDK.SecurityToken`.

## 2. `ISnsOperations`

Mirrors `ISqsOperations` exactly — every method takes the connection secret
as a parameter, one real implementation (`SnsOperations`) built against
`AmazonSimpleNotificationServiceClient` and `AmazonCloudWatchClient`, both
constructed the same way `SqsOperations.BuildSqsClient`/`BuildStsClient` are
(via `AwsConfigParser.Parse` → `AwsCredentialsFactory.BuildConfig`/
`BuildCredentials`, with `ServiceURL`/`UseHttp` forwarded from the same
parsed config so a custom LocalStack endpoint redirects SNS and CloudWatch
too, exactly as it already does for STS):

```csharp
public interface ISnsOperations
{
    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default);
    Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default);
    Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default);
    /// <summary>Mutating, not Destructive -- reversible by subscribing again.</summary>
    Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default);

    Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default);

    /// <summary>Sum of NumberOfNotificationsFailed over the trailing 24h, via CloudWatch GetMetricStatistics.</summary>
    Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default);
}

public sealed record TopicSummary(
    string Name, string TopicArn, bool IsFifo, int SubscriptionCount, int PendingConfirmationCount, bool IsKmsEncrypted);

public sealed record CreateTopicRequest(string Name, bool IsFifo, string? KmsKeyId, bool? ContentBasedDeduplication);

public sealed record SubscriptionSummary(
    string SubscriptionArn, string Protocol, string Endpoint, bool IsPending,
    bool? RawMessageDelivery, string? FilterPolicyJson);

public sealed record SubscribeRequest(string TopicArn, string Protocol, string Endpoint, bool RawMessageDelivery);

public sealed record SnsPublishRequest(
    string? Subject, string Message, IReadOnlyDictionary<string, string>? MessageAttributes,
    string? MessageGroupId, string? MessageDeduplicationId);
```

`TopicSummary.SubscriptionCount`/`PendingConfirmationCount` come from one
`ListSubscriptionsByTopic` call per topic (mirroring `ListQueuesAsync`'s
per-queue `GetQueueAttributes` fan-out) — a topic whose call fails leaves
that row's counts unavailable (a dashed/degraded cell), not the whole page
blank, same partial-failure rule `docs/design.md`'s SQS section already
established. `SubscriptionSummary.IsPending` is true when a subscription's
`SubscriptionArn` is the literal string `"PendingConfirmation"` (SNS's own
convention for an unconfirmed subscription) rather than a real ARN.

`GetDeliveryFailureCountAsync` calls CloudWatch `GetMetricStatistics` with
`Namespace: "AWS/SNS"`, `MetricName: "NumberOfNotificationsFailed"`,
`Dimensions: [{Name: "TopicName", Value: topicName}]`,
`StartTime: now-24h`, `EndTime: now`, `Period: 86400`,
`Statistics: ["Sum"]` — a zero-datapoint response (no failures, or the
metric has no data yet) is `0`, not an error. **This requires the
connection's IAM principal to additionally have `cloudwatch:GetMetricData`/
`GetMetricStatistics`** — a new permission requirement beyond what Queues
needed, documented in `docker/README.md`'s AWS section and in the least-
privilege policy example. A denied call degrades that one topic's Failed-24h
cell (unavailable, not zero — zero and "couldn't check" must render
differently) rather than failing the whole list, same partial-failure rule.

## 3. Topics list (`Pages/Topics.razor`, `/p/aws/topics`)

New `AwsPlugin.NavItems` entry: `new("Topics", "/p/aws/topics")`, second in
the array after Queues. Same connection picker, prod chip, and
`AwsConfigParser.SafeEcho` connection-echo caption pattern as `Queues.razor`.

Table: `Topic | Type | Subs | Pending | Failed 24h | Flags | Actions`.
`Type`: `Standard`/`FIFO` tag, same styling as Queues'. `Flags`: `KMS`
(encrypted), and a "0 subscriptions" warning flag (the mockup's
`invoice-issued-topic` case — "accepts publishes and discards them").
Actions: **Publish**, **Subs** (navigates to `TopicDetail.razor`),
**Delete** (`ActionRisk.Destructive`, typed-confirm-on-prod, same
`IConfirmationService`/`IConnectionProvider.IsProd` gate every other
destructive action in this plugin uses). "+ Create topic" opens
`CreateTopicDialog.razor` — a Standard/FIFO toggle switching the visible
field set, exactly mirroring `CreateQueueDrawer.razor`'s own toggle. FIFO
topic names get the same server-side `.fifo` suffix-append rule
`CreateQueueAsync` already uses for FIFO queues.

Loading shape: names first (from `ListTopics`, which returns ARNs only —
names are the last ARN segment, same as the mockup's caption states),
subscription/pending/failed-24h counts filled in as their per-topic calls
complete, matching the "names first, counts after" skeleton pattern
`Queues.razor` already uses.

## 4. Topic detail (`Pages/TopicDetail.razor`, `/p/aws/topics/{topicArn}`)

Two tabs: **Subscriptions** (default) and **Attributes**. No Access Policy
tab — same cut `QueueDetail`'s design made (§9).

**Subscriptions tab**: ARN header with copy button. Table: `Protocol |
Endpoint | Filter policy | Raw delivery | State | Actions`. Filter policy
column: `none` when absent, or a `MudTooltip`/expandable cell showing the
formatted JSON from `SubscriptionSummary.FilterPolicyJson` — **read-only**;
setting or changing a filter policy is done in the AWS console, not here
(scope cut, see §8). State: `Confirmed` (plain row), `Pending` (warn-colored
row, with the mockup's elapsed-time-since-subscribe caption), `Failing`
(bad-colored row, from `GetSubscriptionAttributes`'
`ConfirmationWasAuthenticated`/delivery-status-adjacent attributes — exact
signal confirmed against the installed SDK at implementation time). Actions:
**Remove** (`UnsubscribeAsync`, `ActionRisk.Mutating`, plain confirm — not
Destructive, since re-subscribing fully reverses it, unlike deleting a
topic) on every row; **Resend** instead of Remove on a `Pending` row, which
calls `SubscribeAsync` again with the same topic/protocol/endpoint (SNS
treats a repeat `Subscribe` call for a still-pending subscription as
idempotent and issues a fresh confirmation delivery — this is the *only*
mechanism AWS exposes for "resend," there is no dedicated resend API).
"+ Subscribe" opens `SubscribeDialog.razor`: Protocol (select: sqs, https,
email, lambda), Endpoint, Raw message delivery toggle. No filter-policy
field (§8).

**Attributes tab**: ARN, encryption (KMS key id or "unencrypted"), FIFO
yes/no, created-at if available. Mirrors `QueueDetail`'s Attributes-only
scope exactly.

## 5. Publish (`Dialogs/PublishDialog.razor`)

Topic (pre-filled from the row/detail page it was opened from), Subject
(caption: "email only"), Message attributes (key/value rows), Message body.
For a FIFO topic: a required Message Group ID field and either a
Deduplication ID field or a "content-based deduplication is on" note,
mirroring `SendMessageDialog.razor`'s identical FIFO field set for SQS.

**Fan-out preview**: on dialog open, calls `GetSubscriptionAttributes` once
per subscription on the topic (typically a handful — this is the one place
in this plan where per-subscription API cost is accepted, because publish
is exactly the moment a filtered topic's fan-out becomes a guess without
it) to read each one's filter policy, then evaluates the currently-entered
message attributes against each policy client-side (pure function, no
network) and renders a "Matches N of M subscriptions" panel listing every
subscription with a ✓/✕/— per the mockup: matched, filtered out (with which
attribute mismatched), pending (excluded, can't receive yet), or failing
(excluded, endpoint is down). Re-evaluated on every attribute edit — this
is why it must be a pure client-side function over already-fetched filter
policies, not a server round-trip per keystroke. `ActionRisk.Mutating`,
audited as `aws.topic.publish`.

## 6. Test-connection integration

`SqsOperations.TestConnectionAsync`'s `ConnectionTestResult.Checks` list
(added in the concurrent connections-page-redesign work,
`docs/superpowers/specs/2026-09-22-connections-page-redesign-design.md` §3)
gains a second entry from a cheap `ListTopics` probe (`MaxResults`-equivalent
low-cost call, `Passed`/`Detail: count` or `Failed`/`Detail: friendly
denial reason`) — this was explicitly flagged as a followup in that spec's
AWS integration note and is completed here now that `ISnsOperations` exists.
This is a one-line addition to `SqsOperations.TestConnectionAsync` (which
would need to either construct an `SnsOperations` internally for this one
probe, or the check is added to a shared `AwsPlugin.TestConnectionAsync`
composition point instead — exact placement decided at implementation time,
whichever avoids a circular/awkward dependency between the two operations
classes).

## 7. Error handling

`FriendlyAwsError` (`SbConsole.Plugins.Aws.Client`) gains mappings for the
SNS/CloudWatch exception types this plugin's new calls can hit —
`NotFoundException` (topic/subscription not found), `InvalidParameterException`
(malformed filter policy read back, malformed endpoint), `AuthorizationErrorException`
(SNS's access-denied shape, distinct from SQS's `AccessDeniedException`), and
CloudWatch's own access-denied shape for the metrics call — every mapping
confirmed against the installed `AWSSDK.SimpleNotificationService`/
`AWSSDK.CloudWatch` package versions at implementation time, not assumed,
same bar every prior exception mapping in this plugin holds itself to.

## 8. Out of scope (this plan)

- **Filter-policy authoring/editing** — Subscribe/Edit have no filter-policy
  field; policies are set via the AWS console and shown read-only here. A
  JSON editor for SNS's filter-policy syntax (prefix/anything-but/numeric
  matching, `$or`/nested conditions) is real surface area deferred to a
  follow-up once this slice's shape proves out.
- **Access Policy tab** on `TopicDetail.razor` — Subscriptions and
  Attributes only, matching `QueueDetail`'s own cut.
- **Delivery logs** (the mockup's "Delivery logs" button on a failing
  subscription) — needs CloudWatch Logs, a separate service/permission from
  the metrics this plan already adds; deferred.
- **Queue detail's "Subscribed to N SNS topics" panel** — `QueueDetail.razor`
  itself was never built (SQS plan §9), so there is still no page to add
  this cross-link to.
- **Nav-badge/dashboard integration** — `GetNavBadgeAsync` et al. stay at
  SDK defaults, same deferral reasoning as Queues.
- **Per-message manual redrive** from any SNS-side view — redrive stays
  queue-level only (already shipped with SQS).
