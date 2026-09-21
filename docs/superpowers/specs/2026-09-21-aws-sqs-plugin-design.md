# AWS plugin — foundation + SQS Queues — Design

Status: draft, 2026-09-21.

## 1. Context

SbConsole has two plugins so far, each built as a sequence of plans starting
with one vertical slice that proves the architecture out: Azure Service Bus
(`docs/design.md` §6, starting with Queues) and Apache Kafka (`docs/design.md`
§6.5, starting with Topics). This spec starts the same sequence for a third
messaging system — AWS, covering SQS and SNS — scoped to the same role Queues
and Topics played for their plugins: prove the shape out end-to-end on SQS,
the simpler of the two services, before SNS follows as its own plan.

Source design material: `~/Desktop/UI mockups for NerveCenter/SbConsole
AWS.dc.html`, eight screens (`1a`–`1h`) covering the connection form, SQS
queues/messages, and SNS topics/subscriptions. This plan covers `1a`–`1e`
(connection form, queue list, create queue, queue detail, receive messages)
plus the SQS-only parts of `1h` (redrive, purge). SNS (`1f`–`1g`, and the
publish parts of `1h`) is out of scope — see §9.

Full target scope for the AWS plugin, decided but **not** all built here:

1. **This plan** — plugin foundation (connection form, all three auth modes,
   test connection), SQS queue management, message receive/send, redrive,
   purge.
2. SNS topics, subscriptions (including the `PendingConfirmation` lifecycle
   and filter policies), and publish — the AWS analogue of Service Bus's
   Topics & Subscriptions plan. Separate plan, once this one ships.

**Client libraries:** `AWSSDK.SQS` and `AWSSDK.SecurityToken` (for credential
validation via STS), added only to the new plugin project — same isolation
rule `Azure.Messaging.ServiceBus` and `Confluent.Kafka` follow today.

## 2. Connection model

Every plugin gets exactly one opaque secret string end-to-end (`docs/
design.md` §6.5's connection-model note, repeated here because AWS is the
plugin with the widest field set of the three). AWS's mockup shows three
structurally different auth modes plus fields that apply regardless of mode
(region, custom endpoint, path-style addressing) — more field variance than
Kafka's SASL/mTLS options, but the same flat `key=value;` string convention
still fits, so no new secret shape is invented:

```
mode=access-keys;region=eu-west-1;accessKeyId=AKIA...;secretAccessKey=...;sessionToken=...
mode=assume-role;region=eu-west-1;roleArn=arn:aws:iam::123456789012:role/SbConsoleReader;externalId=...;sessionName=...
mode=default-chain;region=eu-west-1
```

Optional, any mode: `endpoint=http://localstack:4566` (custom `ServiceURL`
for LocalStack/ElasticMQ) and `pathStyle=true` (forces path-style endpoint
construction — "required by most emulators," per the mockup).

`Client/AwsConfigParser.cs` — same shape as `KafkaConfigParser`: splits on
`;`, then each segment on the first `=`, into a `Dictionary<string, string>`.
A malformed segment (no `=`) is skipped rather than throwing, same reasoning
as Kafka — the resulting config just won't have that key, and every operation
already surfaces a missing required field as a friendly connection-shaped
error (§6) rather than a parser exception.

`AwsConfigParser.SafeEcho` — an allowlist echo of `region`, `mode`, and
`endpoint` only (never `accessKeyId`/`secretAccessKey`/`sessionToken`/
`roleArn`/`externalId`), joined the same `" · "`-separated way Kafka's
`SafeEcho` is, rendered under the queue picker header on `Queues.razor` so a
user can confirm which region/mode they're connected to without the page
needing any new SDK concept. **This is also how region is shown at all** —
`SbConsole.Sdk.ConnectionInfo` gains no new field, and the host's
shared Connections table gains no new column. The mockup's Connections-list
"Region" column (populated for AWS rows, blank for others) is not built;
region is visible only inside the AWS plugin's own pages, the same way
Kafka's cluster fields are visible only inside Kafka's own pages.

Building `AWSCredentials` from the parsed config:

- `access-keys` → `BasicAWSCredentials`, or `SessionAWSCredentials` when
  `sessionToken` is present.
- `assume-role` → the SDK's default credential chain resolves the *base*
  credentials (matching the mockup's "base credentials for the
  `sts:AssumeRole` call come from the default chain" note), then
  `AssumeRoleAWSCredentials` (or the SDK's current equivalent — the exact
  type name is confirmed against the installed `AWSSDK.SecurityToken`
  version at implementation time, not assumed) wraps them with `roleArn`/
  `externalId`/`sessionName`.
- `default-chain` → the SDK's own default resolution (environment variables,
  shared config/credentials file, container/instance metadata, in that
  order) — no `AWSCredentials` object is constructed by this plugin at all;
  the AWS SDK client picks it up automatically when none is supplied.

`endpoint`/`pathStyle` map onto `AmazonSQSConfig.ServiceURL` and whatever
`AWSSDK.SQS` exposes for path-style endpoint construction against emulators
— confirmed against the installed SDK version at implementation time (SQS's
endpoint shape differs from S3's virtual-hosted-vs-path-style distinction the
"path-style addressing" label is borrowed from; the concrete mapping is an
implementation detail, not a spec assumption).

**No custom connection-form UI is built.** `AddEditConnectionDialog.razor`
(`src/SbConsole.Web/Components/Connections/`) is a single generic host
dialog shared by every plugin — one plain "Connection string" textbox, no
per-plugin field hook, no Test-connection UI inside the dialog itself (Test
is the Connections table's existing "Test" row action, `docs/design.md`
§5.1). Kafka's own connection secret — a comparably multi-field librdkafka
config string — never got a custom form either; Kafka users type the flat
string by hand. AWS follows the identical precedent: users type
`mode=access-keys;region=eu-west-1;accessKeyId=...;secretAccessKey=...`
(etc.) directly into the existing textbox. The mockup's rich drawer (region
dropdown, auth-mode segmented control, Advanced disclosure, inline
account-ID/ARN/latency Test-result panel) is **not** built this slice — it
would require a new per-plugin custom-connection-form host/SDK capability
that doesn't exist for any plugin today and is out of scope here (see §9).
The field format is documented in `docker/README.md` (§8) and echoed back
via `AwsConfigParser.SafeEcho` on `Queues.razor` (above) so a user can
confirm what they typed without re-opening the dialog.

## 3. Plugin shell

New project `src/SbConsole.Plugins.AWS`, referencing `SbConsole.Sdk` only.

```csharp
public sealed class AwsPlugin : IPlugin
{
    public string Id => "aws";
    public string DisplayName => "AWS SQS/SNS";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/aws/queues"),
    ];
    public string ConnectionKind => "aws";
    public string ConnectionKindDisplayName => "AWS SQS/SNS";

    // Queues: Create/Delete/Purge queue, Receive, Delete message, Release message, Send, Redrive (8).
    // Pages: Queues, QueueDetail, Receive.
    public PluginContribution Contribution => new(PageCount: 3, ActionCount: 8);

    public void ConfigureServices(IServiceCollection services) { /* §6 */ }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new SqsOperations().TestConnectionAsync(secret, ct);

    // GetNavBadgeAsync, GetDashboardMetricsAsync, GetDashboardProblemsAsync,
    // GetOldestDeadLetterAsync, GetResourceMetricsAsync: SDK defaults (§9,
    // Out of scope) -- nothing to report until this plugin has a DLQ-overview
    // or dashboard-tile plan of its own.
}
```

`ConnectionKind`/`DisplayName` are `"aws"`/`"AWS SQS/SNS"` from the start
(covering the full eventual scope), so adding SNS later needs no rename or
connection-kind migration — only new `NavItems` entries and new
`IPlugin.Get*` overrides.

`ISqsOperations` — the substitutable seam (mirrors `IKafkaOperations`/
`IServiceBusOperations` exactly), every method taking the connection secret
as a parameter:

```csharp
public interface ISqsOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default);
    Task CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default);
    /// <summary>Destructive. Asynchronous and eventually consistent on the AWS side -- see §5.</summary>
    Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default);

    Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default);
    /// <summary>Requires the receipt handle from ReceiveMessagesAsync, not the message ID.</summary>
    Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default);
    /// <summary>"Release now" -- sets visibility timeout to 0 so the message is immediately visible again.</summary>
    Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default);
    Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>Native AWS move task (StartMessageMoveTask) -- destination defaults to the queue the source dead-lettered from.</summary>
    Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default);
    Task<RedriveTaskStatus> GetRedriveTaskStatusAsync(string secret, string taskHandle, CancellationToken ct = default);
}

public sealed record QueueSummary(
    string Name, string QueueUrl, string QueueArn, bool IsFifo,
    long ApproxVisible, long ApproxInFlight, long ApproxDelayed,
    bool HasDeadLetterTarget, int DeadLetterSourceCount, bool IsKmsEncrypted, DateTimeOffset CreatedAt);

public sealed record CreateQueueRequest(
    string Name, bool IsFifo,
    int VisibilityTimeoutSeconds, int RetentionPeriodSeconds, int DelaySeconds, int MaxMessageSizeBytes, int ReceiveWaitTimeSeconds,
    string? DeadLetterTargetArn, int? MaxReceiveCount,
    string? KmsKeyId,
    bool? ContentBasedDeduplication, bool? HighThroughputFifo);

public sealed record SendMessageRequest(
    string Body, IReadOnlyDictionary<string, string>? MessageAttributes, int? DelaySeconds,
    string? MessageGroupId, string? MessageDeduplicationId);

public sealed record ReceivedMessage(
    string MessageId, string ReceiptHandle, string Body, int ApproxReceiveCount,
    DateTimeOffset SentTimestamp, string SenderId, string Md5OfBody,
    IReadOnlyDictionary<string, string> MessageAttributes);

public sealed record RedriveTaskStatus(string Status, long ApproximateNumberOfMessagesMoved, string? FailureReason);
```

One real implementation, `Client/SqsOperations.cs`, built against
`AWSSDK.SQS`'s `AmazonSQSClient` and `AWSSDK.SecurityToken`'s
`AmazonSecurityTokenServiceClient` (for `TestConnectionAsync`'s
`GetCallerIdentity` call). `AwsPlugin` constructs it directly with `new()`,
same reason as `ServiceBusPlugin`/`KafkaPlugin`: plugins are constructed via
a parameterless `new()`, so there's no DI container to pull a registered
instance from at that layer.

## 4. Test connection

`SqsOperations.TestConnectionAsync`: builds `AWSCredentials`/config from the
parsed secret (§2), calls STS `GetCallerIdentity` (validates credentials
identically for all three auth modes — the mockup's "Test connection reports
which link of the [default] chain answered" comes from whichever credential
source `GetCallerIdentity` actually resolved against), then a small
`ListQueues` probe (`MaxResults` capped low) to confirm SQS reachability
specifically, not just STS.

`ConnectionTestResult` stays the SDK's existing `(bool Success, string?
ErrorMessage)` — no new SDK type is introduced for this. Mapping:

- `GetCallerIdentity` throws → `Success = false`, `ErrorMessage` from
  `FriendlyAwsError` (§7) — covers the mockup's `InvalidClientTokenId` case.
- `GetCallerIdentity` succeeds, `ListQueues` throws `AccessDeniedException`
  → `Success = true`, `ErrorMessage` carries a **non-fatal** note ("valid
  credentials; `sqs:ListQueues` denied — queue actions may fail"). This is
  the mockup's "valid but under-permissioned" case, but per the read-only-
  connections scope cut (§9) it is surfaced as text only: nothing is
  persisted, no button anywhere is hidden as a result, and every action
  still fails gracefully via the existing `FriendlyError`-wrapped
  `PluginResult.Fail` path if IAM genuinely denies it at call time — the
  same posture every other plugin already has.
- Both succeed → `Success = true`, `ErrorMessage = null`.

`FriendlyAwsError` (§7) maps the specific exception types this call site can
hit (`InvalidClientTokenIdException`, `AccessDeniedException`,
`UnrecognizedClientException`, a generic timeout/unreachable case) to
distinct readable messages, confirmed against the installed
`AWSSDK.SecurityToken`/`AWSSDK.SQS` package versions at implementation time
— same "confirmed by decompilation, not assumed" bar `docs/design.md` §6
held Service Bus to.

## 5. Queue management

- **`ListQueuesAsync`** → `AmazonSQSClient.ListQueuesAsync` (server-side
  `QueueNamePrefix` filter — SQS has no substring/regex filter, so the
  "Starts with" label on `Queues.razor`'s filter box is deliberate, not a
  UI simplification), then per queue `GetQueueAttributesAsync` with
  `AttributeNames = [QueueAttributeName.All]`.

  **Correction to the mockup's stated cost**: the mockup's caption says a
  refresh costs `1 + 3n` API calls. That doesn't match the real API —
  `GetQueueAttributes` with `AttributeNames=[All]` returns every attribute
  (visible/in-flight/delayed counts, encryption, redrive policy, FIFO
  settings, timestamps) in **one** call per queue, so a refresh is actually
  `1 + n` calls (one `ListQueues` plus one `GetQueueAttributes` per queue).
  `Queues.razor`'s caption states the real number, not the mockup's
  illustrative one.
- **Loading shape**: `ListQueuesAsync` (the query handler, not the SDK
  method above — same name, plugin-handler layer) returns names
  immediately; the page renders rows with skeleton placeholders in the
  count columns while attribute calls complete, matching the mockup's
  "names first, counts after" loading state. A `GetQueueAttributes` failure
  for one queue (e.g. mid-load throttling) leaves that row dashed rather
  than failing the whole page — same "partial success, not total failure"
  shape Kafka's dead-letter walk and Service Bus's dead-letter overview
  already use for a per-item failure.
- **`CreateQueueAsync`** → `CreateQueueAsync` with attributes built from
  `CreateQueueRequest`. FIFO: the `.fifo` suffix is appended server-side in
  the handler (not user-removable, matching the mockup), `FifoQueue=true`,
  `ContentBasedDeduplication`/`FifoThroughputLimit` set from the optional
  fields. `QueueNameExists`-with-different-attributes surfaces as a
  friendly conflict message (via `FriendlyAwsError`) distinguishing it from
  a benign no-op create (identical name + attributes succeeds silently, per
  SQS's own idempotency rule — no special handling needed, the SDK call
  just returns normally). `ActionRisk.Mutating`.
- **`DeleteQueueAsync`** → `DeleteQueueAsync`. `ActionRisk.Destructive`,
  typed-confirm-on-prod via the existing `IConnectionProvider`-sourced
  `IsProd` flag and `IConfirmationService` — identical gate to every other
  plugin's destructive delete, reading `IsProd` server-side, never from a
  client-suppliable parameter (`docs/design.md` §6.1).
- **`PurgeQueueAsync`** → `PurgeQueueAsync`. `ActionRisk.Destructive`,
  typed-confirm-on-prod (queue name), same gate as delete. The dialog states
  the approximate delete count, "there is no undo," and that AWS purges
  asynchronously (up to 60 seconds) — messages sent during that window may
  or may not survive, and the count won't hit zero immediately. This is
  UI copy only; `PurgeQueueAsync` itself is fire-and-forget from
  SbConsole's side (AWS does the actual purge asynchronously), matching how
  the mockup describes it.

`Queues.razor` at `/p/aws/queues` — connection/region echo (§2) under the
header, a "Starts with" prefix filter, a Refresh picker (Off / 15s / 30s /
60s, default **Off**) with the real per-refresh call-count caption (above),
a queue-count + "counts read HH:MM" line, "+ Create queue" opening
`CreateQueueDrawer.razor` (Standard/FIFO toggle switching the visible field
set, exactly as the mockup's two variants show). Table: `Queue | Type |
~Visible | ~In flight | ~Delayed | Flags | Created | Actions`. Flags:
`KMS`/`SSE` (encryption), `DLQ ×N` (this queue is a redrive target for N
source queues), `FIFO`. Actions: **Receive**, **Send**, plus **Redrive**
replacing **Send** on a queue that is itself DLQ-flagged (has messages
redriven into it), and **Delete**/**Purge** from an overflow menu (kept off
the main row per the mockup's action-density, mirroring Kafka's
Peek/Send/Delete row pattern).

`QueueDetail.razor` (drawer or dedicated route, TBD at plan time — mirrors
whichever of Service Bus/Kafka's existing detail-view pattern is closer once
implementation starts): ARN with copy, the four approximate metric tiles
(~Visible, ~In flight, ~Delayed, Oldest message) with the mockup's "these can
even return different numbers on repeated calls — expected, not a bug" note,
a redrive-out panel (this queue's own redrive policy: DLQ target + max
receive count), the full attribute list, and tags. Access-policy and
tag-editing tabs are out of scope (§9) — Attributes only.

## 6. Message receive and send

**Receive** (`Receive.razor`, queue-scoped route): Max (batch size, 1–10 per
SQS's own limit) and Hide for (visibility timeout override) controls, a
Receive button, and the mockup's explicit "SQS has no peek" caption. While
any received message is held, an always-visible banner states "these N
messages are hidden from your consumers for T seconds," with a live
countdown and a **Release now** action (`ChangeMessageVisibilityAsync` with
`0`). Received-message list shows a receive-count badge that escalates
outline → warn → bad as the count climbs (thresholds decided at
implementation time, not hard-specified here); multi-select drives bulk
Delete/Release. Selecting a message shows its body, system attributes
(`ApproximateReceiveCount`, `SentTimestamp`, `SenderId`, `MD5OfBody`),
custom message attributes, and the receipt handle with the mockup's "valid
only while hidden — delete needs this handle, not the message ID" note.

Deleting an expired receipt handle surfaces `ReceiptHandleIsInvalidException`
via `FriendlyAwsError` as "this message's hold already expired — it's back
in the queue," matching the mockup's "delete failed for 2 of 3" error state;
a bulk delete that partially fails this way reports per-message success/
failure rather than treating the whole batch as failed.

`ReceiveMessagesAsync` is modeled as a **command**, not a query, despite
being a read from SbConsole's own database's point of view — unlike Service
Bus/Kafka's non-destructive peek, receiving from SQS has a real, audit-worthy
side effect on the broker (messages become invisible to other consumers for
the visibility timeout). It audits `aws.queue.receive`, `ActionRisk.
Mutating`. `DeleteMessageCommandHandler`, `ReleaseMessageCommandHandler`
(`ChangeMessageVisibilityAsync`), and `SendMessageCommandHandler` are each
`ActionRisk.Mutating`, no confirmation — matching Service Bus/Kafka's Send.

**Send** — `SendMessageDialog.razor` (queue pre-filled from the row it was
opened from): Body (multi-line), Message attributes (key/value rows), an
optional Delay override, and — only when the target queue is FIFO — a
required Message Group ID field plus either a Deduplication ID field or a
note that content-based deduplication is on for this queue (mirroring
`CreateQueueRequest`'s own FIFO fields). Not detailed in the mockup itself
(the mockup shows the Send *action* on rows but not this dialog); designed
from the create-queue FIFO fields and Kafka's `ProduceMessageDialog` for
shape.

**Redrive** — `RedriveDialog.razor`: source (the DLQ) and destination
(defaults to the queue configured as this DLQ's redrive source — reversed
from the create-queue redrive-policy relationship) pickers, a rate limit
input (`maxMessagesPerSecond`), and the mockup's warning that already-failed
messages may dead-letter again with their receive count reset ("looking new
on next arrival"). `StartRedriveTaskAsync` → AWS's native
`StartMessageMoveTask`; `ActionRisk.Mutating` with a plain confirm (the
mockup's redrive dialog has no typed-confirm field, unlike Purge's). Slice 1
starts the task and reports success/failure only — see §9 for the deferred
live-progress polling.

## 7. Error handling

`Client/FriendlyAwsError.cs` — same role as `FriendlyKafkaError`/Service
Bus's `FriendlyError` (`SbConsole.Sdk`): caps and collapses exception text
before it reaches the UI or an audit row, logging the full exception
server-side. Maps `InvalidClientTokenIdException`/`UnrecognizedClientException`
→ "credentials rejected," `AccessDeniedException` → "access denied — check
IAM permissions for `{Action}`," `QueueDoesNotExistException` → "queue not
found," `QueueNameExistsException` → "a queue with this name already exists
with different settings," `ReceiptHandleIsInvalidException` → "this
message's hold already expired," `RequestThrottledException`/
`TooManyRequestsException` → "AWS is throttling this connection — try
again shortly," and a generic fallback for anything else — every mapping
confirmed against the installed `AWSSDK.SQS`/`AWSSDK.SecurityToken`
package versions at implementation time, not assumed. `TestConnectionAsync`
and every handler route through this helper; no raw `ex.Message` ever
reaches a snackbar or the audit log.

`AmazonSQSConfig`/`AmazonSecurityTokenServiceConfig` get tightened
`Timeout`/`MaxErrorRetry` so an unreachable region or a bad custom endpoint
fails fast with a clear message instead of hanging the page — same rule
Service Bus's `RetryOptions`/`TryTimeout` tightening and Kafka's
`AdminClient`/`Consumer` timeout tightening both followed.

## 8. Testing

Same layered approach as Service Bus and Kafka (`docs/design.md` §8):

- Unit tests only, against a substitute `ISqsOperations` (NSubstitute) — no
  Testcontainers, no real AWS/LocalStack traffic, for this plan.
  `docs/design.md` §8 gains a line noting the same deferral applies to AWS,
  for the same "prove the shape out first" reasoning Service Bus and Kafka
  used.
- `SqsOperations` itself gets light coverage by necessity (most of its
  methods can't be meaningfully unit-tested without a real or emulated
  broker) — its value is being a substitutable seam, not its own test
  count, exactly as `docs/design.md` §6 says about
  `AzureServiceBusOperations`/`ConfluentKafkaOperations`.
- `AwsConfigParser` gets full unit coverage — pure function, no I/O: empty
  string, each auth mode's required/optional fields, trailing `;`,
  malformed segment without `=`, duplicate keys (last one wins, matching
  `Dictionary` insertion semantics), `SafeEcho`'s allowlist (confirms
  credential fields never appear in its output).
- Handler tests: connection-not-found, happy path, and exception-to-
  `FriendlyAwsError` mapping for each handler in §5–§6 (list/create/delete/
  purge queue, receive/delete/release/send message, start redrive).
- Component tests (bUnit): `Queues.razor` (list rendering, prefix filter,
  Create/Delete/Purge actions, confirmation gating on a prod-tagged
  connection), `CreateQueueDrawer.razor` (Standard/FIFO field-set
  switching), `Receive.razor` (hold-countdown banner, receive-count badge
  escalation, partial-failure delete), `SendMessageDialog.razor`
  (FIFO-conditional fields), `RedriveDialog.razor`, `PurgeDialog.razor`
  (typed-confirm gating).
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every
  commit.

**Local dev stack**: `docker-compose.yml` gains a `--profile aws` LocalStack
service (`localstack/localstack` image, `SERVICES=sqs,sns` so the SNS plan
needs no compose change later), exposed on `4566`, plus an `aws-init`
one-shot container seeding a couple of sample queues so `Queues.razor` has
something to show on first run — mirroring Kafka's `kafka-init` pattern.
`docker/README.md` documents the endpoint (`http://localstack:4566` from
other containers, `http://localhost:4566` from the host), the dummy
access-key/secret pair LocalStack accepts unconditionally, the region to use
(`us-east-1`, matching the mockup's `aws-localstack` example connection),
and that `pathStyle=true` should be set for this connection. This is for
manual dev use only — the automated test suite stays unit-only per above.

Integration testing (Testcontainers running LocalStack, exercising actual
SQS calls over the wire) is deferred past this plan, mirroring Service Bus's
and Kafka's own deferral — picked up once the AWS plugin's shape has proven
out across this plan and the SNS plan alike, same trigger condition
`docs/design.md` §8 used for the other two.

## 9. Out of scope (this plan)

- **SNS** — topics, subscriptions (including `PendingConfirmation`, filter
  policies, delivery-failure tracking), and publish. Separate plan (§1,
  item 2).
- **Dashboard/nav-badge/dead-letter-overview integration** —
  `GetNavBadgeAsync`, `GetDashboardMetricsAsync`, `GetDashboardProblemsAsync`,
  `GetOldestDeadLetterAsync`, `GetResourceMetricsAsync` all stay at their SDK
  default (no-op) implementations; there is no cross-connection DLQ signal
  worth reporting yet, and adding it prematurely would duplicate work once
  the SNS plan's own dead-letter-relevant screens are designed.
- **Persisted read-only/degraded-connection capability set** — the mockup's
  "Save read-only" flow (denied actions hidden from the UI based on a saved
  Test-connection result) is not built. Test connection reports richer
  diagnostic text (§4) but nothing is persisted, and every action stays
  visible everywhere, failing gracefully via `FriendlyAwsError` if IAM
  denies it at call time — the same posture every other plugin already has.
- **Live redrive progress** — `GetRedriveTaskStatusAsync` exists on
  `ISqsOperations` for a future progress view, but `RedriveDialog.razor`
  does not poll it in this plan; the dialog starts the move task and reports
  only success/failure. A live rate/duration/progress-bar view (the
  mockup's "Est. duration ~21s") is a follow-up.
- **Per-message manual "redrive to source queue"** from inside `Receive.razor`
  — only the queue-level DLQ→source `RedriveDialog` (native move-task API)
  ships in this plan.
- **Queue detail's "Subscribed to N SNS topics" panel** — needs SNS-side
  data (subscription filter/raw-delivery mode read from the queue's access
  policy); deferred with SNS.
- **Access policy and Tags tabs** on `QueueDetail.razor` — Attributes only.
- **Host-level Region column** on the shared Connections table — region is
  visible only inside the AWS plugin's own pages (§2); `ConnectionInfo`
  gains no new field.
- **A custom AWS connection-form UI** (region dropdown, auth-mode segmented
  control, Advanced disclosure, inline rich Test-result panel) — no
  per-plugin custom-connection-form host/SDK capability exists today (§2);
  building one is a materially larger, separately-scoped change, not an
  AWS-plugin-internal one. Users type the flat secret string by hand into
  the existing generic dialog, matching Kafka's precedent exactly.
- Integration tests against LocalStack (§8).
