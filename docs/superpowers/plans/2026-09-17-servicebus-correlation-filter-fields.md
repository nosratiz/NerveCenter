# Service Bus correlation-filter full field set Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the remaining 6 built-in `CorrelationRuleFilter` match fields (MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType) in the Add Rule dialog's correlation mode, alongside the already-shipped CorrelationId/Label/custom-properties.

**Architecture:** `CorrelationRuleSummary`/`CreateCorrelationRuleRequest` regroup their match fields into a new `CorrelationMatch` value type (8 optional fields) instead of 2 flat params. `AzureServiceBusOperations` maps all 8 to/from the real Azure SDK's `CorrelationRuleFilter` properties. `CreateRuleDialog.razor` keeps CorrelationId/Label always visible and reveals the other 6 behind a plain toggle button (no new MudBlazor component). `Topics.razor`'s rendering grows to cover all 8 fields.

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor 9.9.0, `Azure.Messaging.ServiceBus` 7.20.2 (plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`).

**Base branch:** `feature/servicebus-correlation-filters` (PR #8, not yet merged, itself stacked on `feature/servicebus-filter-rules` PR #7). Every file this plan touches was last modified there.

## Global Constraints

- All 8 built-in `CorrelationRuleFilter` match fields (CorrelationId, Label, MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType) — no others; the custom-properties list is unchanged (design §1).
- `CorrelationMatch` field order: CorrelationId, Label, then the remaining 6 in the SDK's own declaration order (design §2) — MessageId, To, ReplyTo, SessionId, ReplyToSessionId, ContentType.
- The SDK names the subject-line property `Subject`; this app calls it `Label` everywhere except inside `AzureServiceBusOperations`, the one translation point (unchanged constraint from the prior slice).
- Still Add/Delete only — no rule editing (design §1).
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: `CorrelationMatch` value type, SDK layer, and rendering

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CorrelationMatch.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `CorrelationMatch(string? CorrelationId = null, string? Label = null, string? MessageId = null, string? To = null, string? ReplyTo = null, string? SessionId = null, string? ReplyToSessionId = null, string? ContentType = null)`, and the reshaped `CorrelationRuleSummary(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties)` / `CreateCorrelationRuleRequest(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties)` — Task 2's dialog UI consumes `CorrelationMatch` by name.

**Deliverable this task proves:** a correlation rule with any of the 8 built-in match fields set — including ones created outside this app (portal, CLI) — lists and renders correctly. The dialog can still only set CorrelationId/Label after this task (Task 2 adds the other 6 to the UI); this task's own dialog change is a minimal, mechanical fix to keep it compiling against the new `CorrelationMatch` shape.

- [x] **Step 1: Create `CorrelationMatch.cs`**

`src/SbConsole.Plugins.ServiceBus/Client/CorrelationMatch.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CorrelationMatch(
    string? CorrelationId = null,
    string? Label = null,
    string? MessageId = null,
    string? To = null,
    string? ReplyTo = null,
    string? SessionId = null,
    string? ReplyToSessionId = null,
    string? ContentType = null);
```

- [x] **Step 2: Reshape `CorrelationRuleSummary` in `RuleSummary.cs`**

Open `src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`. Replace:

```csharp
public sealed record CorrelationRuleSummary(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);
```

with:

```csharp
public sealed record CorrelationRuleSummary(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);
```

- [x] **Step 3: Reshape `CreateCorrelationRuleRequest` in `CreateRuleRequest.cs`**

Open `src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`. Replace:

```csharp
public sealed record CreateCorrelationRuleRequest(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
```

with:

```csharp
public sealed record CreateCorrelationRuleRequest(string Name, CorrelationMatch Match, IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
```

- [x] **Step 4: Update `AzureServiceBusOperations`'s correlation-filter mapping**

Open `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`. In `ListRulesAsync`, replace the `CorrelationRuleFilter` switch arm:

```csharp
                CorrelationRuleFilter correlationFilter => new CorrelationRuleSummary(
                    props.Name,
                    correlationFilter.CorrelationId,
                    correlationFilter.Subject,
                    correlationFilter.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "")),
```

with:

```csharp
                CorrelationRuleFilter correlationFilter => new CorrelationRuleSummary(
                    props.Name,
                    new CorrelationMatch(
                        CorrelationId: correlationFilter.CorrelationId,
                        Label: correlationFilter.Subject,
                        MessageId: correlationFilter.MessageId,
                        To: correlationFilter.To,
                        ReplyTo: correlationFilter.ReplyTo,
                        SessionId: correlationFilter.SessionId,
                        ReplyToSessionId: correlationFilter.ReplyToSessionId,
                        ContentType: correlationFilter.ContentType),
                    correlationFilter.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "")),
```

Then replace `BuildCorrelationFilter`:

```csharp
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

with:

```csharp
    private static CorrelationRuleFilter BuildCorrelationFilter(CreateCorrelationRuleRequest request)
    {
        var filter = new CorrelationRuleFilter
        {
            CorrelationId = request.Match.CorrelationId,
            Subject = request.Match.Label,
            MessageId = request.Match.MessageId,
            To = request.Match.To,
            ReplyTo = request.Match.ReplyTo,
            SessionId = request.Match.SessionId,
            ReplyToSessionId = request.Match.ReplyToSessionId,
            ContentType = request.Match.ContentType,
        };
        foreach (var (key, value) in request.Properties)
        {
            filter.ApplicationProperties[key] = value;
        }

        return filter;
    }
```

- [x] **Step 5: Minimal fix to keep `CreateRuleDialog.razor` compiling**

Open `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`. In `Save()`, replace:

```csharp
            CreateRuleRequest request = _mode == "sql"
                ? new CreateSqlRuleRequest(_name, _sqlExpression)
                : new CreateCorrelationRuleRequest(
                    _name,
                    string.IsNullOrWhiteSpace(_correlationId) ? null : _correlationId,
                    string.IsNullOrWhiteSpace(_label) ? null : _label,
                    properties);
```

with:

```csharp
            CreateRuleRequest request = _mode == "sql"
                ? new CreateSqlRuleRequest(_name, _sqlExpression)
                : new CreateCorrelationRuleRequest(
                    _name,
                    new CorrelationMatch(
                        CorrelationId: string.IsNullOrWhiteSpace(_correlationId) ? null : _correlationId,
                        Label: string.IsNullOrWhiteSpace(_label) ? null : _label),
                    properties);
```

The dialog's UI is unchanged in this step — still only CorrelationId/Label fields visible. `CorrelationMatch`'s other 6 fields default to `null` since they're not named here.

- [x] **Step 6: Update `Topics.razor`'s rendering for all 8 fields**

Open `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`. Replace `FormatCorrelationRule`:

```csharp
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

with:

```csharp
    private static string FormatCorrelationRule(CorrelationRuleSummary rule)
    {
        var parts = new List<string>();
        AddIfSet(parts, "CorrelationId", rule.Match.CorrelationId);
        AddIfSet(parts, "Label", rule.Match.Label);
        AddIfSet(parts, "MessageId", rule.Match.MessageId);
        AddIfSet(parts, "To", rule.Match.To);
        AddIfSet(parts, "ReplyTo", rule.Match.ReplyTo);
        AddIfSet(parts, "SessionId", rule.Match.SessionId);
        AddIfSet(parts, "ReplyToSessionId", rule.Match.ReplyToSessionId);
        AddIfSet(parts, "ContentType", rule.Match.ContentType);
        foreach (var (key, value) in rule.Properties)
        {
            parts.Add($"{key}: {value}");
        }

        return parts.Count == 0 ? "(matches all messages)" : string.Join(" · ", parts);
    }

    private static void AddIfSet(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parts.Add($"{label}: {value}");
        }
    }
```

- [x] **Step 7: Fix `ListSubscriptionRulesQueryHandlerTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`. In `Returns_a_mix_of_sql_correlation_and_other_rules`, replace:

```csharp
                new CorrelationRuleSummary("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" }),
```

with:

```csharp
                new CorrelationRuleSummary("VipCustomers", new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders"), new Dictionary<string, string> { ["tier"] = "gold" }),
```

Replace:

```csharp
        result.Value.Should().Contain(r => r is CorrelationRuleSummary && ((CorrelationRuleSummary)r).Name == "VipCustomers" && ((CorrelationRuleSummary)r).CorrelationId == "vip-123" && ((CorrelationRuleSummary)r).Label == "Orders" && ((CorrelationRuleSummary)r).Properties["tier"] == "gold");
```

with:

```csharp
        result.Value.Should().Contain(r => r is CorrelationRuleSummary && ((CorrelationRuleSummary)r).Name == "VipCustomers" && ((CorrelationRuleSummary)r).Match.CorrelationId == "vip-123" && ((CorrelationRuleSummary)r).Match.Label == "Orders" && ((CorrelationRuleSummary)r).Properties["tier"] == "gold");
```

- [x] **Step 8: Fix `CreateRuleCommandHandlerTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`. In `Creates_a_correlation_rule_and_audits_as_mutating`, replace:

```csharp
        var request = new CreateCorrelationRuleRequest("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" });
```

with:

```csharp
        var request = new CreateCorrelationRuleRequest("VipCustomers", new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders"), new Dictionary<string, string> { ["tier"] = "gold" });
```

- [x] **Step 9: Fix `CreateRuleDialogTests.cs`**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. In `Saving_a_correlation_rule_with_custom_properties_calls_the_handler_and_closes` only (the other two correlation tests reference `.Properties` alone and need no change), replace:

```csharp
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Name == "VipCustomers"
                && ((CreateCorrelationRuleRequest)r).CorrelationId == "vip-123"
                && ((CreateCorrelationRuleRequest)r).Label == "Orders"
                && ((CreateCorrelationRuleRequest)r).Properties["tier"] == "gold"),
```

with:

```csharp
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Name == "VipCustomers"
                && ((CreateCorrelationRuleRequest)r).Match.CorrelationId == "vip-123"
                && ((CreateCorrelationRuleRequest)r).Match.Label == "Orders"
                && ((CreateCorrelationRuleRequest)r).Properties["tier"] == "gold"),
```

- [x] **Step 10: Fix `TopicsPageTests.cs`'s existing correlation-rendering test construction**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. In `A_correlation_rules_panel_row_renders_its_id_label_and_properties`, replace:

```csharp
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary("VipCustomers", "vip-123", "Orders", new Dictionary<string, string> { ["tier"] = "gold" }) });
```

with:

```csharp
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary("VipCustomers", new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders"), new Dictionary<string, string> { ["tier"] = "gold" }) });
```

The test's markup assertions (`"CorrelationId: vip-123"`, `"Label: Orders"`, `"tier: gold"`) are unchanged — same rendered output, only the construction call changes.

- [x] **Step 11: Run the full suite to confirm the mechanical fixes are complete**

Run: `dotnet build -warnaserror && dotnet test`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` and every test passes (259/259, unchanged from before this task).

- [x] **Step 12: Write the failing test for full-field-set rendering**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. Add this test after `A_correlation_rules_panel_row_renders_its_id_label_and_properties`:

```csharp
    [Fact]
    public async Task A_correlation_rule_with_additional_match_fields_renders_all_of_them()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary(
                "VipCustomers",
                new CorrelationMatch(MessageId: "msg-1", To: "sales", ReplyTo: "support", SessionId: "sess-1", ReplyToSessionId: "sess-2", ContentType: "application/json"),
                new Dictionary<string, string>()) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("MessageId: msg-1");
        cut.Markup.Should().Contain("To: sales");
        cut.Markup.Should().Contain("ReplyTo: support");
        cut.Markup.Should().Contain("SessionId: sess-1");
        cut.Markup.Should().Contain("ReplyToSessionId: sess-2");
        cut.Markup.Should().Contain("ContentType: application/json");
    }
```

- [x] **Step 13: Run it to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests"`
Expected: PASS (all tests in this file, including the new one). This test passes immediately because Step 6 already implemented the rendering — it exists to pin that behavior with a real test, not to drive new implementation.

- [x] **Step 14: Run the full suite and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test passing (260 — the 259 already on the branch plus this task's 1 new test; report the exact total from the `dotnet test` summary line).

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/CorrelationMatch.cs src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs
git commit -m "$(cat <<'EOF'
feat: model the full correlation-filter match field set

CorrelationRuleSummary/CreateCorrelationRuleRequest regroup around a new
CorrelationMatch value type covering all 8 built-in CorrelationRuleFilter
fields, not just CorrelationId/Label. AzureServiceBusOperations maps all
8 to/from the real Azure SDK filter; Topics.razor renders all 8. The
dialog can still only set CorrelationId/Label until the next commit.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `CreateRuleDialog.razor` — the "More match fields" toggle

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`

**Interfaces:**
- Consumes: `CorrelationMatch` (Task 1).
- Produces: nothing new for later tasks — this is the final task.

**Deliverable this task proves:** a user can set any of the 8 built-in correlation match fields end-to-end through the UI, not just CorrelationId/Label.

- [x] **Step 1: Write the failing tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. Add these two tests after `Save_is_disabled_in_correlation_mode_until_at_least_one_match_field_is_set`:

```csharp
    [Fact]
    public async Task Clicking_more_match_fields_reveals_the_six_additional_fields()
    {
        var cut = RenderDialog();
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();

        cut.FindAll("input#rule-message-id").Should().BeEmpty();

        cut.Find("button.toggle-more-match-fields").Click();
        cut.Render();

        cut.Find("input#rule-message-id").Should().NotBeNull();
        cut.Find("input#rule-to").Should().NotBeNull();
        cut.Find("input#rule-reply-to").Should().NotBeNull();
        cut.Find("input#rule-session-id").Should().NotBeNull();
        cut.Find("input#rule-reply-to-session-id").Should().NotBeNull();
        cut.Find("input#rule-content-type").Should().NotBeNull();
    }

    [Fact]
    public async Task Saving_a_correlation_rule_with_only_a_new_match_field_set_succeeds()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();
        cut.Find("button.toggle-more-match-fields").Click();
        cut.Render();
        cut.Find("input#rule-message-id").Input("msg-1");

        cut.Find("button.save-rule").HasAttribute("disabled").Should().BeFalse();
        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Match.MessageId == "msg-1"
                && ((CreateCorrelationRuleRequest)r).Match.CorrelationId == null
                && ((CreateCorrelationRuleRequest)r).Match.Label == null),
            Arg.Any<CancellationToken>());
    }
```

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: the pre-existing tests still pass; the 2 new tests FAIL — `button.toggle-more-match-fields`, `input#rule-message-id`, `input#rule-to`, `input#rule-reply-to`, `input#rule-session-id`, `input#rule-reply-to-session-id`, `input#rule-content-type` don't exist yet.

- [x] **Step 3: Implement the toggle and the 6 additional fields**

Open `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`. Replace the correlation-mode `else` block:

```razor
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
```

with:

```razor
        else
        {
            <MudTextField id="rule-correlation-id" @bind-Value="_correlationId" Label="Correlation ID" Immediate="true" />
            <MudTextField id="rule-label" @bind-Value="_label" Label="Label" Immediate="true" />
            <MudButton Class="toggle-more-match-fields" Size="Size.Small" OnClick="@(() => _showMoreMatchFields = !_showMoreMatchFields)">@(_showMoreMatchFields ? "- Fewer match fields" : "+ More match fields")</MudButton>
            @if (_showMoreMatchFields)
            {
                <MudTextField id="rule-message-id" @bind-Value="_messageId" Label="Message ID" Immediate="true" />
                <MudTextField id="rule-to" @bind-Value="_to" Label="To" Immediate="true" />
                <MudTextField id="rule-reply-to" @bind-Value="_replyTo" Label="Reply To" Immediate="true" />
                <MudTextField id="rule-session-id" @bind-Value="_sessionId" Label="Session ID" Immediate="true" />
                <MudTextField id="rule-reply-to-session-id" @bind-Value="_replyToSessionId" Label="Reply To Session ID" Immediate="true" />
                <MudTextField id="rule-content-type" @bind-Value="_contentType" Label="Content Type" Immediate="true" />
            }
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
```

In the `@code` block, replace:

```csharp
    private string _mode = "sql";
    private string _name = "";
    private string _sqlExpression = "";
    private string _correlationId = "";
    private string _label = "";
    private readonly List<PropertyRow> _properties = [];
    private bool _busy;
```

with:

```csharp
    private string _mode = "sql";
    private string _name = "";
    private string _sqlExpression = "";
    private string _correlationId = "";
    private string _label = "";
    private bool _showMoreMatchFields;
    private string _messageId = "";
    private string _to = "";
    private string _replyTo = "";
    private string _sessionId = "";
    private string _replyToSessionId = "";
    private string _contentType = "";
    private readonly List<PropertyRow> _properties = [];
    private bool _busy;
```

Replace `CanSave`:

```csharp
    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_name)
        && !_busy
        && (_mode == "sql"
            ? !string.IsNullOrWhiteSpace(_sqlExpression)
            : !string.IsNullOrWhiteSpace(_correlationId)
              || !string.IsNullOrWhiteSpace(_label)
              || _properties.Any(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)));
```

with:

```csharp
    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_name)
        && !_busy
        && (_mode == "sql"
            ? !string.IsNullOrWhiteSpace(_sqlExpression)
            : !string.IsNullOrWhiteSpace(_correlationId)
              || !string.IsNullOrWhiteSpace(_label)
              || !string.IsNullOrWhiteSpace(_messageId)
              || !string.IsNullOrWhiteSpace(_to)
              || !string.IsNullOrWhiteSpace(_replyTo)
              || !string.IsNullOrWhiteSpace(_sessionId)
              || !string.IsNullOrWhiteSpace(_replyToSessionId)
              || !string.IsNullOrWhiteSpace(_contentType)
              || _properties.Any(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)));
```

In `Save()`, replace the `CreateCorrelationRuleRequest` construction (from Task 1's Step 5):

```csharp
            CreateRuleRequest request = _mode == "sql"
                ? new CreateSqlRuleRequest(_name, _sqlExpression)
                : new CreateCorrelationRuleRequest(
                    _name,
                    new CorrelationMatch(
                        CorrelationId: string.IsNullOrWhiteSpace(_correlationId) ? null : _correlationId,
                        Label: string.IsNullOrWhiteSpace(_label) ? null : _label),
                    properties);
```

with:

```csharp
            CreateRuleRequest request = _mode == "sql"
                ? new CreateSqlRuleRequest(_name, _sqlExpression)
                : new CreateCorrelationRuleRequest(
                    _name,
                    new CorrelationMatch(
                        CorrelationId: string.IsNullOrWhiteSpace(_correlationId) ? null : _correlationId,
                        Label: string.IsNullOrWhiteSpace(_label) ? null : _label,
                        MessageId: string.IsNullOrWhiteSpace(_messageId) ? null : _messageId,
                        To: string.IsNullOrWhiteSpace(_to) ? null : _to,
                        ReplyTo: string.IsNullOrWhiteSpace(_replyTo) ? null : _replyTo,
                        SessionId: string.IsNullOrWhiteSpace(_sessionId) ? null : _sessionId,
                        ReplyToSessionId: string.IsNullOrWhiteSpace(_replyToSessionId) ? null : _replyToSessionId,
                        ContentType: string.IsNullOrWhiteSpace(_contentType) ? null : _contentType),
                    properties);
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: PASS (every test in this file, including the 2 new ones).

- [x] **Step 5: Run the full solution build and test suite**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: every test across the solution passes (260 before this task plus 2 new — report the exact total from the `dotnet test` summary line).

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs
git commit -m "$(cat <<'EOF'
feat: expose the full correlation match field set in the Add rule dialog

A "+ More match fields" toggle reveals MessageId/To/ReplyTo/SessionId/
ReplyToSessionId/ContentType alongside the always-visible CorrelationId/
Label, completing the full built-in CorrelationRuleFilter field set.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## After this plan

Rule editing, the SQL/correlation rule "action" clause, sessions, scheduled messages, message deferral, auto-forwarding, duplicate detection, namespace-level settings, and queue/subscription update operations remain out of scope, per the design doc §8 — separate, independent slices in the same backlog.
