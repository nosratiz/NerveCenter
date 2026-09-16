# Service Bus subscription correlation filters — Design

Status: draft, 2026-09-16.

## 1. Context

`docs/superpowers/specs/2026-09-15-servicebus-filter-rules-design.md` §7
explicitly deferred correlation filters: that slice (implemented on
`feature/servicebus-filter-rules`, PR #7, not yet merged at the time of
writing) only lets a user add/view/delete SQL filter rules. This spec is
that deferred slice.

**Dependency:** every file this spec touches was created or last touched by
the filter-rules slice. The implementation branch for this spec must be
based on `feature/servicebus-filter-rules` (or on `main` once PR #7 has
merged) — never on a `main` that predates it.

Scope: correlation filters (CorrelationId, Label/Subject, and custom
application properties) as a second filter kind alongside SQL filters, in
the same "+ Add rule" dialog. Add and Delete only, same as the SQL slice —
Azure Service Bus rules have no update API regardless of filter kind.
Deliberately excluded: the full built-in property set (MessageId, To,
ReplyTo, SessionId, ReplyToSessionId, ContentType) — only CorrelationId and
Label are exposed as named fields; anything else a user needs goes in the
custom-properties list, same as it would need to today via the Azure portal
for those rarer fields.

## 2. Data model

`RuleSummary` and `CreateRuleRequest` (`Client/RuleSummary.cs`,
`Client/CreateRuleRequest.cs`) change from single flat records to sealed
abstract records with per-kind subtypes:

```csharp
public abstract record RuleSummary(string Name);
public sealed record SqlRuleSummary(string Name, string SqlExpression) : RuleSummary(Name);
public sealed record CorrelationRuleSummary(
    string Name, string? CorrelationId, string? Label, IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);
public sealed record OtherRuleSummary(string Name, string RawFilterText) : RuleSummary(Name);

public abstract record CreateRuleRequest(string Name);
public sealed record CreateSqlRuleRequest(string Name, string SqlExpression) : CreateRuleRequest(Name);
public sealed record CreateCorrelationRuleRequest(
    string Name, string? CorrelationId, string? Label, IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
```

`OtherRuleSummary` replaces today's silent `Filter.ToString()` fallback
inside `SqlRuleSummary` — a `TrueRuleFilter` or any filter shape this
plugin doesn't create itself now renders honestly as "other", rather than
being mislabeled as a SQL rule with a confusing expression string.

`IServiceBusOperations.CreateRuleAsync`'s signature is unchanged
(`CreateRuleRequest request` — callers now pass a subtype); no other
`IServiceBusOperations` member changes.

## 3. SDK layer (`AzureServiceBusOperations`)

`ListRulesAsync` pattern-matches `RuleProperties.Filter` by runtime type:
`SqlRuleFilter` → `SqlRuleSummary`; `CorrelationRuleFilter` → map its
`CorrelationId`, subject-line property (exact SDK member name confirmed by
reflection per the verification item below), and `Properties` dictionary
into `CorrelationRuleSummary`; anything else → `OtherRuleSummary` with
`Filter.ToString()`.

`CreateRuleAsync` branches on the request's runtime type: a
`CreateSqlRuleRequest` builds `new SqlRuleFilter(...)` exactly as today; a
`CreateCorrelationRuleRequest` builds `new CorrelationRuleFilter { ... }`,
setting `CorrelationId`/the subject-line property only when non-null, and
copying entries into `.Properties`.

**Open verification item for the implementation plan:** the installed
`Azure.Messaging.ServiceBus` SDK version must be reflected on to confirm
the exact member name for the subject-line property on
`CorrelationRuleFilter` (it has been named both `Label` and `Subject`
across SDK versions) and the exact type of `.Properties` (an
`IDictionary<string, object>` in some versions). This spec calls the field
"Label" in the UI/data-model layer regardless of what the SDK calls it
internally — the SDK layer is where any name/type translation happens,
consistent with how this plugin already isolates Azure SDK shapes from its
own `Client/` models.

## 4. Handlers

No changes to `Rules/ListSubscriptionRulesQueryHandler`,
`Rules/CreateRuleCommandHandler`, or `Rules/DeleteRuleCommandHandler`
beyond what the compiler requires from the `CreateRuleRequest` type
becoming abstract — their try/catch/audit/`FriendlyError` shape, audit
target string, and risk levels (`rule.create` → `Mutating`, `rule.delete`
→ `Destructive`) are unaffected.

## 5. `CreateRuleDialog.razor`

Gains a `MudToggleGroup` ("SQL expression" / "Correlation match") above the
existing `Name` field. Below the toggle:

- **SQL mode** (default, matches today): the existing SQL-expression field,
  unchanged.
- **Correlation mode**: `CorrelationId` and `Label` text fields (both
  optional), plus a repeatable custom-property row list — each row a key
  field, a value field, and a remove button, with a "+ Add property" button
  below the list. Duplicate keys are not specially validated; building the
  properties dictionary follows normal last-value-wins dictionary
  semantics, documented as acceptable (user error, not a case worth a
  validation message).

**Save is disabled** until `Name` is non-empty and, in correlation mode, at
least one of `CorrelationId`/`Label`/a property row with both key and value
filled is set. An all-empty correlation filter is valid to Azure (it
matches every message, equivalent to a `TrueRuleFilter`) but is
overwhelmingly likely to be a mistake in this dialog, so the client-side
gate exists to catch it before a network round-trip, not because Azure
would reject it.

On Save, the dialog builds a `CreateSqlRuleRequest` or
`CreateCorrelationRuleRequest` depending on the active toggle mode and
calls `CreateRuleCommandHandler` exactly as today.

## 6. Rules panel rendering (`Topics.razor`)

Each rule row pattern-matches on its `RuleSummary` subtype:
- `SqlRuleSummary` → today's behavior, unchanged (`Name — SqlExpression`).
- `CorrelationRuleSummary` → `Name` plus a compact single line joining only
  the parts that are set, e.g. `CorrelationId: X · Label: Y · prop1: val1`
  (omitting `·`-separated parts whose value is null/empty; if nothing is
  set at all — the degenerate all-empty case, only reachable via a rule
  created outside this UI — render `(matches all messages)`).
- `OtherRuleSummary` → its raw filter text, same spirit as today's
  fallback.

No change to the rule count chip (still a count, kind-agnostic), the fetch/
cache mechanism, or the Delete button (still name-only, filter-kind
agnostic).

## 7. Error handling

No new exception types or mapping logic — `FriendlyError.From(ex)` already
covers arbitrary `ServiceBusException`/`RequestFailedException` instances
from the administration client for both filter kinds (name conflicts,
malformed correlation values, not-found on delete).

## 8. Testing

Same layered approach as the SQL slice:
- Handler tests: `CreateRuleCommandHandler` gets a correlation-filter happy
  path alongside its existing SQL case; `ListSubscriptionRulesQueryHandler`
  gets a case returning a mix of SQL/correlation/other rules.
- `CreateRuleDialogTests`: toggle-mode interaction (switching modes swaps
  the visible fields), a correlation-mode save (with and without custom
  properties), and the Save-disabled-when-all-empty case.
- `TopicsPageTests`: a correlation rule renders its compact summary line in
  the panel; an "other" rule renders its raw text.
- `AzureServiceBusOperations`'s new/changed code gets no dedicated unit
  test, same accepted precedent as every other SDK-calling method in this
  class (`docs/design.md` §6/§8) — the build is the correctness gate for
  that file, plus the reflection-based verification called out in §3 above
  done once, in the plan, before any code is written against it.

Gate: `dotnet build -warnaserror` and `dotnet test` green before every
commit.

## 9. Out of scope

- Rule editing (still delete-then-recreate, both filter kinds).
- The full built-in `CorrelationRuleFilter` property set beyond
  CorrelationId/Label (MessageId, To, ReplyTo, SessionId,
  ReplyToSessionId, ContentType) — see §1.
- The SQL/correlation rule "action" clause (property injection on match) —
  distinct from the filter itself, not mentioned by the original slice
  either.
- Dashboard problems/alerting on rule count or rule kind.
