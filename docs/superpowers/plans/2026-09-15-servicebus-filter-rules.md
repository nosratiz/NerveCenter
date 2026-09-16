# Service Bus subscription filter rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user view, add, and delete SQL filter rules on a Service Bus subscription, directly from the existing "Topics & Subscriptions" page — the slice `docs/superpowers/plans/2026-09-13-servicebus-topics-subscriptions-plugin.md` explicitly deferred.

**Architecture:** Additive throughout, same shape as every prior slice of this plugin. `IServiceBusOperations` gains three rule methods (list/create/delete), implemented in `AzureServiceBusOperations` against `ServiceBusAdministrationClient`. Three new handlers in a `Rules/` folder mirror the existing `Subscriptions/` handlers exactly (secret lookup, try/catch, `FriendlyError`, audit). `Topics.razor` gains a sixth table column ("Rules") and a second, nested expand/collapse level: expanding a subscription row (itself only reachable once its parent topic is expanded) reveals a full-width panel listing that subscription's rules with Delete buttons and an "+ Add rule" button. Rule data for every subscription in a topic is fetched once, in parallel, the moment that topic expands — the same fetch feeds both the "Rules" column's count chip and the later per-subscription panel, so expanding a subscription's panel never issues a second network call.

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor 9.9.0, `Azure.Messaging.ServiceBus` 7.20.2 (plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`).

## Global Constraints

- SQL filter rules only — no correlation filters (`docs/superpowers/specs/2026-09-15-servicebus-filter-rules-design.md` §1, §7).
- Add and Delete only — rules have no update API in Azure Service Bus, so no "edit" action exists anywhere in this plan (design §1).
- `IServiceBusOperations`'s existing methods, signatures, and behavior do not change; the three new methods are additive, inserted after the existing subscription dead-letter methods (design §2).
- Every Azure SDK member referenced below was confirmed by reflecting on the installed `Azure.Messaging.ServiceBus` 7.20.2 assembly (`ServiceBusAdministrationClient.GetRulesAsync(string, string, CancellationToken)`, `.CreateRuleAsync(string, string, CreateRuleOptions, CancellationToken)`, `.DeleteRuleAsync(string, string, string, CancellationToken)`; `RuleProperties.Name`/`.Filter`; `CreateRuleOptions(string name, RuleFilter filter)`; `SqlRuleFilter(string sqlExpression)`/`.SqlExpression`) — not assumed.
- Handlers follow this plugin's established naming and shape: every command/query handler wraps its `IServiceBusOperations` call in try/catch, logs the full exception via an injected `ILogger<T>`, and returns `PluginResult[<T>].Fail(ex)` (routing through `FriendlyError`). Registered in `ServiceBusPlugin.ConfigureServices` in the same task that creates them.
- `rule.create` audits `ActionRisk.Mutating`; `rule.delete` audits `ActionRisk.Destructive` — same convention as `subscription.create`/`subscription.delete`. Target string is `{ConnectionName}/{TopicName}/{SubscriptionName}/{RuleName}`.
- Destructive delete goes through the existing injected `IConfirmationService`, with `IsProd`/`ConnectionName` always looked up from the real `ConnectionInfo` (never trusted from a URL parameter) — same pattern already applied to every delete action on this page.
- No new route: this feature lives entirely inside the existing `/p/azure-servicebus/topics` page, so `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`'s route list needs no change.
- Testing strategy is unit tests only against a substitute of `IServiceBusOperations`/`IConnectionProvider`/`IConfirmationService` — no Testcontainers, no real network calls (`docs/design.md` §8). `AzureServiceBusOperations`'s own new methods get no dedicated unit test (same precedent as every other admin CRUD method in this class — they can't be meaningfully tested without a broker); the build is the correctness gate for that one file.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: SDK models and `IServiceBusOperations` rule methods

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Consumes: the existing `AzureServiceBusOperations.CreateAdministrationClientOptions()`.
- Produces: `RuleSummary(string Name, string SqlExpression)`, `CreateRuleRequest(string Name, string SqlExpression)`, and three new `IServiceBusOperations` methods (`ListRulesAsync`, `CreateRuleAsync`, `DeleteRuleAsync`) that Task 2's handlers call.

- [x] **Step 1: Create the two new model files**

`src/SbConsole.Plugins.ServiceBus/Client/RuleSummary.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record RuleSummary(string Name, string SqlExpression);
```

`src/SbConsole.Plugins.ServiceBus/Client/CreateRuleRequest.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateRuleRequest(string Name, string SqlExpression);
```

- [x] **Step 2: Add the three new methods to `IServiceBusOperations`**

Open `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`. Insert the following block immediately after `PurgeSubscriptionDeadLetterMessagesAsync` (the last member), before the closing `}` of the interface:

```csharp

    Task<IReadOnlyList<RuleSummary>> ListRulesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);

    Task CreateRuleAsync(string connectionString, string topicName, string subscriptionName, CreateRuleRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteRuleAsync(string connectionString, string topicName, string subscriptionName, string ruleName, CancellationToken ct = default);
```

- [x] **Step 3: Implement the three methods in `AzureServiceBusOperations`**

Open `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`. Insert the following block at the very end of the class, just before its closing `}`:

```csharp

    public async Task<IReadOnlyList<RuleSummary>> ListRulesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var rules = new List<RuleSummary>();
        await foreach (var props in adminClient.GetRulesAsync(topicName, subscriptionName, ct).WithCancellation(ct))
        {
            // Every rule this plugin creates is a SqlRuleFilter (CreateRuleAsync below never
            // creates any other kind), but a rule created by some other tool (portal, CLI,
            // ARM template) could be a CorrelationRuleFilter or TrueRuleFilter -- ToString()
            // keeps this list from throwing on a filter shape this plugin doesn't build itself.
            var expression = props.Filter is SqlRuleFilter sqlFilter ? sqlFilter.SqlExpression : props.Filter.ToString() ?? "";
            rules.Add(new RuleSummary(props.Name, expression));
        }

        return rules;
    }

    public async Task CreateRuleAsync(string connectionString, string topicName, string subscriptionName, CreateRuleRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.CreateRuleAsync(topicName, subscriptionName, new CreateRuleOptions(request.Name, new SqlRuleFilter(request.SqlExpression)), ct);
    }

    public async Task DeleteRuleAsync(string connectionString, string topicName, string subscriptionName, string ruleName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.DeleteRuleAsync(topicName, subscriptionName, ruleName, ct);
    }
```

- [x] **Step 4: Build and confirm no regressions**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: same pass count as before this task (no new tests yet — these three methods can't be meaningfully unit-tested without a broker, same precedent as every other admin CRUD method in this class).

- [x] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/
git commit -m "$(cat <<'EOF'
feat: add subscription rule CRUD to IServiceBusOperations

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Rule handlers

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Rules/ListSubscriptionRulesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Rules/DeleteRuleCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Rules/DeleteRuleCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.ListRulesAsync`/`CreateRuleAsync`/`DeleteRuleAsync` and `RuleSummary`/`CreateRuleRequest` from Task 1.
- Produces: `ListSubscriptionRulesQueryHandler.HandleAsync(Guid connectionId, string topicName, string subscriptionName, CancellationToken ct = default) : Task<PluginResult<IReadOnlyList<RuleSummary>>>`; `CreateRuleCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, string RuleName, string SqlExpression)` and `CreateRuleCommandHandler.HandleAsync(CreateRuleCommand, CancellationToken) : Task<PluginResult>`; `DeleteRuleCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName, string RuleName)` and `DeleteRuleCommandHandler.HandleAsync(DeleteRuleCommand, CancellationToken) : Task<PluginResult>` — all three are what Task 3/4's `Topics.razor` and `CreateRuleDialog.razor` inject and call.

- [x] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Rules/ListSubscriptionRulesQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class ListSubscriptionRulesQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_subscriptions_rules()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'") });

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeTrue();
        var rule = result.Value.Should().ContainSingle().Subject;
        rule.Name.Should().Be("HighPriority");
        rule.SqlExpression.Should().Be("Priority = 'High'");
    }

    [Fact]
    public async Task Missing_connection_fails_without_calling_operations()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var operations = Substitute.For<IServiceBusOperations>();

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await operations.DidNotReceive().ListRulesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_through_FriendlyError()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListRulesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<RuleSummary>>(new InvalidOperationException("subscription not found")));

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("subscription not found");
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Rules/CreateRuleCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class CreateRuleCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_rule_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", "HighPriority", "Priority = 'High'"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r.Name == "HighPriority" && r.SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", "HighPriority", "Priority = 'High'"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule already exists");
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, false, "rule already exists", Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Rules/DeleteRuleCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class DeleteRuleCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_rule_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteRuleCommandHandler(operations, connections, audit, NullLogger<DeleteRuleCommandHandler>.Instance)
            .HandleAsync(new DeleteRuleCommand(connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteRuleCommandHandler(operations, connections, audit, NullLogger<DeleteRuleCommandHandler>.Instance)
            .HandleAsync(new DeleteRuleCommand(connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule not found");
        await audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Rules"`
Expected: FAIL to compile — `ListSubscriptionRulesQueryHandler`, `CreateRuleCommandHandler`, `CreateRuleCommand`, `DeleteRuleCommandHandler`, `DeleteRuleCommand` don't exist yet.

- [x] **Step 3: Implement the three handlers**

`src/SbConsole.Plugins.ServiceBus/Rules/ListSubscriptionRulesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed class ListSubscriptionRulesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListSubscriptionRulesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<RuleSummary>>> HandleAsync(Guid connectionId, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<RuleSummary>>.Fail("Connection not found.");
            }

            var rules = await operations.ListRulesAsync(secret, topicName, subscriptionName, ct);
            return PluginResult<IReadOnlyList<RuleSummary>>.Ok(rules);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing rules for {TopicName}/{SubscriptionName} failed.", topicName, subscriptionName);
            return PluginResult<IReadOnlyList<RuleSummary>>.Fail(ex);
        }
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Rules/CreateRuleCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record CreateRuleCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, string RuleName, string SqlExpression);

public sealed class CreateRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateRuleCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.RuleName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, new CreateRuleRequest(cmd.RuleName, cmd.SqlExpression), ct);
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

`src/SbConsole.Plugins.ServiceBus/Rules/DeleteRuleCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record DeleteRuleCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName, string RuleName);

public sealed class DeleteRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteRuleCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.RuleName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.RuleName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} failed.", target);
            await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [x] **Step 4: Register the three handlers in `ServiceBusPlugin.ConfigureServices`**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, add after the `services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();` line:

```csharp
        services.AddScoped<Rules.ListSubscriptionRulesQueryHandler>();
        services.AddScoped<Rules.CreateRuleCommandHandler>();
        services.AddScoped<Rules.DeleteRuleCommandHandler>();
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Rules"`
Expected: PASS (7 tests: 3 for List, 2 for Create, 2 for Delete).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Rules/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Rules/
git commit -m "$(cat <<'EOF'
feat: add subscription rule list/create/delete handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `Topics.razor` — Rules column, eager per-topic fetch, and the per-subscription rules panel (view + delete)

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`

**Interfaces:**
- Consumes: `Rules.ListSubscriptionRulesQueryHandler`, `Rules.DeleteRuleCommandHandler`, `RuleSummary` from Task 2/1.
- Produces: nothing new for later tasks to consume by type — Task 4 adds to this same file (the "+ Add rule" button and `CreateRuleDialog`). This task's row-key convention (`$"{topicName}/{subscriptionName}"` as the dictionary key into `_rulesByKey`) is what Task 4's `OpenCreateRule`/refresh logic must reuse.

This task does **not** add the "+ Add rule" button yet — that arrives in Task 4 once `CreateRuleDialog` exists. A subscription with rules already present (as returned by the test double) is fully viewable and deletable after this task; there is just no in-page way yet to create the first one (matching the existing precedent that a subscription's rules come pre-seeded by whatever created it — that's fine, since this task's own tests seed rules directly through the substitute `IServiceBusOperations`, exactly like `TopicsPageTests.cs` already does for subscriptions themselves).

- [x] **Step 1: Write the failing tests**

Open `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`. Add `using SbConsole.Plugins.ServiceBus.Rules;` to the usings block, then register the new handler and the confirmation double already present:

```csharp
using SbConsole.Plugins.ServiceBus.Rules;
```

In the constructor, after `Services.AddSingleton<SendMessageCommandHandler>();`, add:

```csharp
        Services.AddSingleton<ListSubscriptionRulesQueryHandler>();
        Services.AddSingleton<DeleteRuleCommandHandler>();
```

Then add these four tests at the end of the class, before the closing `}`:

```csharp

    [Fact]
    public async Task Rules_chip_shows_the_live_count_after_the_topic_expands()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'"), new("LowPriority", "Priority = 'Low'") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".rule-count-chip").TextContent.Should().Contain("2");
    }

    [Fact]
    public async Task Expanding_a_subscriptions_rules_panel_reveals_its_rules_without_a_second_fetch()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("HighPriority");
        cut.Markup.Should().Contain("Priority = 'High'");
        await _operations.Received(1).ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_subscription_with_no_rules_shows_an_honest_empty_state_in_its_panel()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary>());

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("No rules.");
    }

    [Fact]
    public async Task Delete_rule_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'") });
        _confirmation.ConfirmAsync("Delete", "HighPriority", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.delete-rule").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests"`
Expected: FAIL — `.rule-count-chip`/`button.expand-subscription-rules`/`button.delete-rule` don't exist yet, and `IServiceBusOperations.ListRulesAsync`/`DeleteRuleAsync` are unused by the page.

- [x] **Step 3: Implement the Rules column and panel in `Topics.razor`**

Open `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`.

Add to the `@using`/`@inject` block at the top (after the existing `@inject DeleteSubscriptionCommandHandler DeleteSubscriptionHandler` line):

```razor
@using SbConsole.Plugins.ServiceBus.Rules
@inject ListSubscriptionRulesQueryHandler RulesHandler
@inject DeleteRuleCommandHandler DeleteRuleHandler
```

Replace the `<HeaderContent>` block:

```razor
        <HeaderContent>
            <MudTh>Name</MudTh>
            <MudTh>Active</MudTh>
            <MudTh>Dead-letter</MudTh>
            <MudTh>Scheduled</MudTh>
            <MudTh>Rules</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
```

In the `TopicRowEntry` branch of `<RowTemplate>`, find this existing line:

```razor
                <MudTd>@topicRow.Topic.ScheduledMessageCount</MudTd>
```

Insert one new line directly after it (before the `<MudTd>` that starts the Actions cell, the one containing the "Send" button):

```razor
                <MudTd>—</MudTd>
```

Replace the `SubscriptionRowEntry` branch entirely:

```razor
            else if (context is SubscriptionRowEntry subRow)
            {
                var rowKey = $"{subRow.TopicName}/{subRow.Subscription.Name}";
                <MudTd Style="padding-left:56px">— @subRow.Subscription.Name</MudTd>
                <MudTd>@subRow.Subscription.ActiveMessageCount</MudTd>
                <MudTd>@subRow.Subscription.DeadLetterMessageCount</MudTd>
                <MudTd>—</MudTd>
                <MudTd>
                    @if (_rulesByKey.TryGetValue(rowKey, out var subRules))
                    {
                        <MudButton Class="expand-subscription-rules" Size="Size.Small" OnClick="@(() => ToggleSubscriptionRulesExpanded(rowKey))">@(_expandedSubscriptionRules.Contains(rowKey) ? "▾" : "▸")</MudButton>
                        <MudChip T="string" Class="rule-count-chip" Size="Size.Small">@subRules.Count @(subRules.Count == 1 ? "rule" : "rules")</MudChip>
                    }
                    else if (_loadingRulesForTopic == subRow.TopicName)
                    {
                        <MudProgressCircular Class="rules-loading" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
                    }
                </MudTd>
                <MudTd>
                    <MudButton Href="@PeekUrl(subRow.TopicName, subRow.Subscription.Name)">Peek</MudButton>
                    <MudButton Class="dead-letter-action" Href="@PeekUrl(subRow.TopicName, subRow.Subscription.Name, deadLetter: true, deadLetterCount: subRow.Subscription.DeadLetterMessageCount)">Dead-letter</MudButton>
                    <MudButton Class="delete-subscription" Color="Color.Error" Disabled="@(_deletingEntity == rowKey)" OnClick="@(() => DeleteSubscriptionAsync(subRow.TopicName, subRow.Subscription.Name))">Delete</MudButton>
                    @if (_deletingEntity == rowKey)
                    {
                        <MudProgressCircular Class="delete-subscription-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                    }
                </MudTd>
            }
            else if (context is SubscriptionRulesPanelRowEntry panelRow)
            {
                <MudTd colspan="6" Class="rules-panel">
                    <div style="padding-left:80px">
                        @if (panelRow.Rules.Count == 0)
                        {
                            <MudText Typo="Typo.body2" Class="mud-text-secondary">No rules.</MudText>
                        }
                        else
                        {
                            @foreach (var rule in panelRow.Rules)
                            {
                                <div class="rule-row d-flex align-center gap-2">
                                    <span style="font-family:monospace">@rule.Name — @rule.SqlExpression</span>
                                    <MudButton Class="delete-rule" Size="Size.Small" Color="Color.Error" OnClick="@(() => DeleteRuleAsync(panelRow.TopicName, panelRow.SubscriptionName, rule.Name))">Delete</MudButton>
                                </div>
                            }
                        }
                    </div>
                </MudTd>
            }
```

In `@code`, add the new row record after `SubscriptionRowEntry`:

```csharp
    private sealed record SubscriptionRulesPanelRowEntry(string TopicName, string SubscriptionName, IReadOnlyList<RuleSummary> Rules) : TopicsTableRow;
```

Add the new state fields, next to `_deletingEntity`:

```csharp
    private readonly Dictionary<string, IReadOnlyList<RuleSummary>> _rulesByKey = new();
    private readonly HashSet<string> _expandedSubscriptionRules = [];
    private string? _loadingRulesForTopic;
```

Replace `RowClassFunc`'s lambda on the `<MudTable>` element:

```razor
    <MudTable Items="_rows" RowClassFunc="@((row, _) => row switch { SubscriptionRowEntry => "subscription-row", SubscriptionRulesPanelRowEntry => "rules-panel-row", _ => "topic-row" })">
```

Replace `FlattenRows` to also yield the panel row when a subscription's rules are expanded:

```csharp
    private IEnumerable<TopicsTableRow> FlattenRows(TopicRow topic)
    {
        var expanded = _expandedTopics.Contains(topic.Topic.Name);
        yield return new TopicRowEntry(topic.Topic, topic.ActiveMessageCount, topic.DeadLetterMessageCount, expanded);
        if (!expanded)
        {
            yield break;
        }

        foreach (var subscription in topic.Subscriptions)
        {
            var rowKey = $"{topic.Topic.Name}/{subscription.Name}";
            yield return new SubscriptionRowEntry(topic.Topic.Name, subscription);
            if (_expandedSubscriptionRules.Contains(rowKey) && _rulesByKey.TryGetValue(rowKey, out var rules))
            {
                yield return new SubscriptionRulesPanelRowEntry(topic.Topic.Name, subscription.Name, rules);
            }
        }
    }
```

Replace `ToggleExpanded` (and its call site) with an async version that eager-fetches rules the first time a topic expands. Replace the method:

```csharp
    private async Task ToggleTopicExpandedAsync(string topicName)
    {
        if (_expandedTopics.Remove(topicName))
        {
            return;
        }

        _expandedTopics.Add(topicName);

        var topic = _topics.FirstOrDefault(t => t.Topic.Name == topicName);
        if (topic is null)
        {
            return;
        }

        var toFetch = topic.Subscriptions.Where(s => !_rulesByKey.ContainsKey($"{topicName}/{s.Name}")).ToList();
        if (toFetch.Count == 0)
        {
            return;
        }

        _loadingRulesForTopic = topicName;
        try
        {
            var results = await Task.WhenAll(toFetch.Select(s => RulesHandler.HandleAsync(_selectedConnectionId, topicName, s.Name)));
            for (var i = 0; i < toFetch.Count; i++)
            {
                var key = $"{topicName}/{toFetch[i].Name}";
                _rulesByKey[key] = results[i].IsSuccess ? results[i].Value! : [];
            }
        }
        finally
        {
            _loadingRulesForTopic = null;
        }
    }

    private void ToggleSubscriptionRulesExpanded(string rowKey)
    {
        if (!_expandedSubscriptionRules.Remove(rowKey))
        {
            _expandedSubscriptionRules.Add(rowKey);
        }
    }
```

Update the topic row's expand button to call the renamed method:

```razor
                    <MudButton Class="expand-topic" OnClick="@(() => ToggleTopicExpandedAsync(topicRow.Topic.Name))">@(topicRow.IsExpanded ? "▾" : "▸")</MudButton>
```

Add cache-clearing to `OnConnectionChanged`:

```csharp
    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        _expandedTopics.Clear();
        _rulesByKey.Clear();
        _expandedSubscriptionRules.Clear();
        await LoadTopicsAsync();
    }
```

Add the delete-rule and refresh methods at the end of the class, before the closing `}`:

```csharp

    private async Task RefreshRulesAsync(string topicName, string subscriptionName)
    {
        var result = await RulesHandler.HandleAsync(_selectedConnectionId, topicName, subscriptionName);
        if (result.IsSuccess)
        {
            _rulesByKey[$"{topicName}/{subscriptionName}"] = result.Value!;
        }
        else
        {
            Snackbar.Add(result.Error!, Severity.Error);
        }
    }

    private async Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", ruleName, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        var result = await DeleteRuleHandler.HandleAsync(new DeleteRuleCommand(connection.Id, connection.Name, connection.IsProd, topicName, subscriptionName, ruleName));
        if (!result.IsSuccess)
        {
            Snackbar.Add(result.Error!, Severity.Error);
        }

        await RefreshRulesAsync(topicName, subscriptionName);
    }
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests"`
Expected: PASS (10 tests: the 6 existing plus these 4 new ones).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [x] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs
git commit -m "$(cat <<'EOF'
feat: show and delete subscription filter rules on the Topics page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `CreateRuleDialog.razor` and the "+ Add rule" action

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Create: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs`

**Interfaces:**
- Consumes: `Rules.CreateRuleCommandHandler`/`CreateRuleCommand` from Task 2; `Topics.razor`'s `RefreshRulesAsync(string, string)` from Task 3.
- Produces: `CreateRuleDialog` with `[Parameter]` properties `ConnectionId : Guid`, `ConnectionName : string`, `TopicName : string`, `SubscriptionName : string` — a self-contained dialog, nothing else depends on its internals.

- [x] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs` (mirrors `CreateSubscriptionDialogTests.cs` exactly):

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateRuleDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

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

    [Fact]
    public async Task Save_closes_the_dialog_when_the_handler_succeeds()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("HighPriority");
        cut.Find("input#rule-sql-expression").Input("Priority = 'High'");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r.Name == "HighPriority" && r.SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("HighPriority");
        cut.Find("input#rule-sql-expression").Input("Priority = 'High'");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("rule already exists"));
    }
}
```

No `TopicsPageTests.cs` addition is needed for the "+ Add rule" button itself: `Topics.razor`'s test host never renders a `MudDialogProvider` (that only exists in the real app's `MainLayout.razor`), so `DialogService.ShowAsync` never produces findable markup inside a `Topics` component test — exactly why the existing `OpenCreateTopic`/`OpenCreateSubscription` methods have no "click button, fill in the dialog, submit" test in that file either, only their button-click-to-dialog-open wiring goes untested there while the dialog's own behavior (`CreateSubscriptionDialogTests.cs`) is tested in isolation. `CreateRuleDialogTests.cs` above is this feature's equivalent: it proves `CreateRuleDialog` calls the handler correctly and closes/stays open appropriately, which is everything that can be meaningfully unit-tested about the "Add rule" flow.

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: FAIL to compile — `CreateRuleDialog` doesn't exist yet.

- [x] **Step 3: Implement `CreateRuleDialog.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor`:

```razor
@using SbConsole.Plugins.ServiceBus.Rules
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="rule-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudTextField id="rule-sql-expression" @bind-Value="_sqlExpression" Label="SQL expression" Placeholder="Priority = 'High'" Required="true" Immediate="true" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="save-rule-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="save-rule" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_name) || string.IsNullOrWhiteSpace(_sqlExpression) || _busy)" OnClick="Save">Add</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public string SubscriptionName { get; set; } = "";

    [Inject] private CreateRuleCommandHandler CreateHandler { get; set; } = default!;

    private string _name = "";
    private string _sqlExpression = "";
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var result = await CreateHandler.HandleAsync(new CreateRuleCommand(ConnectionId, ConnectionName, TopicName, SubscriptionName, _name, _sqlExpression));
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

- [x] **Step 4: Wire the "+ Add rule" button into `Topics.razor`**

Open `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`. In the `SubscriptionRulesPanelRowEntry` branch added in Task 3, add the button right after the `@if (panelRow.Rules.Count == 0) { ... } else { ... }` block, still inside the `<div style="padding-left:80px">`:

```razor
                        <MudButton Class="add-rule-action" Size="Size.Small" OnClick="@(() => OpenCreateRule(panelRow.TopicName, panelRow.SubscriptionName))">+ Add rule</MudButton>
```

Add `OpenCreateRule` to `@code`, next to `OpenCreateSubscription`:

```csharp
    private async Task OpenCreateRule(string topicName, string subscriptionName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateRuleDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.TopicName, topicName },
            { x => x.SubscriptionName, subscriptionName },
        };
        var dialog = await DialogService.ShowAsync<CreateRuleDialog>("Add rule", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await RefreshRulesAsync(topicName, subscriptionName);
        }
    }
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateRuleDialogTests"`
Expected: PASS (2 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green — this also exercises `Topics.razor`'s new `OpenCreateRule` wiring through compilation, even though no test drives it end-to-end (§Step 2 above).

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/CreateRuleDialog.razor src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateRuleDialogTests.cs
git commit -m "$(cat <<'EOF'
feat: add the Create rule dialog and wire it into the Topics page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Final `Contribution` tally and full regression pass

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`

**Interfaces:** None new — this task only updates the static summary shown on the host's Plugins page now that the two new actions (Add rule, Delete rule) exist. No new page/route was added in this plan, so `PageCount` is unchanged.

- [x] **Step 1: Update `Contribution`**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, change:

```csharp
    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter (7 -- Send is reused, not counted again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);
```

to:

```csharp
    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter, Add/Delete rule (9 -- Send is reused, not counted
    // again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 15);
```

- [x] **Step 2: Update the test**

In `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`, find the assertion on `plugin.Contribution` (in `Declares_the_expected_identity_and_connection_kind`, or wherever it lives) and update it to:

```csharp
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 15));
```

- [x] **Step 3: Run the full suite one final time**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: every test across the solution passes. Report the total count.

- [x] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
git commit -m "$(cat <<'EOF'
chore: update plugin Contribution tally for subscription filter rules

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## After this plan

Correlation filters and rule editing (delete + recreate exposed as a single UI action) are natural follow-on slices if ever needed, per `docs/superpowers/specs/2026-09-15-servicebus-filter-rules-design.md` §7. Both are explicitly out of scope here.
