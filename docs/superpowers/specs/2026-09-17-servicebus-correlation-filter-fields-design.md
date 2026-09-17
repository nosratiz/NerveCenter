# Service Bus correlation-filter full field set — Design

Status: implemented, 2026-09-17.

## 1. Context

`docs/superpowers/specs/2026-09-16-servicebus-correlation-filters-design.md` §1/§9 deliberately curated the correlation-filter dialog down to CorrelationId + Label + custom properties, explicitly deferring the remaining built-in `CorrelationRuleFilter` match fields (MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType). That slice shipped as `feature/servicebus-correlation-filters` (PR #8, stacked on the not-yet-merged PR #7). This spec is that deferred extension.

**Dependency:** builds on `feature/servicebus-correlation-filters` (PR #8) — every type this spec changes was introduced or last touched there. The implementation branch for this spec must be based on that branch, not `main`.

Scope: expose all 8 built-in `CorrelationRuleFilter` match fields (CorrelationId, Label, MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType) in the Add Rule dialog's correlation mode, plus the already-existing custom-properties list. Still Add/Delete only — no rule editing (unchanged constraint from both prior slices).

## 2. Data model

`CorrelationRuleSummary` and `CreateCorrelationRuleRequest` currently carry `CorrelationId`/`Label` as two flat nullable-string params. Growing that to 8 flat params each would make already-large positional records unwieldy, so both are regrouped around a new shared value type:

```csharp
public sealed record CorrelationMatch(
    string? CorrelationId,
    string? Label,
    string? MessageId,
    string? To,
    string? ReplyTo,
    string? SessionId,
    string? ReplyToSessionId,
    string? ContentType);

public sealed record CorrelationRuleSummary(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);

public sealed record CreateCorrelationRuleRequest(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
```

`CorrelationMatch` field order matches the SDK's own declaration order (confirmed via decompilation of the installed `Azure.Messaging.ServiceBus` 7.20.2 assembly) except `Label` is placed second, immediately after `CorrelationId` — the two fields exposed by the prior slice — so the type reads as "the fields you already know, then the rest," not an arbitrary SDK-order dump.

Every existing reference to the old 4-arg `CorrelationRuleSummary`/`CreateCorrelationRuleRequest` constructors (production code and PR #8's tests) is updated to the nested shape as part of this change — a mechanical rename, not new behavior, following the same pattern used when `RuleSummary` itself was introduced as a discriminated type.

## 3. SDK layer (`AzureServiceBusOperations`)

`ListRulesAsync`'s `CorrelationRuleFilter` mapping arm grows from 2 fields to 8, reading `correlationFilter.CorrelationId/.Subject/.MessageId/.To/.ReplyTo/.SessionId/.ReplyToSessionId/.ContentType` directly into a `CorrelationMatch`. `BuildCorrelationFilter` (used by `CreateRuleAsync`) grows symmetrically, setting all 8 SDK properties from the request's `CorrelationMatch`. Both are straight 1:1 property mappings — no new logic, no new exception handling (the existing `FriendlyError`/try-catch shape at the handler layer is untouched).

## 4. `CreateRuleDialog.razor`

CorrelationId and Label stay in their current always-visible position. The 6 new fields sit behind a collapsed section, revealed by a plain `MudButton` (e.g. "+ More match fields" / "- Fewer match fields") flipping a private bool and `@if`-rendering the extra `MudTextField`s — the same conditional-rendering pattern this dialog already uses for its SQL/correlation mode switch, not a new MudBlazor component. This keeps the common case (matching on just CorrelationId) a 2-field form while making the rest one click away.

`CanSave`'s correlation-mode check ("at least one match field set, or a complete property row") extends from checking 2 fields to checking all 8 non-null/non-whitespace `CorrelationMatch` fields — same OR-chain, more terms.

`Save()` builds a `CorrelationMatch` from the 8 text fields (blank → `null`, same normalization the prior slice already applies to CorrelationId/Label) and passes it into `CreateCorrelationRuleRequest`.

## 5. Rules panel rendering (`Topics.razor`)

`FormatCorrelationRule` grows from 2 conditional `parts.Add(...)` calls to 8, one per `CorrelationMatch` field, same "only include what's set" pattern, same `" · "` join, same `"(matches all messages)"` fallback when nothing is set. Field labels match the dialog's field labels (e.g. `MessageId: ...`, `ReplyToSessionId: ...`).

## 6. Error handling

Unchanged — `FriendlyError.From(ex)` already covers arbitrary SDK exceptions from rule creation regardless of how many correlation-filter properties are populated.

## 7. Testing

- Existing `CorrelationRuleSummary`/`CreateCorrelationRuleRequest` constructions across PR #8's test files (handler tests, dialog tests, page tests) updated to the nested `CorrelationMatch` shape.
- New dialog tests: the "More match fields" toggle reveals/hides the 6 extra fields; saving with only one of the newly-added fields set (e.g. just `MessageId`, nothing else) succeeds and produces a `CorrelationMatch` with that one field populated and the rest null.
- New rendering test: a `CorrelationRuleSummary` with several of the new fields set renders all of them in the panel.
- `AzureServiceBusOperations`'s mapping code gets no dedicated unit test, same accepted precedent as the rest of this file (`docs/design.md` §6/§8) — the build is the correctness gate for that one file, plus the reflection-based verification already done for this spec (§3 above) before any code is written against it.

Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

## 8. Out of scope

- Rule editing (still delete-then-recreate, unchanged from both prior slices).
- The SQL/correlation rule "action" clause, sessions, scheduled messages, message deferral, auto-forwarding, duplicate detection, namespace-level settings, and queue/subscription update operations — separate, independent slices in the same backlog, not part of this one.
