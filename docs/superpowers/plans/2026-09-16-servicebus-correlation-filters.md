# Service Bus subscription correlation filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user add a correlation filter (CorrelationId, Label, custom application properties), not just a SQL expression, when adding a subscription filter rule — a second filter kind alongside the existing SQL-only "+ Add rule" flow.

**Architecture:** `RuleSummary` and `CreateRuleRequest` become sealed abstract records with per-kind subtypes (`SqlRuleSummary`/`CorrelationRuleSummary`/`OtherRuleSummary`, `CreateSqlRuleRequest`/`CreateCorrelationRuleRequest`). `AzureServiceBusOperations` maps between these and the real Azure SDK filter types. `CreateRuleCommand` carries a `CreateRuleRequest` instead of a flat SQL-only shape. `CreateRuleDialog.razor` gains a `MudToggleGroup` switching between SQL and correlation fields. `Topics.razor`'s rules panel pattern-matches on rule kind to render each correctly.

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor 9.9.0, `Azure.Messaging.ServiceBus` 7.20.2 (plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`).

**Base branch:** `feature/servicebus-filter-rules` (PR #7) — every file this plan touches was created by that branch, which is not yet merged to `main`. Branch this plan's work from `feature/servicebus-filter-rules`, not from `main`.

## Global Constraints

- Only `CorrelationId` and `Label` are exposed as named correlation fields, plus an open-ended custom-properties list — the other five built-in `CorrelationRuleFilter` properties (MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType) are out of scope (design §1).
- Add and Delete only — no rule editing, for either filter kind (design §1, unchanged from the SQL slice).
- The Azure SDK's `CorrelationRuleFilter` names the subject-line property `Subject` (confirmed via `ilspycmd` decompilation of the installed `Azure.Messaging.ServiceBus` 7.20.2 assembly, not assumed) — this plan's own UI/data-model layer calls the same concept "Label" throughout; the SDK-facing mapping in `AzureServiceBusOperations` is the only place the name `Subject` appears.
- `CorrelationRuleFilter.ApplicationProperties` is `IDictionary<string, object>` with an **internal setter** — it cannot be replaced via object initializer (`Properties = ...` does not compile); entries must be added to the existing dictionary instance (`filter.ApplicationProperties[key] = value`). Confirmed via decompilation.
- `IServiceBusOperations.ListRulesAsync`/`CreateRuleAsync`/`DeleteRuleAsync` signatures are unchanged — only the concrete type flowing through `RuleSummary`/`CreateRuleRequest` changes. No edit needed to `IServiceBusOperations.cs`.
- Duplicate custom-property keys are not specially validated; building the properties dictionary uses ordinary last-value-wins semantics (design §5) — not a defect, not tested.
- No new route, no new page, no new `ServiceBusPlugin.Contribution` tally change — this plan reuses the existing `rule.create`/`rule.delete` audit actions and the existing `/p/azure-servicebus/topics` page; it does not add a new action or page.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: Data model, SDK layer, and rules-panel rendering

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`

**Interfaces:**
- Consumes: nothing new — this task changes the shape of two existing types (`RuleSummary`, `CreateRuleRequest`) that `IServiceBusOperations` already references.
- Produces: `SqlRuleSummary`/`CorrelationRuleSummary`/`OtherRuleSummary` (all `: RuleSummary`), `CreateSqlRuleRequest`/`CreateCorrelationRuleRequest` (both `: CreateRuleRequest`), and `Topics.razor`'s `FormatRule(RuleSummary)` — Task 2 and Task 3 both consume `CreateSqlRuleRequest`/`CreateCorrelationRuleRequest` by name.

**Deliverable this task proves:** a subscription's rules — including correlation and non-SQL-non-correlation ("other") filter kinds created outside this app (portal, CLI, ARM) — list and render correctly in the Topics page. Rule *creation* is still SQL-only after this task; Tasks 2–3 add correlation creation.

- [ ] **Step 1: Replace `RuleSummary.cs` with the discriminated shape**

`src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public abstract record RuleSummary(string Name);

public sealed record SqlRuleSummary(string Name, string SqlExpression) : RuleSummary(Name);

public sealed record CorrelationRuleSummary(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);

public sealed record OtherRuleSummary(string Name, string RawFilterText) : RuleSummary(Name);
```

- [ ] **Step 2: Replace `CreateRuleRequest.cs` with the discriminated shape**

`src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public abstract record CreateRuleRequest(string Name);

public sealed record CreateSqlRuleRequest(string Name, string SqlExpression) : CreateRuleRequest(Name);

public sealed record CreateCorrelationRuleRequest(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
```

- [ ] **Step 3: Update `AzureServiceBusOperations.ListRulesAsync`/`CreateRuleAsync`**

Open `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`. Replace the existing `ListRulesAsync` and `CreateRuleAsync` methods (the block starting at the `ListRulesAsync` declaration and ending after `CreateRuleAsync`'s closing brace, immediately before `DeleteRuleAsync`) with:

```csharp
    public async Task<IReadOnlyList<RuleSummary>> ListRulesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var rules = new List<RuleSummary>();
        await foreach (var props in adminClient.GetRulesAsync(topicName, subscriptionName, ct).WithCancellation(ct))
        {
            rules.Add(props.Filter switch
            {
                SqlRuleFilter sqlFilter => new SqlRuleSummary(props.Name, sqlFilter.SqlExpression),
                CorrelationRuleFilter correlationFilter => new CorrelationRuleSummary(
                    props.Name,
                    correlationFilter.CorrelationId,
                    correlationFilter.Subject,
                    correlationFilter.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "")),
                _ => new OtherRuleSummary(props.Name, props.Filter.ToString() ?? ""),
            });
        }

        return rules;
    }

    public async Task CreateRuleAsync(string connectionString, string topicName, string subscriptionName, CreateRuleRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        RuleFilter filter = request switch
        {
            CreateSqlRuleRequest sql => new SqlRuleFilter(sql.SqlExpression),
            CreateCorrelationRuleRequest correlation => BuildCorrelationFilter(correlation),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown rule request kind."),
        };
        await adminClient.CreateRuleAsync(topicName, subscriptionName, new CreateRuleOptions(request.Name, filter), ct);
    }

    private static CorrelationRuleFilter BuildCorrelationFilter(CreateCorrelationRuleRequest request)
    {
        var filter = new CorrelationRuleFilter
        {
            CorrelationId = request.CorrelationId,
            Subject = request.Label,
        };
        foreach (var (key, value) in request.Properties)
        {
            filter.ApplicationProperties[key] = value;
        }

        return filter;
    }
```

Leave `DeleteRuleAsync` untouched.

- [ ] **Step 4: Fix the one call site in `CreateRuleCommandHandler.cs`**

Open `src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`. This file's `CreateRuleCommand` record still has a flat `SqlExpression` field — that reshape is Task 2's job. For now, only fix the line that no longer compiles because `CreateRuleRequest` is abstract:

```csharp
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, new CreateRuleRequest(cmd.RuleName, cmd.SqlExpression), ct);
```

becomes:

```csharp
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, new CreateSqlRuleRequest(cmd.RuleName, cmd.SqlExpression), ct);
```

Nothing else in this file changes.

- [ ] **Step 5: Fix `Topics.razor`'s rendering to compile against the new `RuleSummary` shape**

Open `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`. Find this line inside the rules-panel `@foreach` (currently reads `@rule.Name — @rule.SqlExpression`):

```razor
                                    <span style="font-family:monospace">@rule.Name — @rule.SqlExpression</span>
```

Replace it with:

```razor
                                    <span style="font-family:monospace">@rule.Name — @FormatRule(rule)</span>
```

Then add these two methods to the `@code` block, near the other private helper methods (e.g. next to `PeekUrl`):

```csharp
    private static string FormatRule(RuleSummary rule) => rule switch
    {
        SqlRuleSummary sql => sql.SqlExpression,
        CorrelationRuleSummary correlation => FormatCorrelationRule(correlation),
        OtherRuleSummary other => other.RawFilterText,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule kind."),
    };

    private static string FormatCorrelationRule(CorrelationRuleSummary rule)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(rule.CorrelationId))
        {
            parts.Add($"CorrelationId: {rule.CorrelationId}");
        }
        if (!string.IsNullOrEmpty(rule.Label))
        {
            parts.Add($"Label: {rule.Label}");
        }
        foreach (var (key, value) in rule.Properties)
        {
            parts.Add($"{key}: {value}");
        }

        return parts.Count == 0 ? "(matches all messages)" : string.Join(" · ", parts);
    }
```

- [ ] **Step 6: Fix the existing tests broken by the type change — `ListSubscriptionRulesQueryHandlerTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`. In `Returns_the_subscriptions_rules`, replace:

```csharp
        operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'") });

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeTrue();
        var rule = result.Value.Should().ContainSingle().Subject;
        rule.Name.Should().Be("HighPriority");
        rule.SqlExpression.Should().Be("Priority = 'High'");
```

with:

```csharp
        operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeTrue();
        var rule = result.Value.Should().ContainSingle().Subject.Should().BeOfType<SqlRuleSummary>().Subject;
        rule.Name.Should().Be("HighPriority");
        rule.SqlExpression.Should().Be("Priority = 'High'");
```

The other two tests in this file (`Missing_connection_fails_without_calling_operations`, `Azure_failure_is_reported_through_FriendlyError`) reference `RuleSummary` only via `Arg.Any<...>`/exception generics — no change needed there.

- [ ] **Step 7: Fix `CreateRuleCommandHandlerTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`. In both tests, replace:

```csharp
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r.Name == "HighPriority" && r.SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
```

with:

```csharp
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r is CreateSqlRuleRequest { Name: "HighPriority", SqlExpression: "Priority = 'High'" }), Arg.Any<CancellationToken>());
```

(This line appears once in `Creates_the_rule_and_audits_as_mutating`.) In `Azure_failure_is_reported_and_audited_as_failed`, replace:

```csharp
        operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
```

— this line needs no change (`Arg.Any<CreateRuleRequest>()` still compiles against the abstract base). Leave it as-is.

- [ ] **Step 8: Fix `CreateRuleDialogTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. In both `Save_closes_the_dialog_when_the_handler_succeeds` and `Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails`, replace:

```csharp
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r.Name == "HighPriority" && r.SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
```

with:

```csharp
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r is CreateSqlRuleRequest { Name: "HighPriority", SqlExpression: "Priority = 'High'" }), Arg.Any<CancellationToken>());
```

(This assertion only appears in the first test; the second test's `.Returns(...)` line uses `Arg.Any<CreateRuleRequest>()`, which needs no change.)

- [ ] **Step 9: Fix `TopicsPageTests.cs`'s existing `RuleSummary` constructions**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. Six existing `.Returns(new List<RuleSummary> { ... })`/`.Returns(new List<RuleSummary>())` calls construct `RuleSummary` directly via its old positional constructor. Replace every occurrence of:

```csharp
new List<RuleSummary> { new("HighPriority", "Priority = 'High'"), new("LowPriority", "Priority = 'Low'") }
```

with:

```csharp
new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'"), new SqlRuleSummary("LowPriority", "Priority = 'Low'") }
```

(in `Rules_chip_shows_the_live_count_after_the_topic_expands`), and every occurrence of:

```csharp
new List<RuleSummary> { new("HighPriority", "Priority = 'High'") }
```

with:

```csharp
new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") }
```

(in `Expanding_a_subscriptions_rules_panel_reveals_its_rules_without_a_second_fetch`, `Delete_rule_goes_through_confirmation_before_calling_the_handler`). The two calls constructing `new List<RuleSummary>()` (empty list, in `A_subscription_with_no_rules_shows_an_honest_empty_state_in_its_panel` and `Creating_a_subscription_fetches_its_rules_even_when_the_topic_starts_collapsed`) need no change — an empty list has no element construction to fix.

- [ ] **Step 10: Run the full suite to confirm the mechanical fixes are complete**

Run: `dotnet build -warnaserror && dotnet test`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` and every test passes (250/250, unchanged from before this task — nothing new yet).

- [ ] **Step 11: Write the failing test for mixed-kind rule listing**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`. Add this test after `Returns_the_subscriptions_rules`:

```csharp
    [Fact]
    public async Task Returns_a_mix_of_sql_correlation_and_other_rules()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary>
            {
                new SqlRuleSummary("HighPriority", "Priority = 'High'"),
                new CorrelationRuleSummary("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" }),
                new OtherRuleSummary("$Default", "TrueFilter"),
            });

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value.Should().Contain(r => r is SqlRuleSummary { Name: "HighPriority", SqlExpression: "Priority = 'High'" });
        result.Value.Should().Contain(r => r is CorrelationRuleSummary { Name: "VipCustomers", CorrelationId: "vip-123", Label: "Orders" } c && c.Properties["tier"] == "gold");
        result.Value.Should().Contain(r => r is OtherRuleSummary { Name: "$Default", RawFilterText: "TrueFilter" });
    }
```

- [ ] **Step 12: Run it to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ListSubscriptionRulesQueryHandlerTests"`
Expected: PASS (4 tests — the 3 pre-existing plus this new one). This test passes immediately because Steps 1–3 already implemented the pass-through behavior the handler needs (it has no logic of its own beyond forwarding `IServiceBusOperations`'s result) — it is here to pin that behavior, not to drive new implementation.

- [ ] **Step 13: Write the failing tests for correlation/other rule rendering in `Topics.razor`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. Add these two tests after `Expanding_a_subscriptions_rules_panel_reveals_its_rules_without_a_second_fetch`:

```csharp
    [Fact]
    public async Task A_correlation_rules_panel_row_renders_its_id_label_and_properties()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" }) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("VipCustomers");
        cut.Markup.Should().Contain("CorrelationId: vip-123");
        cut.Markup.Should().Contain("Label: Orders");
        cut.Markup.Should().Contain("tier: gold");
    }

    [Fact]
    public async Task An_other_kind_rules_panel_row_renders_its_raw_filter_text()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new OtherRuleSummary("$Default", "TrueFilter") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("$Default");
        cut.Markup.Should().Contain("TrueFilter");
    }
```

- [ ] **Step 14: Run them to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests"`
Expected: PASS (all tests in this file, including the 2 new ones). Like Step 12, this pins behavior Step 5 already implemented.

- [ ] **Step 15: Run the full suite and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test passing (250 pre-existing plus the 1 new handler test and 2 new page tests added in this task — report the exact total from the `dotnet test` summary line).

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs
git commit -m "$(cat <<'EOF'
feat: model correlation and other rule kinds, list and render them

RuleSummary/CreateRuleRequest become discriminated (Sql/Correlation/Other)
so a subscription's existing correlation-filter and non-SQL rules list and
render correctly. Rule creation is still SQL-only until the next commits.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `CreateRuleCommand` carries a `CreateRuleRequest`

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`

**Interfaces:**
- Consumes: `CreateSqlRuleRequest`/`CreateCorrelationRuleRequest` (Task 1).
- Produces: `CreateRuleCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, CreateRuleRequest Rule)` — Task 3's dialog builds the `Rule` value and constructs this command.

**Deliverable this task proves:** the command/handler layer can create a rule of either kind when given one, proven by a handler test that passes a `CreateCorrelationRuleRequest` straight through to `IServiceBusOperations.CreateRuleAsync`. The dialog UI is still SQL-only after this task (Task 3 adds the UI); this task only reshapes the pipe between the dialog and the handler.

- [ ] **Step 1: Write the failing test for correlation-rule creation through the handler**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`. Add this test after `Creates_the_rule_and_audits_as_mutating`:

```csharp
    [Fact]
    public async Task Creates_a_correlation_rule_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();
        var request = new CreateCorrelationRuleRequest("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" });

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/VipCustomers", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
```

This will not compile yet: `CreateRuleCommand` doesn't accept a `CreateRuleRequest` as its fifth argument.

- [ ] **Step 2: Run it to verify it fails to compile**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleCommandHandlerTests"`
Expected: FAIL to compile — `CreateRuleCommand` has no constructor taking `(Guid, string, string, string, CreateCorrelationRuleRequest)`.

- [ ] **Step 3: Reshape `CreateRuleCommand` and `CreateRuleCommandHandler`**

Open `src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`. Replace the whole file with:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record CreateRuleCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, CreateRuleRequest Rule);

public sealed class CreateRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateRuleCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.Rule.Name}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.Rule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} failed.", target);
            await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [ ] **Step 4: Fix the now-broken existing handler test**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`. In both `Creates_the_rule_and_audits_as_mutating` and `Azure_failure_is_reported_and_audited_as_failed`, replace:

```csharp
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", "HighPriority", "Priority = 'High'"));
```

with:

```csharp
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", new CreateSqlRuleRequest("HighPriority", "Priority = 'High'")));
```

- [ ] **Step 5: Fix the now-broken dialog call site**

Open `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`. Replace:

```csharp
            var result = await CreateHandler.HandleAsync(new CreateRuleCommand(ConnectionId, ConnectionName, TopicName, SubscriptionName, _name, _sqlExpression));
```

with:

```csharp
            var result = await CreateHandler.HandleAsync(new CreateRuleCommand(ConnectionId, ConnectionName, TopicName, SubscriptionName, new CreateSqlRuleRequest(_name, _sqlExpression)));
```

The dialog is still SQL-only after this step — Task 3 replaces this single-request construction with mode-branching logic.

- [ ] **Step 6: Fix the now-broken dialog tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. The two existing tests don't construct `CreateRuleCommand` directly (the dialog does, internally) — check whether they still compile and pass as-is now that Step 5 changed the dialog's internals. Run:

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: PASS (2 tests, unchanged) — these tests assert against `_operations.CreateRuleAsync(...)`'s arguments, which still receive a `CreateSqlRuleRequest` with the same `Name`/`SqlExpression` values as before, so Task 1's Step 8 fix already made these assertions correct and no further change is needed here.

- [ ] **Step 7: Run the new test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleCommandHandlerTests"`
Expected: PASS (3 tests — the 2 pre-existing plus `Creates_a_correlation_rule_and_audits_as_mutating`).

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test passing.

```bash
git add src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs
git commit -m "$(cat <<'EOF'
feat: let CreateRuleCommand carry either a SQL or correlation rule request

CreateRuleCommand.Rule replaces the SQL-only RuleName/SqlExpression pair,
so the handler layer can create a correlation-filter rule when asked. The
dialog still only ever builds a CreateSqlRuleRequest until the next commit.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `CreateRuleDialog.razor` — the SQL/Correlation toggle

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`

**Interfaces:**
- Consumes: `CreateSqlRuleRequest`/`CreateCorrelationRuleRequest` (Task 1), `CreateRuleCommand(Guid, string, string, string, CreateRuleRequest)` (Task 2).
- Produces: nothing new for later tasks — this is the final task.

**Deliverable this task proves:** a user can create a correlation-filter rule end-to-end through the UI.

`MudToggleGroup<T>`/`MudToggleItem<T>` API confirmed via `ilspycmd` decompilation of the installed MudBlazor 9.9.0 assembly: `MudToggleGroup<T>` has bindable `Value`/`ValueChanged` (works with `@bind-Value`); each `MudToggleItem<T>` renders internally as a `MudButton` with its own `Class` parameter flowing through to that button's rendered `class` attribute (confirmed by reading `MudToggleItem<T>.BuildRenderTree`) — so `cut.Find("button.rule-mode-correlation").Click()` works exactly like every other button-click assertion already in this codebase's tests.

- [ ] **Step 1: Write the failing tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. Add these three tests after `Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails`:

```csharp
    [Fact]
    public async Task Switching_to_correlation_mode_hides_the_sql_field_and_shows_correlation_fields()
    {
        var cut = RenderDialog();

        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();

        cut.FindAll("input#rule-sql-expression").Should().BeEmpty();
        cut.Find("input#rule-correlation-id").Should().NotBeNull();
        cut.Find("input#rule-label").Should().NotBeNull();
    }

    [Fact]
    public async Task Save_is_disabled_in_correlation_mode_until_at_least_one_match_field_is_set()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();

        cut.Find("button.save-rule").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#rule-correlation-id").Input("vip-123");
        cut.Render();

        cut.Find("button.save-rule").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Saving_a_correlation_rule_with_custom_properties_calls_the_handler_and_closes()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();
        cut.Find("input#rule-correlation-id").Input("vip-123");
        cut.Find("input#rule-label").Input("Orders");
        cut.Find("button.add-property-row").Click();
        cut.Render();
        cut.Find("input.property-key").Input("tier");
        cut.Find("input.property-value").Input("gold");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                {
                    Name: "VipCustomers",
                    CorrelationId: "vip-123",
                    Label: "Orders",
                } c && c.Properties["tier"] == "gold"),
            Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: the 2 pre-existing tests still pass; the 3 new tests FAIL — `button.rule-mode-correlation`, `input#rule-correlation-id`, `input#rule-label`, `button.add-property-row`, `input.property-key`/`input.property-value` don't exist yet, and `CreateCorrelationRuleRequest` is never constructed by the dialog.

- [ ] **Step 3: Implement the toggle, correlation fields, and property-row list**

Replace `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor` in full:

```razor
@using SbConsole.Plugins.ServiceBus.Rules
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="rule-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudToggleGroup T="string" @bind-Value="_mode" Class="mt-2 mb-2">
            <MudToggleItem Value="@("sql")" Class="rule-mode-sql" Text="SQL expression" />
            <MudToggleItem Value="@("correlation")" Class="rule-mode-correlation" Text="Correlation match" />
        </MudToggleGroup>
        @if (_mode == "sql")
        {
            <MudTextField id="rule-sql-expression" @bind-Value="_sqlExpression" Label="SQL expression" Placeholder="Priority = 'High'" Required="true" Immediate="true" />
        }
        else
        {
            <MudTextField id="rule-correlation-id" @bind-Value="_correlationId" Label="Correlation ID" Immediate="true" />
            <MudTextField id="rule-label" @bind-Value="_label" Label="Label" Immediate="true" />
            <MudText Typo="Typo.subtitle2" Class="mt-2">Custom properties</MudText>
            @foreach (var row in _properties)
            {
                <div class="d-flex align-center gap-2 property-row">
                    <MudTextField @bind-Value="row.Key" Label="Key" Immediate="true" Class="property-key" />
                    <MudTextField @bind-Value="row.Value" Label="Value" Immediate="true" Class="property-value" />
                    <MudIconButton Class="remove-property-row" Icon="@Icons.Material.Filled.Close" Size="Size.Small" OnClick="@(() => _properties.Remove(row))" />
                </div>
            }
            <MudButton Class="add-property-row" Size="Size.Small" OnClick="@(() => _properties.Add(new PropertyRow()))">+ Add property</MudButton>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="save-rule-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="save-rule" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSave)" OnClick="Save">Add</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public string SubscriptionName { get; set; } = "";

    [Inject] private CreateRuleCommandHandler CreateHandler { get; set; } = default!;

    private sealed class PropertyRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private string _mode = "sql";
    private string _name = "";
    private string _sqlExpression = "";
    private string _correlationId = "";
    private string _label = "";
    private readonly List<PropertyRow> _properties = [];
    private bool _busy;

    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_name)
        && !_busy
        && (_mode == "sql"
            ? !string.IsNullOrWhiteSpace(_sqlExpression)
            : !string.IsNullOrWhiteSpace(_correlationId)
              || !string.IsNullOrWhiteSpace(_label)
              || _properties.Any(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)));

    private async Task Save()
    {
        _busy = true;
        try
        {
            CreateRuleRequest request = _mode == "sql"
                ? new CreateSqlRuleRequest(_name, _sqlExpression)
                : new CreateCorrelationRuleRequest(
                    _name,
                    string.IsNullOrWhiteSpace(_correlationId) ? null : _correlationId,
                    string.IsNullOrWhiteSpace(_label) ? null : _label,
                    _properties
                        .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                        .ToDictionary(p => p.Key, p => p.Value));

            var result = await CreateHandler.HandleAsync(new CreateRuleCommand(ConnectionId, ConnectionName, TopicName, SubscriptionName, request));
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

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: PASS (5 tests — 2 pre-existing plus the 3 new ones).

- [ ] **Step 5: Run the full solution build and test suite**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: every test across the solution passes (250 before this plan, plus 1 from Task 1 Step 11, 2 from Task 1 Step 13, 1 from Task 2 Step 1, and 3 from this task's Step 1 — report the exact total from the `dotnet test` summary line).

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs
git commit -m "$(cat <<'EOF'
feat: add correlation-filter creation to the Add rule dialog

A MudToggleGroup switches the dialog between SQL expression and
correlation match modes; correlation mode exposes CorrelationId, Label,
and a repeatable custom-property list. This is the final piece needed
for a user to create a correlation-filter rule end-to-end from the UI.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## After this plan

The full built-in `CorrelationRuleFilter` property set (MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType) and rule editing remain out of scope, per the design doc §9 — natural follow-on slices if ever needed.
