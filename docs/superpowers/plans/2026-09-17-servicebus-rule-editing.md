# Service Bus subscription rule editing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user edit an existing filter rule (SQL or correlation) by pre-filling the existing "+ Add rule" dialog with its current values, instead of manually deleting it and re-typing a new one.

**Architecture:** A new `EditRuleCommandHandler` orchestrates the delete-then-create (or create-then-delete, when renaming) sequence Azure's lack of an update-rule API forces, reusing the existing `IServiceBusOperations.CreateRuleAsync`/`DeleteRuleAsync` and the existing `rule.create`/`rule.delete` audit actions. `CreateRuleDialog.razor` gains an optional `ExistingRule` parameter that pre-fills its fields and switches its save path to the new handler. `Topics.razor` gains an "Edit" button per rule row.

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor 9.9.0, `Azure.Messaging.ServiceBus` 7.20.2 (plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`).

**Base branch:** `feature/servicebus-correlation-filter-fields` (PR #9, not yet merged, itself stacked on PR #8 → PR #7). Every file this plan touches was last modified there.

## Global Constraints

- Renaming (new name ≠ original name): create-then-delete — safe order, brief window where both exist (design §2).
- Same name (the common case): delete-then-create — Azure's forced order, brief window where neither exists. If create fails after delete succeeds, the error text is exactly: `Rule '{OriginalName}' was deleted but the update could not be created ({error}) — it no longer exists and must be re-added.` (design §3).
- If create succeeds but the old-rule delete fails during a rename, the error text is exactly: `A new rule '{NewName}' was created, but the old rule '{OriginalName}' could not be removed ({error}) and must be deleted manually.` (design §3).
- No new `IServiceBusOperations` methods and no new audit action — every edit is recorded through the existing `rule.create`/`rule.delete` actions, two audit rows per edit (design §3).
- Confirmation happens at Save time inside the dialog (after the user has decided what to change), not when the dialog opens (design §4).
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: `EditRuleCommandHandler`

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Rules/EditRuleCommandHandler.cs`
- Create: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/EditRuleCommandHandlerTests.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.CreateRuleAsync`/`DeleteRuleAsync` (existing, unchanged), `CreateRuleRequest` (existing).
- Produces: `EditRuleCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName, string OriginalName, CreateRuleRequest NewRule)` and `EditRuleCommandHandler.HandleAsync(EditRuleCommand, CancellationToken) : Task<PluginResult>` — Task 2's dialog calls this by name.

**Deliverable this task proves:** the command/handler layer can edit a rule (same-name or rename) correctly, including both failure-window messages, entirely independent of any UI — proven by tests substituting `IServiceBusOperations` directly.

- [x] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Rules/EditRuleCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class EditRuleCommandHandlerTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public EditRuleCommandHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
    }

    private EditRuleCommandHandler Handler => new(_operations, _connections, _audit, NullLogger<EditRuleCommandHandler>.Instance);

    [Fact]
    public async Task Same_name_edit_deletes_then_creates_and_audits_both()
    {
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", newRule, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_creates_then_deletes_and_audits_both()
    {
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _operations.CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", newRule, Arg.Any<CancellationToken>());
            _operations.DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        });
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Same_name_edit_stops_and_reports_when_the_delete_fails()
    {
        _operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule not found");
        await _operations.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordAsync("rule.create", Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Same_name_edit_reports_the_stranded_rule_message_when_create_fails_after_delete_succeeds()
    {
        _operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("quota exceeded")));
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Rule 'HighPriority' was deleted but the update could not be created (quota exceeded) — it no longer exists and must be re-added.");
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, false, "quota exceeded", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_stops_and_reports_when_the_create_fails()
    {
        _operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("a rule with this name already exists")));
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("a rule with this name already exists");
        await _operations.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, false, "a rule with this name already exists", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordAsync("rule.delete", Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_reports_the_manual_cleanup_message_when_delete_fails_after_create_succeeds()
    {
        _operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("A new rule 'HighestPriority' was created, but the old rule 'HighPriority' could not be removed (rule not found) and must be deleted manually.");
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_connection_fails_without_calling_operations()
    {
        var missingConnectionId = Guid.NewGuid();
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(missingConnectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EditRuleCommandHandlerTests"`
Expected: FAIL to compile — `EditRuleCommand`/`EditRuleCommandHandler` don't exist yet.

- [x] **Step 3: Implement `EditRuleCommandHandler.cs`**

`src/SbConsole.Plugins.ServiceBus/Rules/EditRuleCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record EditRuleCommand(
    Guid ConnectionId, string ConnectionName, bool IsProd,
    string TopicName, string SubscriptionName, string OriginalName, CreateRuleRequest NewRule);

public sealed class EditRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<EditRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(EditRuleCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        return cmd.NewRule.Name == cmd.OriginalName
            ? await DeleteThenCreateAsync(cmd, secret, ct)
            : await CreateThenDeleteAsync(cmd, secret, ct);
    }

    private async Task<PluginResult> DeleteThenCreateAsync(EditRuleCommand cmd, string secret, CancellationToken ct)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.OriginalName}";
        try
        {
            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.OriginalName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} (for edit) failed.", target);
            await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);

        try
        {
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.NewRule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} (for edit) failed after the original was deleted.", target);
            await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail($"Rule '{cmd.OriginalName}' was deleted but the update could not be created ({FriendlyError.From(ex)}) — it no longer exists and must be re-added.");
        }

        await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }

    private async Task<PluginResult> CreateThenDeleteAsync(EditRuleCommand cmd, string secret, CancellationToken ct)
    {
        var newTarget = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.NewRule.Name}";
        var oldTarget = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.OriginalName}";
        try
        {
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.NewRule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} (for edit/rename) failed.", newTarget);
            await audit.RecordAsync("rule.create", newTarget, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.create", newTarget, ActionRisk.Mutating, succeeded: true, ct: ct);

        try
        {
            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.OriginalName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} (for edit/rename) failed after the new rule was created.", oldTarget);
            await audit.RecordAsync("rule.delete", oldTarget, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail($"A new rule '{cmd.NewRule.Name}' was created, but the old rule '{cmd.OriginalName}' could not be removed ({FriendlyError.From(ex)}) and must be deleted manually.");
        }

        await audit.RecordAsync("rule.delete", oldTarget, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [x] **Step 4: Register the handler**

Open `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`. Find:

```csharp
        services.AddScoped<Rules.ListSubscriptionRulesQueryHandler>();
        services.AddScoped<Rules.CreateRuleCommandHandler>();
        services.AddScoped<Rules.DeleteRuleCommandHandler>();
```

Add a fourth line immediately after:

```csharp
        services.AddScoped<Rules.ListSubscriptionRulesQueryHandler>();
        services.AddScoped<Rules.CreateRuleCommandHandler>();
        services.AddScoped<Rules.DeleteRuleCommandHandler>();
        services.AddScoped<Rules.EditRuleCommandHandler>();
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EditRuleCommandHandlerTests"`
Expected: PASS (7 tests).

- [x] **Step 6: Run the full suite and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test passing.

```bash
git add src/SbConsole.Plugins.ServiceBus/Rules/EditRuleCommandHandler.cs src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Rules/EditRuleCommandHandlerTests.cs
git commit -m "$(cat <<'EOF'
feat: add EditRuleCommandHandler for delete-then-create rule editing

Orchestrates the safe order (create-then-delete on rename) and the
forced order (delete-then-create on a same-name edit) Azure's lack of
an update-rule API requires, reusing the existing rule.create/rule.delete
audit actions. No UI wires this up yet.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `CreateRuleDialog.razor` — pre-fill and edit save path

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`

**Interfaces:**
- Consumes: `EditRuleCommand`/`EditRuleCommandHandler` (Task 1), `IConfirmationService.ConfirmAsync(string, string, bool, int?, CancellationToken)` (existing, `SbConsole.Sdk`).
- Produces: `CreateRuleDialog.ExistingRule : RuleSummary?` and `CreateRuleDialog.IsProd : bool` parameters — Task 3's `Topics.razor` sets both when opening the dialog for Edit.

**Deliverable this task proves:** opening the dialog with an existing rule pre-fills every field correctly for both SQL and correlation rules, and saving in that mode calls `EditRuleCommandHandler` (after confirmation) instead of `CreateRuleCommandHandler`. `Topics.razor` doesn't call any of this yet — Task 3 adds the Edit button.

- [x] **Step 1: Write the failing tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`. Replace the constructor and `RenderDialog` helper:

```csharp
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateRuleDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateRuleCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog()
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CreateRuleDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateRuleDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateRuleDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(CreateRuleDialog.TopicName), "orders");
                inner.AddComponentParameter(4, nameof(CreateRuleDialog.SubscriptionName), "uk-team");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }
```

with:

```csharp
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateRuleDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateRuleCommandHandler>();
        Services.AddSingleton<EditRuleCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(RuleSummary? existingRule = null, bool isProd = false)
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CreateRuleDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateRuleDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateRuleDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(CreateRuleDialog.TopicName), "orders");
                inner.AddComponentParameter(4, nameof(CreateRuleDialog.SubscriptionName), "uk-team");
                inner.AddComponentParameter(5, nameof(CreateRuleDialog.ExistingRule), existingRule);
                inner.AddComponentParameter(6, nameof(CreateRuleDialog.IsProd), isProd);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }
```

Then add these tests after the last existing test in the file (`Saving_a_correlation_rule_with_duplicate_property_keys_keeps_the_last_value_and_does_not_crash`):

```csharp
    [Fact]
    public void Pre_fills_from_an_existing_sql_rule_and_shows_Save_not_Add()
    {
        var cut = RenderDialog(existingRule: new SqlRuleSummary("HighPriority", "Priority = 'High'"));

        cut.Find("input#rule-name").GetAttribute("value").Should().Be("HighPriority");
        cut.Find("input#rule-sql-expression").GetAttribute("value").Should().Be("Priority = 'High'");
        cut.Find("button.save-rule").TextContent.Should().Be("Save");
    }

    [Fact]
    public void Pre_fills_from_an_existing_correlation_rule_including_extra_fields_and_properties()
    {
        var cut = RenderDialog(existingRule: new CorrelationRuleSummary(
            "VipCustomers",
            new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders", MessageId: "msg-1"),
            new Dictionary<string, string> { ["tier"] = "gold" }));

        cut.Find("input#rule-name").GetAttribute("value").Should().Be("VipCustomers");
        cut.Find("input#rule-correlation-id").GetAttribute("value").Should().Be("vip-123");
        cut.Find("input#rule-label").GetAttribute("value").Should().Be("Orders");
        cut.Find("input#rule-message-id").GetAttribute("value").Should().Be("msg-1");
        cut.Find(".property-key input").GetAttribute("value").Should().Be("tier");
        cut.Find(".property-value input").GetAttribute("value").Should().Be("gold");
    }

    [Fact]
    public async Task Saving_in_edit_mode_confirms_then_calls_EditRuleCommandHandler_not_CreateRuleCommandHandler()
    {
        _confirmation.ConfirmAsync("Edit", "HighPriority", true, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderDialog(existingRule: new SqlRuleSummary("HighPriority", "Priority = 'High'"), isProd: true);
        cut.Find("input#rule-sql-expression").Change("Priority = 'Highest'");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _confirmation.Received(1).ConfirmAsync("Edit", "HighPriority", true, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateSqlRuleRequest
                && ((CreateSqlRuleRequest)r).Name == "HighPriority"
                && ((CreateSqlRuleRequest)r).SqlExpression == "Priority = 'Highest'"),
            Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_confirmation_in_edit_mode_does_not_call_the_handler()
    {
        _confirmation.ConfirmAsync("Edit", "HighPriority", false, null, Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderDialog(existingRule: new SqlRuleSummary("HighPriority", "Priority = 'High'"));

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        await _operations.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
```

Add `using SbConsole.Plugins.ServiceBus.Client;` to the file's usings if not already present (it already is, per the existing file).

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: the pre-existing tests still pass; the 4 new tests FAIL to compile — `CreateRuleDialog.ExistingRule`/`.IsProd` don't exist yet, and `EditRuleCommandHandler` isn't referenced by the dialog.

- [x] **Step 3: Implement the pre-fill, title/button switch, and edit save path**

Open `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`. Replace the `Add` button:

```razor
        <MudButton Class="save-rule" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSave)" OnClick="Save">Add</MudButton>
```

with:

```razor
        <MudButton Class="save-rule" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSave)" OnClick="Save">@(ExistingRule is null ? "Add" : "Save")</MudButton>
```

Replace the `@code` block's parameters and injected services:

```csharp
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public string SubscriptionName { get; set; } = "";

    [Inject] private CreateRuleCommandHandler CreateHandler { get; set; } = default!;
```

with:

```csharp
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public string SubscriptionName { get; set; } = "";
    [Parameter] public RuleSummary? ExistingRule { get; set; }
    [Parameter] public bool IsProd { get; set; }

    [Inject] private CreateRuleCommandHandler CreateHandler { get; set; } = default!;
    [Inject] private EditRuleCommandHandler EditHandler { get; set; } = default!;
    [Inject] private IConfirmationService Confirmation { get; set; } = default!;
```

Add an `OnInitialized` override right after the field declarations (before `CanSave`):

```csharp
    protected override void OnInitialized()
    {
        if (ExistingRule is SqlRuleSummary sql)
        {
            _mode = "sql";
            _name = sql.Name;
            _sqlExpression = sql.SqlExpression;
        }
        else if (ExistingRule is CorrelationRuleSummary correlation)
        {
            _mode = "correlation";
            _name = correlation.Name;
            _correlationId = correlation.Match.CorrelationId ?? "";
            _label = correlation.Match.Label ?? "";
            _messageId = correlation.Match.MessageId ?? "";
            _to = correlation.Match.To ?? "";
            _replyTo = correlation.Match.ReplyTo ?? "";
            _sessionId = correlation.Match.SessionId ?? "";
            _replyToSessionId = correlation.Match.ReplyToSessionId ?? "";
            _contentType = correlation.Match.ContentType ?? "";
            _showMoreMatchFields = !string.IsNullOrEmpty(_messageId) || !string.IsNullOrEmpty(_to) || !string.IsNullOrEmpty(_replyTo)
                || !string.IsNullOrEmpty(_sessionId) || !string.IsNullOrEmpty(_replyToSessionId) || !string.IsNullOrEmpty(_contentType);
            foreach (var (key, value) in correlation.Properties)
            {
                _properties.Add(new PropertyRow { Key = key, Value = value });
            }
        }
        else if (ExistingRule is not null)
        {
            // OtherRuleSummary (e.g. a TrueRuleFilter/FalseRuleFilter, or an unrecognized filter
            // kind created outside this app) has no fields this dialog can pre-fill — it falls
            // through to the same blank SQL-mode defaults as adding a brand-new rule. Name is
            // deliberately NOT pre-filled from ExistingRule.Name here: Save() reads the original
            // name from ExistingRule.Name directly, not from _name's initial value, so leaving
            // _name blank simply means the user must type a name, same as any new rule.
        }
    }
```

Replace `Save()` in full:

```csharp
    private async Task Save()
    {
        _busy = true;
        try
        {
            var properties = new Dictionary<string, string>();
            foreach (var row in _properties.Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)))
            {
                properties[row.Key] = row.Value;
            }

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

            PluginResult result;
            if (ExistingRule is null)
            {
                result = await CreateHandler.HandleAsync(new CreateRuleCommand(ConnectionId, ConnectionName, TopicName, SubscriptionName, request));
            }
            else
            {
                var confirmed = await Confirmation.ConfirmAsync("Edit", ExistingRule.Name, IsProd);
                if (!confirmed)
                {
                    return;
                }

                result = await EditHandler.HandleAsync(new EditRuleCommand(ConnectionId, ConnectionName, IsProd, TopicName, SubscriptionName, ExistingRule.Name, request));
            }

            if (result.IsSuccess)
            {
                MudDialog.Close(DialogResult.Ok(true));
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add(FriendlyError.From(ex), Severity.Error);
        }
        finally
        {
            _busy = false;
        }
    }
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: PASS (every test in this file, including the 4 new ones).

- [x] **Step 5: Run the full solution build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test passing.

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs
git commit -m "$(cat <<'EOF'
feat: pre-fill CreateRuleDialog from an existing rule for editing

ExistingRule/IsProd parameters let the same dialog serve as both Add
and Edit: pre-fills every field from a SqlRuleSummary or
CorrelationRuleSummary, switches its title/button text, and routes
Save() through a confirmation prompt and EditRuleCommandHandler
instead of CreateRuleCommandHandler. No UI opens it in edit mode yet.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `Topics.razor` — the Edit button

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`

**Interfaces:**
- Consumes: `CreateRuleDialog.ExistingRule`/`.IsProd` (Task 2).
- Produces: nothing new for later tasks — this is the final task.

**Deliverable this task proves:** a user can edit a rule end-to-end from the Topics & Subscriptions page.

- [x] **Step 1: Write the failing tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. Add these two tests after `Delete_rule_goes_through_confirmation_before_calling_the_handler`:

```csharp
    [Fact]
    public async Task Edit_rule_opens_the_dialog_pre_filled_with_the_rules_current_values()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogService.ShowAsync<CreateRuleDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.edit-rule").Click();
        await Task.Delay(30);

        await _dialogService.Received(1).ShowAsync<CreateRuleDialog>("Edit rule", Arg.Is<DialogParameters>(p =>
            p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule)) is SqlRuleSummary
            && ((SqlRuleSummary)p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule))!).Name == "HighPriority"
            && ((SqlRuleSummary)p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule))!).SqlExpression == "Priority = 'High'"
            && p.Get<bool>(nameof(CreateRuleDialog.IsProd)) == _connectionInfo.IsProd));
    }

    [Fact]
    public async Task A_non_canceled_edit_refreshes_the_rules_panel()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _dialogService.ShowAsync<CreateRuleDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.edit-rule").Click();
        await Task.Delay(30);

        await _operations.Received(2).ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }
```

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests"`
Expected: FAIL — `button.edit-rule` doesn't exist yet, so `cut.Find` throws.

- [x] **Step 3: Add the Edit button and `OpenEditRule`**

Open `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`. Replace:

```razor
                                <div class="rule-row d-flex align-center gap-2">
                                    <span style="font-family:monospace">@rule.Name — @FormatRule(rule)</span>
                                    <MudButton Class="delete-rule" Size="Size.Small" Color="Color.Error" OnClick="@(() => DeleteRuleAsync(panelRow.TopicName, panelRow.SubscriptionName, rule.Name))">Delete</MudButton>
                                </div>
```

with:

```razor
                                <div class="rule-row d-flex align-center gap-2">
                                    <span style="font-family:monospace">@rule.Name — @FormatRule(rule)</span>
                                    <MudButton Class="edit-rule" Size="Size.Small" OnClick="@(() => OpenEditRule(panelRow.TopicName, panelRow.SubscriptionName, rule))">Edit</MudButton>
                                    <MudButton Class="delete-rule" Size="Size.Small" Color="Color.Error" OnClick="@(() => DeleteRuleAsync(panelRow.TopicName, panelRow.SubscriptionName, rule.Name))">Delete</MudButton>
                                </div>
```

Add `OpenEditRule` to the `@code` block, immediately after `OpenCreateRule`:

```csharp
    private async Task OpenEditRule(string topicName, string subscriptionName, RuleSummary existingRule)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateRuleDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.TopicName, topicName },
            { x => x.SubscriptionName, subscriptionName },
            { x => x.ExistingRule, existingRule },
            { x => x.IsProd, connection.IsProd },
        };
        var dialog = await DialogService.ShowAsync<CreateRuleDialog>("Edit rule", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await RefreshRulesAsync(topicName, subscriptionName);
        }
    }
```

- [x] **Step 4: Update the `Contribution` tally**

Open `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`. Replace:

```csharp
    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter, Add/Delete rule (9 -- Send is reused, not counted
    // again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 15);
```

with:

```csharp
    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter, Add/Delete/Edit rule (10 -- Send is reused, not
    // counted again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 16);
```

Open `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`. Find the assertion on `plugin.Contribution` and update it to:

```csharp
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 16));
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~ServiceBusPluginTests"`
Expected: PASS (every test in both files).

- [x] **Step 6: Run the full solution build and test suite**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: every test across the solution passes — report the exact total from the `dotnet test` summary line.

- [x] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
git commit -m "$(cat <<'EOF'
feat: add the Edit button to the rules panel

Wires CreateRuleDialog's edit mode into Topics.razor: an Edit button
per rule row opens the dialog pre-filled via ExistingRule/IsProd, and
a non-canceled result refreshes the panel exactly like Add and Delete
already do. Updates the plugin's action tally (15 -> 16) for the new
action.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## After this plan

The SQL/correlation rule "action" clause, sessions, scheduled messages, message deferral,
auto-forwarding, duplicate detection, namespace-level settings, and queue/subscription update
operations remain out of scope, per the design doc §8 — separate, independent slices in the
same backlog. Bulk rule editing is also out of scope.
