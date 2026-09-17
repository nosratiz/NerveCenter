# Service Bus subscription rule editing — Design

Status: draft, 2026-09-17.

## 1. Context

Both `docs/superpowers/specs/2026-09-15-servicebus-filter-rules-design.md` §1 and
`docs/superpowers/specs/2026-09-16-servicebus-correlation-filters-design.md` §1 deliberately
scoped filter rules to Add/Delete only, since Azure Service Bus has no update-rule API — a
user who wants to change a rule has always had to delete it and add a new one by hand. This
spec adds an "Edit" action that wraps that same delete-then-create sequence behind a single
pre-filled dialog, so the user never has to re-type a rule's unchanged fields from scratch.

**Dependency:** builds on `feature/servicebus-correlation-filter-fields` (PR #9, not yet
merged, itself stacked on PR #8 → PR #7). Every type this spec touches was introduced or
last modified across those three branches.

Scope: one "Edit" action per rule (SQL or correlation), reusing the existing "+ Add rule"
dialog pre-filled with the rule's current values. The user can change anything while editing,
including switching between SQL and correlation mode, or renaming the rule — the dialog does
not distinguish "editing" from "creating" beyond its starting values and what happens on save.

## 2. The Azure constraint and its consequence

Rule names are unique per subscription, and Azure has no atomic rename or update. This means:

- **Renaming a rule** (new name ≠ original name) can be done safely: create the new rule
  first, then delete the old one. There is a brief window where both exist, never a window
  where neither does.
- **Keeping the same name** (the common case) cannot be done safely: Azure will reject
  creating a rule under a name that's still in use, so the old one must be deleted first, then
  the new one created. There is an unavoidable window where the rule doesn't exist at all, and
  if creation fails after the deletion succeeded, the rule is gone until the user manually
  re-adds it.

This isn't a design flaw to engineer around — it's the same limitation the Azure Portal itself
lives with, since there's no lower-level primitive that avoids it. The design's job is to pick
the safe order whenever possible and make the unsafe window's failure mode legible when it
isn't.

## 3. Command/handler layer

New `Rules/EditRuleCommandHandler.cs`:

```csharp
public sealed record EditRuleCommand(
    Guid ConnectionId, string ConnectionName, bool IsProd,
    string TopicName, string SubscriptionName, string OriginalName, CreateRuleRequest NewRule);
```

`EditRuleCommandHandler` (constructor-injected `IServiceBusOperations`, `IConnectionProvider`,
`IAuditScope`, `ILogger<EditRuleCommandHandler>` — same shape as every other handler in this
plugin) resolves the connection secret once, then:

- If `NewRule.Name == OriginalName`: delete the old rule, then create the new one. If delete
  fails, report that failure and stop (nothing changed). If delete succeeds but create fails,
  return a distinct error: *"Rule '{OriginalName}' was deleted but the update could not be
  created ({error}) — it no longer exists and must be re-added."*
- If `NewRule.Name != OriginalName`: create the new rule, then delete the old one. If create
  fails, report that failure and stop (nothing changed). If create succeeds but delete fails,
  return a distinct error: *"A new rule '{NewRule.Name}' was created, but the old rule
  '{OriginalName}' could not be removed ({error}) and must be deleted manually."*

No new `IServiceBusOperations` methods — this handler is pure orchestration over the existing
`CreateRuleAsync`/`DeleteRuleAsync`. No new audit action: each half is recorded through the
**existing** `rule.create` (`ActionRisk.Mutating`) / `rule.delete` (`ActionRisk.Destructive`)
actions, exactly as a manual delete-then-add would produce — two audit rows per edit, which
honestly reflects that two operations happened against Azure. `IsProd` on the command exists
for the same reason `DeleteRuleCommand` already carries a `bool IsProd`-equivalent
confirmation input: to gate the destructive half through `IConfirmationService` at the call
site (below), not inside the handler.

## 4. `CreateRuleDialog.razor`

Gains `[Parameter] public RuleSummary? ExistingRule { get; set; }`. `OnInitialized` checks it:
if set, pattern-matches the subtype and pre-fills every field —
`SqlRuleSummary` → `_mode = "sql"`, `_sqlExpression` set; `CorrelationRuleSummary` → `_mode =
"correlation"`, all 8 `CorrelationMatch` fields and the `_properties` list populated from its
`Properties` dictionary. The dialog's title ("Add rule" / "Edit rule") and submit button text
("Add" / "Save") switch based on whether `ExistingRule` is set. Nothing else about the form —
validation, the SQL/correlation toggle, the property-row list — changes; a user editing a rule
can freely flip it to the other mode or change its name, exactly as if building a new one from
those starting values.

Gains one more parameter: `[Parameter] public bool IsProd { get; set; }` — `IConfirmationService`
and the live `ConnectionInfo` lookup currently live in `Topics.razor`, not the dialog (confirmed
by reading `DeleteRuleAsync`, which does the lookup and confirms before ever calling its
handler), but the dialog already injects its own handler directly (`CreateRuleCommandHandler`)
rather than delegating that call back to the page, so it also injects `[Inject]
IConfirmationService Confirmation` itself and receives `IsProd` as a parameter the same way it
already receives `ConnectionId`/`ConnectionName`.

`Save()` branches on `ExistingRule`: `null` calls `CreateRuleCommandHandler` as today;
non-null first calls `Confirmation.ConfirmAsync("Edit", ExistingRule.Name, IsProd)` — same call
shape as `Topics.razor`'s existing `DeleteRuleAsync` — and only on confirmation calls
`EditRuleCommandHandler` with `OriginalName = ExistingRule.Name` and the newly built
`CreateRuleRequest`. Confirming happens at Save time, after the user has already decided what
to change, not when the dialog first opens — asking "are you sure?" before they've made any
edits would be premature.

## 5. `Topics.razor`

Each rule row in the panel gains an `Edit` button next to the existing `Delete` button. Its
handler (`OpenEditRule`, mirroring `OpenCreateRule`) looks up the connection the same way
`DeleteRuleAsync` already does (`_connections.Single(c => c.Id == _selectedConnectionId)`) and
opens `CreateRuleDialog` with `ExistingRule` set to that row's `RuleSummary` plus `IsProd` set
to `connection.IsProd`, using the same `DialogService.ShowAsync<CreateRuleDialog>(...)` /
`DialogParameters<CreateRuleDialog>` pattern `OpenCreateRule` already uses. On a non-canceled
result, it calls `RefreshRulesAsync` exactly as Add and Delete already do.

## 6. Error handling

`EditRuleCommandHandler`'s two failure-window messages (§3) are the only new error text in
this slice; both route through the same `PluginResult.Fail(string)` /
`Snackbar.Add(result.Error!, Severity.Error)` path every other handler failure already uses —
no new error-handling shape, just new message content for two specific, named failure modes.

## 7. Testing

- `EditRuleCommandHandlerTests`: same-name happy path (delete-then-create, both audited);
  rename happy path (create-then-delete, both audited); same-name delete-fails (create never
  attempted, single failure reported); same-name delete-succeeds-create-fails (the distinct
  "no longer exists" message, both audit rows present — one success, one failure); rename
  create-fails (delete never attempted); rename create-succeeds-delete-fails (the distinct
  "must be deleted manually" message).
- `CreateRuleDialogTests`: pre-fill from a `SqlRuleSummary` (name/expression populated, mode
  defaults to SQL); pre-fill from a `CorrelationRuleSummary` (mode defaults to correlation, all
  8 fields and property rows populated); title/button text reflect edit vs. add; saving in
  edit mode calls `EditRuleCommandHandler`, not `CreateRuleCommandHandler`; saving in edit mode
  goes through `IConfirmationService` first.
- `TopicsPageTests`: the Edit button opens the dialog with the row's current values; a
  non-canceled edit result triggers `RefreshRulesAsync`.

Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

## 8. Out of scope

- The SQL/correlation rule "action" clause, sessions, scheduled messages, message deferral,
  auto-forwarding, duplicate detection, namespace-level settings, and queue/subscription
  update operations — separate, independent slices in the same backlog, not part of this one.
- Bulk edit (editing more than one rule in a single action).
