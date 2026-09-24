# AWS Plugin Completion Pass — Spec + Implementation Plan (2026-09-24)

> **For agentic workers:** execute task-by-task, TDD (failing test first), `dotnet build -warnaserror`
> and `dotnet test` green before every commit. One commit per task (fix-up commits allowed).

## Goal

Close out the AWS plugin's deferred scope recorded in `docs/design.md` §6.7 ("Deferred past this plan",
"Also not built") and §6.7.1 ("Out of this plan"), except the items that are either explicitly rejected
at the host level or need a new AWS service with a separate permission story (listed under Non-goals).

## Global Constraints

- Follow every convention in `CLAUDE.md`: handlers are plain `XxxQueryHandler`/`XxxCommandHandler`
  classes returning `PluginResult`/`PluginResult<T>`; commands audit via `IAuditScope` (success *and*
  failure), queries never audit; all AWS calls go through `ISqsOperations`/`ISnsOperations`; every
  exception is logged then reduced via `FriendlyAwsError.From`; `IsProd` comes from a server-side
  `IConnectionProvider` lookup; no hex colors; MudBlazor only.
- Match the existing handler/page/test style — read one existing sibling (e.g.
  `Queues/PurgeQueueCommandHandler.cs` + its tests, `Pages/TopicDetail.razor` + `TopicDetailPageTests.cs`)
  before writing a new one.
- New AWS SDK members must be verified against the installed `AWSSDK.*` package versions (build, or
  inspect the DLL) — never assumed.
- Keep `AwsPlugin.Contribution` and its comment accurate as pages/actions are added.
- Pure logic (parsing, cache expiry, problem derivation) is extracted into `internal static` helpers so
  it is unit-testable without AWS — same approach as `SqsOperations.ExtractDeadLetterTargetArn` and
  `KafkaPlugin.GetCachedConsumerGroupsAsync`.

## Non-goals (deliberately not built)

- Persisted "denied actions"/read-only enforcement and IAM Policy Simulator — rejected host-wide in
  the connections-page redesign spec §8.
- SNS delivery logs — needs CloudWatch Logs, a separate service + permission set.
- `GetOldestDeadLetterAsync` — SQS exposes no enqueue timestamp without *receiving* a message, and a
  receive has a real side effect (invisibility). The wallboard tile stays at the SDK default (`null`).

---

## Task 1: Dashboard, nav badge, and resource metrics

**Behavior**
- `AwsPlugin.GetNavBadgeAsync("/p/aws/queues", …)` → total `ApproxVisible` across DLQ queues
  (`DeadLetterSourceCount > 0`), or `null` when zero; `null` for any other href.
- `GetDashboardMetricsAsync` → `Queues` (count), `Dead-lettered` (sum of DLQ visible counts).
- `GetResourceMetricsAsync` → one `PluginResourceMetric` per queue with readable attributes:
  `ActiveCount = ApproxVisible`, `DeadLetterCount` = `ApproxVisible` for a DLQ queue else 0.
- `GetDashboardProblemsAsync` → one `Warning` per DLQ queue with visible messages:
  Title = queue name, Detail = `"{n} dead-lettered"`, LinkHref = `/p/aws/queues/{Uri.EscapeDataString(queueUrl)}`.
- All four share a 60s static per-connection-string cache of `ListQueuesAsync(secret, null)`, built
  exactly like `KafkaPlugin.GetCachedConsumerGroupsAsync` (injectable `now` + `fetch` for tests).
  Failures in these hooks must not throw into the host — return null/empty (check how Kafka handles it
  and match).
- Replace the "trivial override" comment in `AwsPlugin`.

**Tests**: cache reuse/expiry; badge sum/null; problems derivation; metric derivation — via internal
static helpers over `IReadOnlyList<QueueSummary>`.

## Task 2: Queue detail page (`/p/aws/queues/{QueueUrl}`)

**Ops**
- `ISqsOperations.GetQueueDetailAsync(secret, queueUrl)` → `QueueDetail` record: name, url, arn, isFifo,
  the three approx counts, created/last-modified, all raw attributes (`IReadOnlyDictionary<string,string>`),
  tags (`ListQueueTags`), parsed own redrive policy (`DeadLetterTargetArn`, `MaxReceiveCount`, may be null).
- `ISnsOperations.ListSubscriptionsForEndpointAsync(secret, endpoint)` → subscriptions whose
  `Endpoint == endpoint` (paginated `ListSubscriptions`, filtered client-side) as `SubscriptionSummary`
  (reuse the existing record; add fields only if needed).
- Handlers: `GetQueueDetailQueryHandler`, `ListQueueSnsSubscriptionsQueryHandler` (both queries).

**Page** `Pages/QueueDetail.razor`, route `/p/aws/queues/{QueueUrl}` (URL-encoded, same approach as
`TopicDetail.razor`'s ARN route), `?connection={id}` query param like the other pages:
- Header: name, FIFO/KMS chips, ARN with copy button, buttons Receive (link), Send, Purge, Delete (reuse
  the existing dialogs/handlers exactly as `Queues.razor` does).
- Four metric tiles: Visible, In flight, Delayed, and (if DLQ) Sources.
- "Redrive out" panel: this queue's own RedrivePolicy (target queue name + maxReceiveCount), or "No DLQ configured".
- "Subscribed to N SNS topics" panel listing topic name + protocol + link to the topic detail page. An SNS
  permission failure renders an inline warning, not a page failure.
- Attributes table (all raw attributes, sorted) and Tags table.
- In `Queues.razor`, the queue name becomes a link to this page.

**Tests**: `ParseRedrivePolicy` helper, handler success/failure, bUnit page (renders tiles, redrive panel,
SNS panel incl. failure state, tags).

## Task 3: Redrive progress + cancel

- `ISqsOperations.ListMessageMoveTasksAsync(secret, sourceArn)` → `IReadOnlyList<MessageMoveTaskSummary>`
  (TaskHandle, Status, SourceArn, DestinationArn, MessagesMoved, MessagesToMove, FailureReason, StartedAt).
- `ISqsOperations.CancelMessageMoveTaskAsync(secret, taskHandle)`.
- `ListRedriveTasksQueryHandler`; `CancelRedriveCommandHandler` (`ActionRisk.Mutating`, audited as
  `aws.queue.redrive.cancel`).
- `QueueDetail.razor` (DLQ queues only): "Redrive tasks" panel with a progress bar per task and a Cancel
  button for `RUNNING` tasks; the panel polls every 5s while any task is `RUNNING` and stops otherwise
  (timer disposed with the component). Also a Redrive button reusing `RedriveDialog`.

**Tests**: handlers; bUnit — progress rendering, Cancel invokes handler, polling helper tested by calling
its refresh step directly (NavMenu precedent, design.md §8).

## Task 4: Queues list polish

- Render a `Created` column from `QueueSummary.CreatedAt` (dash for unavailable rows).
- Auto-refresh picker Off/15s/30s/60s (default Off, persisted per plugin via `IPluginStore` key
  `queues.autoRefreshSeconds`) and a "counts read HH:mm:ss" caption after each load. Timer disposed
  with the component; a refresh never overlaps an in-flight load.
- Move Delete/Purge into a per-row overflow `MudMenu`.

**Tests**: bUnit — Created column, caption, picker persists, overflow menu items invoke existing flows.
Update existing `QueuesPageTests` selectors that referenced the inline buttons.

## Task 5: Topic attributes + access policy

- `ISnsOperations.GetTopicAttributesAsync(secret, topicArn)` → `IReadOnlyDictionary<string,string>`.
- `GetTopicAttributesQueryHandler`.
- `TopicDetail.razor` Attributes tab shows: ARN, display name, owner, FIFO, content-based dedup, KMS key,
  confirmed/pending/deleted subscription counts, and every other raw attribute; a new **Access policy**
  tab shows the `Policy` attribute pretty-printed read-only (and `DeliveryPolicy`/`EffectiveDeliveryPolicy`
  if present). Read-only — no policy editing.

**Tests**: handler; bUnit — attribute rendering, pretty-printed policy, failure state.

## Task 6: Filter-policy authoring

- `ISnsOperations.SetSubscriptionFilterPolicyAsync(secret, subscriptionArn, string? policyJson, string scope)`
  via `SetSubscriptionAttributes` (`FilterPolicy`, `FilterPolicyScope` = `MessageAttributes`|`MessageBody`);
  null/empty policy clears it.
- `SetFilterPolicyCommandHandler` — `Mutating`, audited `aws.subscription.filterpolicy.set`. Rejects
  non-object JSON before calling AWS (validation helper, unit-tested).
- `SubscribeRequest` gains optional `FilterPolicy`/`FilterPolicyScope`, sent as subscribe attributes.
- UI: `SubscribeDialog` gains an optional filter-policy editor; `TopicDetail.razor` subscription rows get
  an "Edit filter" action opening `FilterPolicyDialog.razor` (pre-filled from the existing filter-policy
  query; JSON validation inline; scope selector).

**Tests**: validation helper, handler success/failure/audit, dialog bUnit.

## Task 7: Per-message "move to source" from Receive

- On a DLQ queue (`DeadLetterSourceCount > 0`), each received message row gets **Move to source**.
  Source queues = queues whose RedrivePolicy targets this queue's ARN (from `ListQueuesAsync`); one source
  → used directly; several → the user picks in a small dialog.
- `MoveMessageToSourceCommandHandler` (`Mutating`, audited `aws.message.move`): `SendMessageAsync` to the
  source with the same body + message attributes (FIFO: reuse `MessageGroupId`, dedup id = original
  message id), then `DeleteMessageAsync` on the DLQ with the receipt handle. If send fails, nothing is
  deleted; if delete fails after a successful send, the result reports the duplicate risk explicitly.
- `ReceivedMessage` exposes whatever it needs (message attributes, group id) — check the existing record.

**Tests**: handler ordering/failure semantics (send fail → no delete; delete fail → specific message);
source-resolution helper; bUnit button visibility on DLQ vs non-DLQ.

## Task 8: Docs

- `docs/design.md`: new dated `### 6.7.2 Completion pass (2026-09-24)` under §6.7 recording what shipped,
  new IAM permissions, and the Non-goals above with their reasons; strike the now-stale "Deferred"/"Out of
  this plan" sentences by adding a pointer to §6.7.2 (append-only style — don't delete history).
- `docker/README.md`: add new least-privilege permissions (`sqs:ListQueueTags`,
  `sqs:ListMessageMoveTasks`, `sqs:CancelMessageMoveTask`, `sns:ListSubscriptions`,
  `sns:SetSubscriptionAttributes`, and any others actually used).

## Final gate

`dotnet build -warnaserror` 0/0, `dotnet test` all green, `git status --short` clean apart from the
user's pre-existing `src/SbConsole.Web/Properties/launchSettings.json` edit (never commit it).
