# Service Bus subscription filter rules — Design

Status: implemented, 2026-09-16.

## 1. Context

The Service Bus plugin's Topics & Subscriptions plan
(`docs/superpowers/plans/2026-09-13-servicebus-topics-subscriptions-plugin.md`)
explicitly deferred filter rules: every subscription created by the plugin
gets the topic's default catch-all rule, and the mockup's "Rules" column
(filter-count chips) was intentionally left out as view-only scaffolding. Its
own "After this plan" note called this out as "the natural next slice,
reusing this plan's `Topics.razor` expandable rows for a per-subscription
rules sub-panel." This spec is that slice.

Scope: SQL filter rules only (no correlation filters — deferred further).
Add and Delete only — Azure Service Bus rules have no update API, so
"editing" a rule is delete-then-create under a different UI, which this plan
does not build; a user who wants to change a rule deletes it and adds a new
one.

## 2. SDK models and `IServiceBusOperations`

New files under `Client/`:

```csharp
public sealed record RuleSummary(string Name, string SqlExpression);

public sealed record CreateRuleRequest(string Name, string SqlExpression);
```

`IServiceBusOperations` gains three methods, same shape and placement
(after the subscription dead-letter methods) as every other addition in this
interface:

```csharp
Task<IReadOnlyList<RuleSummary>> ListRulesAsync(
    string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);

Task CreateRuleAsync(
    string connectionString, string topicName, string subscriptionName, CreateRuleRequest request, CancellationToken ct = default);

/// <summary>Destructive.</summary>
Task DeleteRuleAsync(
    string connectionString, string topicName, string subscriptionName, string ruleName, CancellationToken ct = default);
```

`AzureServiceBusOperations` implements these against
`ServiceBusAdministrationClient`:
- `ListRulesAsync` → `GetRulesAsync(topicName, subscriptionName)`, projecting
  each `RuleProperties` to `RuleSummary` (`Filter` is expected to be a
  `SqlRuleFilter`; its `SqlExpression` is read directly — a rule created by
  some other tool as a correlation filter would need a fallback string, e.g.
  `Filter.ToString()`, so the page never throws on an unexpected filter type).
- `CreateRuleAsync` → `CreateRuleAsync(topicName, subscriptionName, new
  CreateRuleOptions(request.Name, new SqlRuleFilter(request.SqlExpression)))`.
- `DeleteRuleAsync` → `DeleteRuleAsync(topicName, subscriptionName,
  ruleName)`.

## 3. Handlers

New `Rules/` folder (mirrors `Subscriptions/`):

- `ListSubscriptionRulesQueryHandler` — connection lookup, calls
  `ListRulesAsync`, returns `PluginResult<IReadOnlyList<RuleSummary>>`.
- `CreateRuleCommandHandler` — audits `rule.create`,
  `ActionRisk.Mutating`, target `{connection}/{topic}/{subscription}/{ruleName}`.
- `DeleteRuleCommandHandler` — audits `rule.delete`, `ActionRisk.Destructive`,
  same target shape.

Both command handlers follow the existing try/catch/audit/`FriendlyError`
pattern used by every handler in this plugin (e.g.
`DeleteSubscriptionCommandHandler`) — no new error-handling shape.

Register all three in `ServiceBusPlugin.ConfigureServices`, alongside the
existing subscription handler registrations.

## 4. `Topics.razor`

**New column:** "Rules", inserted between "Scheduled" and "Actions". Topic
rows render `—` (rules belong to subscriptions, not topics — same convention
subscription rows already use for the topic-only "Scheduled" column).
Subscription rows render a chip with the live rule count.

**Data flow:** `Topics.razor` already holds an expand/collapse toggle per
topic and, on expanding a topic, fetches that topic's subscriptions. This
plan adds: when a topic expands, alongside the existing subscription fetch,
fire `ListSubscriptionRulesQueryHandler` once per subscription in that topic,
in parallel (`Task.WhenAll`), and cache each result in a component field
`Dictionary<string, IReadOnlyList<RuleSummary>>` keyed by
`"{topicName}/{subscriptionName}"`. This is the only place rules are
fetched — the chip and the sub-panel (below) both read from this same cache,
so expanding a subscription's rules panel never issues a second network
call. Collapsing and re-expanding a topic re-fetches (same pattern the page
already uses for subscriptions themselves — no new caching lifetime rules to
invent).

**Sub-panel:** subscription rows get their own expand toggle (same `▸`/`▾`
glyph convention as topic rows). Expanding one inserts a full-width row
(colspan across all six columns) immediately below it, listing that
subscription's rules — each as `Name — SqlExpression` with a `Delete`
button — and a `+ Add rule` button.

**`CreateRuleDialog.razor`:** two text fields (Name, SQL expression),
styled like `CreateSubscriptionDialog.razor`. Both required client-side
(non-empty); Azure's own validation (name format, expression syntax) surfaces
through the handler's existing `FriendlyError` mapping on submit.

**Delete confirmation:** goes through the existing injected
`IConfirmationService`, same call shape as `DeleteSubscriptionAsync` —
plain vs. typed-for-prod is decided by the host from the connection's
`IsProd` flag, no special-casing needed here. Deleting a subscription's last
remaining rule is allowed with no extra warning copy: a subscription with
zero rules simply receives nothing going forward, which is valid Azure
behavior and consistent with how this plugin doesn't second-guess other
destructive actions (e.g. purging a dead-letter queue) with extra prompts
beyond the standard confirm.

## 5. Error handling

No new exception types or mapping logic — `FriendlyError.From(ex)` already
handles arbitrary `ServiceBusException`/`RequestFailedException` instances
thrown by the administration client, which covers rule-name conflicts,
invalid SQL expression syntax, and the not-found case on delete.

## 6. Testing

Same layered approach as the rest of the plugin (`docs/design.md` §8):
handler tests against a substitute `IServiceBusOperations` (list/create/
delete happy path, connection-not-found, and exception-to-`FriendlyError`
mapping), plus `Topics.razor` component tests covering: the Rules chip count
after expanding a topic, the sub-panel's rule list and Add/Delete actions,
and the `[Authorize]`/route regression suite (no new route is added, so no
new entry needed there — this feature is entirely inside the existing
`/p/azure-servicebus/topics` page).

Gate: `dotnet build -warnaserror` and `dotnet test` green before commit.

## 7. Out of scope

- Correlation filters (structured match on CorrelationId, Label, session,
  custom properties) — SQL filters only for this slice.
- Rule editing — delete and re-add is the only path to changing a rule.
- Dashboard problems/alerting on rule count (e.g. flagging a subscription
  with zero rules) — no new `DashboardProblems.cs` entries in this plan.
- Any change to `CreateSubscriptionCommandHandler`'s existing behavior of
  leaving a new subscription on the topic's default catch-all rule.
