# Dashboard Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Give the Dashboard (`Home.razor`) a triage-first "Needs attention" list that covers DLQ backlogs and disabled subscriptions (not just unreachable connections), plus a compact Activity panel, header summary, and per-connection namespace chips — per `docs/superpowers/specs/2026-09-14-dashboard-redesign-design.md`.

**Architecture:** A new generic `IPlugin.GetDashboardProblemsAsync` SDK method lets plugins report problems the host merges with its own unreachable-connection detection. The `azure-servicebus` plugin implements it via two new pure, directly-unit-testable builder functions (`DashboardProblems.ForDeadLetterBacklogs`/`ForDisabledSubscriptions`) fed by already-fetched Azure data plus the existing metric-history store (extended with a new `ReadAsync`). `Home.razor` gathers and merges everything into a two-column layout.

**Tech Stack:** .NET/Blazor Server, MudBlazor, EF Core (SQLite), Azure.Messaging.ServiceBus (7.20.2), xUnit + bUnit + FluentAssertions + NSubstitute.

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` must stay green after every task (`docs/design.md` §8).
- No new background service, no new EF migration — reuse the existing 60s `MetricsCollectorService` and the `IPluginStore` JSON history (spec §1, §3).
- The `azure-servicebus` plugin never gets a DI container of its own (`IPlugin` methods construct `AzureServiceBusOperations` directly) — this is a standing codebase convention, not something to fix here.
- No new CSS files/stylesheets — follow the existing convention of inline `Style="..."` and MudBlazor's CSS custom properties (e.g. `var(--mud-palette-lines-default)`), same as `Home.razor`'s current `dashboard-tile`.

---

### Task 1: SDK — `PluginDashboardProblem` and `IPlugin.GetDashboardProblemsAsync`

**Files:**
- Create: `src/SbConsole.Sdk/PluginDashboardProblem.cs`
- Modify: `src/SbConsole.Sdk/IPlugin.cs`

**Interfaces:**
- Produces: `PluginDashboardProblem(string Severity, string Title, string Detail, string? LinkHref)` and `IPlugin.GetDashboardProblemsAsync(Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) -> Task<IReadOnlyList<PluginDashboardProblem>>`, default no-op (`[]`). Every later task in this plan consumes these exact names/types.

There is no dedicated `SbConsole.Sdk` test project (the existing `PluginDashboardMetric`/`PluginResourceMetric` records have no direct tests either — they're exercised indirectly through consumers), so this task has no test step of its own; correctness is verified by the build and by the consuming tests in later tasks.

- [x] **Step 1: Add the `PluginDashboardProblem` record**

```csharp
// src/SbConsole.Sdk/PluginDashboardProblem.cs
namespace SbConsole.Sdk;

/// <summary>
/// One problem a plugin wants surfaced on the host Dashboard's "Needs attention" section, for one
/// connection. The host calls IPlugin.GetDashboardProblemsAsync once per connection and merges the
/// results with its own host-level problems (e.g. an unreachable connection) into one list, sorted
/// Errors before Warnings.
/// </summary>
public sealed record PluginDashboardProblem(
    string Severity,   // "Error" | "Warning"
    string Title,      // e.g. "payments-dlq", or "notify-fanout / sms"
    string Detail,     // e.g. "214 dead-lettered, +38 in the last hour" -- the host prefixes the
                        // connection name when rendering, so Detail itself never repeats it
    string? LinkHref); // e.g. "/p/azure-servicebus/dead-letter" -- rendered as a link when present
```

- [x] **Step 2: Add the default `GetDashboardProblemsAsync` method to `IPlugin`**

In `src/SbConsole.Sdk/IPlugin.cs`, add after the existing `GetResourceMetricsAsync` default method (before the closing `}` of the interface):

```csharp
    /// <summary>
    /// Optional Dashboard "Needs attention" problems for one connection of this plugin's
    /// ConnectionKind (e.g. a dead-letter backlog, a disabled subscription). The host calls this
    /// once per connection and merges the results with its own host-level problems (e.g. an
    /// unreachable connection) into one Needs-attention list. connectionId and store are needed
    /// because a plugin may want to read its own per-connection metric history (see
    /// MetricHistoryStore) to compute a problem like "growing" -- something GetDashboardMetricsAsync
    /// has no way to express. Returning an empty list means "nothing to report" -- the default
    /// implementation does exactly that, so a plugin written before this method existed needs no
    /// change at all.
    /// </summary>
    Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardProblem>>([]);
```

- [x] **Step 3: Build the solution**

Run: `dotnet build -warnaserror`
Expected: Build succeeds (0 errors, 0 warnings). No project implements `IPlugin` without relying on defaults yet, so nothing else needs to change.

- [x] **Step 4: Commit**

```bash
git add src/SbConsole.Sdk/PluginDashboardProblem.cs src/SbConsole.Sdk/IPlugin.cs
git commit -m "feat: add IPlugin.GetDashboardProblemsAsync for Dashboard problem reporting

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `MetricHistoryStore.ReadAsync`, and point `Queues.razor` at it

**Files:**
- Modify: `src/SbConsole.Sdk/MetricHistoryStore.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor`
- Create: `tests/SbConsole.Web.Tests/MetricHistoryStoreTests.cs`

**Interfaces:**
- Consumes: `IPluginStore.GetAsync(string key, CancellationToken)` (existing), `MetricHistoryKey.For(Guid, string)` (existing), `MetricSnapshotPoint` (existing).
- Produces: `MetricHistoryStore.ReadAsync(IPluginStore store, Guid connectionId, string resourceName, CancellationToken ct = default) -> Task<IReadOnlyList<MetricSnapshotPoint>>`. Task 5 (ServiceBusPlugin wiring) consumes this exact signature.

- [x] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/MetricHistoryStoreTests.cs
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using SbConsole.Sdk;

namespace SbConsole.Web.Tests;

public class MetricHistoryStoreTests
{
    private readonly IPluginStore _store = Substitute.For<IPluginStore>();
    private readonly Guid _connectionId = Guid.NewGuid();

    [Fact]
    public async Task Returns_an_empty_list_when_no_history_exists()
    {
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var points = await MetricHistoryStore.ReadAsync(_store, _connectionId, "orders-inbound");

        points.Should().BeEmpty();
    }

    [Fact]
    public async Task Round_trips_points_written_by_AppendAsync()
    {
        var point = new MetricSnapshotPoint(DateTimeOffset.Parse("2026-09-14T12:00:00Z"), 12, 3);
        await MetricHistoryStore.AppendAsync(_store, _connectionId, "orders-inbound", point);
        var written = (string)Capture();
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns(written);

        var points = await MetricHistoryStore.ReadAsync(_store, _connectionId, "orders-inbound");

        points.Should().ContainSingle().Which.Should().Be(point);

        object Capture()
        {
            var call = _store.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "SetAsync");
            return call.GetArguments()[1]!;
        }
    }
}
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~MetricHistoryStoreTests`
Expected: FAIL to compile — `MetricHistoryStore.ReadAsync` does not exist yet.

- [x] **Step 3: Add `ReadAsync` to `MetricHistoryStore`**

In `src/SbConsole.Sdk/MetricHistoryStore.cs`, add after `AppendAsync`:

```csharp
    /// <summary>
    /// Reads a resource's metric history back (oldest first), or an empty list if nothing has been
    /// recorded for it yet. Shared by a plugin's own UI (e.g. Queues.razor's sparklines) and any
    /// plugin logic that needs to reason about a resource's history (e.g. a Dashboard problem
    /// computing a delta over the last hour).
    /// </summary>
    public static async Task<IReadOnlyList<MetricSnapshotPoint>> ReadAsync(
        IPluginStore store, Guid connectionId, string resourceName, CancellationToken ct = default)
    {
        var json = await store.GetAsync(MetricHistoryKey.For(connectionId, resourceName), ct);
        return json is null ? [] : JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(json) ?? [];
    }
```

- [x] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~MetricHistoryStoreTests`
Expected: PASS (2 tests).

- [x] **Step 5: Point `Queues.razor` at the new helper instead of its own inline deserialize**

In `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor`, replace the `LoadHistoryAsync` method:

```csharp
    private async Task LoadHistoryAsync()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        foreach (var queue in _queues)
        {
            var json = await Store.GetAsync(MetricHistoryKey.For(_selectedConnectionId, queue.Name));
            history[queue.Name] = json is null ? [] : JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(json) ?? [];
        }

        _history = history;
    }
```

with:

```csharp
    private async Task LoadHistoryAsync()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        foreach (var queue in _queues)
        {
            history[queue.Name] = await MetricHistoryStore.ReadAsync(Store, _selectedConnectionId, queue.Name);
        }

        _history = history;
    }
```

Then remove the now-unused `@using System.Text.Json` line near the top of the same file (line 2) — `JsonSerializer` was only used in the method just replaced.

- [x] **Step 6: Run the full test suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green, including the existing Queues sparkline tests (data source unchanged, just how it's read).

- [x] **Step 7: Commit**

```bash
git add src/SbConsole.Sdk/MetricHistoryStore.cs src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor tests/SbConsole.Web.Tests/MetricHistoryStoreTests.cs
git commit -m "feat: add MetricHistoryStore.ReadAsync and reuse it from Queues.razor

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: `SubscriptionSummary.Status` and the admin-client merge

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Produces: `SubscriptionSummary(string Name, long ActiveMessageCount, long DeadLetterMessageCount, long TotalMessageCount, string Status)`. Task 4's `DashboardProblems.ForDisabledSubscriptions` consumes `.Name`/`.Status`.

This method talks to the real Azure SDK and, like `TestConnectionAsync`/`GetDashboardMetricsAsync` elsewhere in this same file, has no dedicated unit test — it "can't be meaningfully unit-tested without a real or emulated broker" (the file's own header comment). Verified by build plus the manual check in Task 8.

- [x] **Step 1: Add `Status` to `SubscriptionSummary`**

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record SubscriptionSummary(
    string Name,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long TotalMessageCount,
    string Status); // "Active" | "Disabled" | "SendDisabled" | "ReceiveDisabled"
```

- [x] **Step 2: Merge subscription config status into `ListSubscriptionsAsync`**

In `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`, replace:

```csharp
    public async Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string connectionString, string topicName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var subscriptions = new List<SubscriptionSummary>();
        await foreach (var props in adminClient.GetSubscriptionsRuntimePropertiesAsync(topicName, ct).WithCancellation(ct))
        {
            subscriptions.Add(new SubscriptionSummary(props.SubscriptionName, props.ActiveMessageCount, props.DeadLetterMessageCount, props.TotalMessageCount));
        }

        return subscriptions;
    }
```

with:

```csharp
    public async Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string connectionString, string topicName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());

        // Status lives on the config properties (GetSubscriptionsAsync), not the runtime
        // properties (GetSubscriptionsRuntimePropertiesAsync) fetched below -- two separate calls,
        // merged here by name so ListSubscriptionsAsync stays the one place callers need to know.
        var statusByName = new Dictionary<string, string>();
        await foreach (var props in adminClient.GetSubscriptionsAsync(topicName, ct).WithCancellation(ct))
        {
            statusByName[props.SubscriptionName] = props.Status.ToString();
        }

        var subscriptions = new List<SubscriptionSummary>();
        await foreach (var props in adminClient.GetSubscriptionsRuntimePropertiesAsync(topicName, ct).WithCancellation(ct))
        {
            var status = statusByName.GetValueOrDefault(props.SubscriptionName, "Active");
            subscriptions.Add(new SubscriptionSummary(props.SubscriptionName, props.ActiveMessageCount, props.DeadLetterMessageCount, props.TotalMessageCount, status));
        }

        return subscriptions;
    }
```

- [x] **Step 3: Build**

Run: `dotnet build -warnaserror`
Expected: Succeeds. `grep -rn "new SubscriptionSummary(" src tests` confirms this is the only construction site, so no other file needs updating.

- [x] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs
git commit -m "feat: report subscription config status from ListSubscriptionsAsync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Pure dashboard-problem builders (DLQ backlog + disabled subscriptions)

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/DashboardProblems.cs`
- Create: `tests/SbConsole.Plugins.ServiceBus.Tests/DashboardProblemsTests.cs`

**Interfaces:**
- Consumes: `QueueSummary` (existing), `SubscriptionSummary` (Task 3), `MetricSnapshotPoint` (existing), `PluginDashboardProblem` (Task 1).
- Produces: `DashboardProblems.ForDeadLetterBacklogs(IReadOnlyList<QueueSummary> queues, IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByQueue, DateTimeOffset now) -> IReadOnlyList<PluginDashboardProblem>` and `DashboardProblems.ForDisabledSubscriptions(string topicName, IReadOnlyList<SubscriptionSummary> subscriptions) -> IReadOnlyList<PluginDashboardProblem>`. Task 5 consumes both exact signatures.

These are the actual test leverage for the Dashboard-problem logic (per spec §4/§5): pure, synchronous, fed with already-fetched data, no Azure SDK / live broker involved — unlike everything else in this plugin.

- [x] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/DashboardProblemsTests.cs
using FluentAssertions;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests;

public class DashboardProblemsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T09:41:00Z");

    [Fact]
    public void ForDeadLetterBacklogs_ignores_queues_with_no_dead_letters()
    {
        var queues = new[] { new QueueSummary("orders-inbound", 10, 0, 0, 0) };

        var problems = DashboardProblems.ForDeadLetterBacklogs(
            queues, new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>(), Now);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ForDeadLetterBacklogs_reports_the_current_count_with_no_delta_when_there_is_no_hour_old_history()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 214, 0, 0) };

        var problems = DashboardProblems.ForDeadLetterBacklogs(
            queues, new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>(), Now);

        problems.Should().ContainSingle().Which.Should().Be(
            new PluginDashboardProblem("Warning", "payments-dlq", "214 dead-lettered", "/p/azure-servicebus/dead-letter"));
    }

    [Fact]
    public void ForDeadLetterBacklogs_appends_the_growth_delta_when_an_hour_old_point_exists()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 214, 0, 0) };
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["payments-dlq"] =
            [
                new MetricSnapshotPoint(Now.AddHours(-2), 0, 100),
                new MetricSnapshotPoint(Now.AddHours(-1), 0, 176), // closest point at/before the 1h cutoff
                new MetricSnapshotPoint(Now.AddMinutes(-10), 0, 210), // too recent to count as the baseline
            ],
        };

        var problems = DashboardProblems.ForDeadLetterBacklogs(queues, history, Now);

        problems.Should().ContainSingle().Which.Detail.Should().Be("214 dead-lettered, +38 in the last hour");
    }

    [Fact]
    public void ForDeadLetterBacklogs_omits_the_delta_when_the_backlog_shrank_or_held_steady()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 50, 0, 0) };
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["payments-dlq"] = [new MetricSnapshotPoint(Now.AddHours(-1), 0, 80)],
        };

        var problems = DashboardProblems.ForDeadLetterBacklogs(queues, history, Now);

        problems.Should().ContainSingle().Which.Detail.Should().Be("50 dead-lettered");
    }

    [Fact]
    public void ForDisabledSubscriptions_ignores_active_subscriptions()
    {
        var subscriptions = new[] { new SubscriptionSummary("sms", 0, 0, 0, "Active") };

        var problems = DashboardProblems.ForDisabledSubscriptions("notify-fanout", subscriptions);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ForDisabledSubscriptions_reports_every_non_active_subscription()
    {
        var subscriptions = new[]
        {
            new SubscriptionSummary("sms", 0, 0, 0, "Disabled"),
            new SubscriptionSummary("email", 0, 0, 0, "Active"),
            new SubscriptionSummary("push", 0, 0, 0, "ReceiveDisabled"),
        };

        var problems = DashboardProblems.ForDisabledSubscriptions("notify-fanout", subscriptions);

        problems.Should().BeEquivalentTo(
        [
            new PluginDashboardProblem("Warning", "notify-fanout / sms", "subscription disabled", "/p/azure-servicebus/topics"),
            new PluginDashboardProblem("Warning", "notify-fanout / push", "subscription receivedisabled", "/p/azure-servicebus/topics"),
        ]);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter FullyQualifiedName~DashboardProblemsTests`
Expected: FAIL to compile — `DashboardProblems` does not exist yet.

- [x] **Step 3: Implement `DashboardProblems`**

```csharp
// src/SbConsole.Plugins.ServiceBus/DashboardProblems.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

/// <summary>
/// Turns already-fetched Service Bus data into Dashboard "Needs attention" problems. Pure and
/// synchronous on purpose: ServiceBusPlugin's own IPlugin methods construct AzureServiceBusOperations
/// directly with no DI seam and can't be meaningfully unit-tested without a live broker (see
/// AzureServiceBusOperations' own header comment) -- this class is the actual test leverage for the
/// dashboard-problem logic, the same way MetricHistoryStore is the test leverage for history.
/// </summary>
internal static class DashboardProblems
{
    private static readonly TimeSpan DeltaWindow = TimeSpan.FromHours(1);
    private const string DeadLetterNavHref = "/p/azure-servicebus/dead-letter";
    private const string TopicsNavHref = "/p/azure-servicebus/topics";

    public static IReadOnlyList<PluginDashboardProblem> ForDeadLetterBacklogs(
        IReadOnlyList<QueueSummary> queues,
        IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByQueue,
        DateTimeOffset now)
    {
        var cutoff = now - DeltaWindow;
        var problems = new List<PluginDashboardProblem>();
        foreach (var queue in queues.Where(q => q.DeadLetterMessageCount > 0))
        {
            var detail = $"{queue.DeadLetterMessageCount} dead-lettered";
            if (historyByQueue.TryGetValue(queue.Name, out var history))
            {
                var baseline = history.Where(p => p.At <= cutoff).OrderByDescending(p => p.At).FirstOrDefault();
                if (baseline is not null)
                {
                    var delta = queue.DeadLetterMessageCount - baseline.DeadLetterCount;
                    if (delta > 0)
                    {
                        detail += $", +{delta} in the last hour";
                    }
                }
            }

            problems.Add(new PluginDashboardProblem("Warning", queue.Name, detail, DeadLetterNavHref));
        }

        return problems;
    }

    public static IReadOnlyList<PluginDashboardProblem> ForDisabledSubscriptions(
        string topicName, IReadOnlyList<SubscriptionSummary> subscriptions) =>
        [
            .. subscriptions
                .Where(s => s.Status != "Active")
                .Select(s => new PluginDashboardProblem(
                    "Warning", $"{topicName} / {s.Name}", $"subscription {s.Status.ToLowerInvariant()}", TopicsNavHref))
        ];
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter FullyQualifiedName~DashboardProblemsTests`
Expected: PASS (6 tests).

- [x] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/DashboardProblems.cs tests/SbConsole.Plugins.ServiceBus.Tests/DashboardProblemsTests.cs
git commit -m "feat: add pure DLQ-backlog and disabled-subscription problem builders

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: Wire `ServiceBusPlugin.GetDashboardProblemsAsync` and the Dead-lettered tile

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`

**Interfaces:**
- Consumes: `AzureServiceBusOperations.ListQueuesAsync`/`ListTopicsAsync`/`ListSubscriptionsAsync` (existing/Task 3), `MetricHistoryStore.ReadAsync` (Task 2), `DashboardProblems.ForDeadLetterBacklogs`/`ForDisabledSubscriptions` (Task 4).
- Produces: `ServiceBusPlugin` now implements `GetDashboardProblemsAsync` for real, and `GetDashboardMetricsAsync` additionally reports a `"Dead-lettered"` metric. Task 6 (`Home.razor`) consumes both.

Like `TestConnectionAsync` and the existing body of `GetDashboardMetricsAsync`, this wiring talks to the real Azure SDK and has no dedicated unit test of its own — the logic it delegates to (`DashboardProblems`, `MetricHistoryStore.ReadAsync`) is already covered in Tasks 2 and 4. Verified by build plus the manual check in Task 8.

- [x] **Step 1: Add the `"Dead-lettered"` metric to `GetDashboardMetricsAsync`**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, replace:

```csharp
    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);
        var topics = await ops.ListTopicsAsync(connectionString, ct);
        var subscriptions = topics.Sum(t => t.SubscriptionCount);
        return
        [
            new PluginDashboardMetric("Queues", queues.Count),
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Subscriptions", subscriptions),
        ];
    }
```

with:

```csharp
    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);
        var topics = await ops.ListTopicsAsync(connectionString, ct);
        var subscriptions = topics.Sum(t => t.SubscriptionCount);
        return
        [
            new PluginDashboardMetric("Queues", queues.Count),
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Subscriptions", subscriptions),
            new PluginDashboardMetric("Dead-lettered", (int)queues.Sum(q => q.DeadLetterMessageCount)),
        ];
    }
```

- [x] **Step 2: Implement `GetDashboardProblemsAsync`**

Add after `GetDashboardMetricsAsync` in the same file:

```csharp
    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);

        var historyByQueue = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        foreach (var queue in queues.Where(q => q.DeadLetterMessageCount > 0))
        {
            historyByQueue[queue.Name] = await MetricHistoryStore.ReadAsync(store, connectionId, queue.Name, ct);
        }

        var problems = new List<PluginDashboardProblem>(
            DashboardProblems.ForDeadLetterBacklogs(queues, historyByQueue, DateTimeOffset.UtcNow));

        var topics = await ops.ListTopicsAsync(connectionString, ct);
        foreach (var topic in topics)
        {
            var subscriptions = await ops.ListSubscriptionsAsync(connectionString, topic.Name, ct);
            problems.AddRange(DashboardProblems.ForDisabledSubscriptions(topic.Name, subscriptions));
        }

        return problems;
    }
```

- [x] **Step 3: Update the existing identity test's Contribution assertion if needed, and build**

`GetDashboardProblemsAsync` is a Dashboard read, not a page or a CRUD action, so `ServiceBusPluginTests.Declares_the_expected_identity_and_connection_kind`'s `PluginContribution(PageCount: 4, ActionCount: 13)` assertion is unaffected — no change needed there.

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green, including `DashboardProblemsTests` (Task 4) and `ServiceBusPluginTests`.

- [x] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs
git commit -m "feat: report DLQ-backlog and disabled-subscription problems from ServiceBusPlugin

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: `Home.razor` data layer — gather and merge plugin problems

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Home.razor`
- Modify: `tests/SbConsole.Web.Tests/HomeTests.cs`

**Interfaces:**
- Consumes: `IPlugin.GetDashboardProblemsAsync` (Task 1), `IPluginStoreFactory.For(string)` (existing).
- Produces: `Home` now holds `_pluginProblems : List<(PluginDashboardProblem Problem, ConnectionInfo Connection)>`, `_connections : List<ConnectionInfo>`, `_connectionsWithProblems : HashSet<Guid>`, and a `HeaderSummary` computed property. Task 7 (markup) consumes all four.

This task is data/merge logic only — rendered as plain-text markup for now (no two-column layout yet; that's Task 7) so it's independently testable.

- [x] **Step 1: Make the fake plugin list in `HomeTests` configurable per test**

In `tests/SbConsole.Web.Tests/HomeTests.cs`, replace the field/constructor line:

```csharp
    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(Array.Empty<IPlugin>());
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
    }
```

with:

```csharp
    private IPlugin[] _plugins = [];
    private readonly IPluginStoreFactory _pluginStoreFactory = Substitute.For<IPluginStoreFactory>();

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(_ => _plugins);
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(Substitute.For<IPluginStore>());
        Services.AddSingleton(_pluginStoreFactory);
    }
```

(`_plugins` is set by individual test methods **before** calling `Render<Home>()`; every existing test leaves it at its `[]` default, so their behavior is unchanged.)

- [x] **Step 2: Write the failing tests**

Add to `tests/SbConsole.Web.Tests/HomeTests.cs`:

```csharp
    [Fact]
    public async Task Plugin_reported_problems_render_in_Needs_attention()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Warning", "payments-dlq", "214 dead-lettered", "/p/azure-servicebus/dead-letter"),
            });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Markup.Should().Contain("payments-dlq");
        cut.Markup.Should().Contain("214 dead-lettered");
        cut.Markup.Should().Contain("sb-uk-prod");
    }

    [Fact]
    public async Task All_clear_requires_both_zero_unreachable_connections_and_zero_plugin_problems()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".dashboard-tile").Count > 0);

        cut.Markup.Should().Contain("All clear");
    }

    [Fact]
    public async Task Header_summary_reflects_connection_count_and_plugin_metrics()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric>
            {
                new("Queues", 148), new("Topics", 23), new("Subscriptions", 91), new("Dead-lettered", 312),
            });
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("148 q"));

        cut.Markup.Should().Contain("1 conn · 148 q · 23 t / 91 sub · 312 dlq");
    }
```

- [x] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~HomeTests`
Expected: FAIL — `Plugin_reported_problems_render_in_Needs_attention` and `Header_summary_reflects_connection_count_and_plugin_metrics` fail (markup doesn't contain the expected text yet); `All_clear_requires_both_zero_unreachable_connections_and_zero_plugin_problems` passes already (no behavior change needed for it, but it must still compile — it exercises the new `_plugins`/`GetDashboardProblemsAsync` stub wiring).

- [x] **Step 4: Extend `Home.razor`'s code-behind**

In `src/SbConsole.Web/Components/Pages/Home.razor`, add the injection:

```razor
@inject IPluginStoreFactory PluginStoreFactory
```

(after the existing `@inject IConnectionProvider ConnectionProvider` line).

Replace the `@code` block's fields/`LoadAsync` with:

```csharp
@code {
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private int _connectionCount;
    private List<ConnectionInfo> _connections = [];
    private List<ConnectionInfo> _unreachable = [];
    private List<(PluginDashboardProblem Problem, ConnectionInfo Connection)> _pluginProblems = [];
    private HashSet<Guid> _connectionsWithProblems = [];
    private List<KeyValuePair<string, int>> _pluginMetrics = [];
    private IReadOnlyList<AuditEntry> _recentActivity = [];
    private DateTimeOffset _refreshedAt = DateTimeOffset.UtcNow;
    private Guid? _retryingConnectionId;

    private IEnumerable<(string Label, string Value, bool Alert)> Tiles =>
    [
        ("Connections", _connectionCount.ToString(), false),
        .. _pluginMetrics.Select(m => (m.Key, m.Value.ToString(), false)),
        ("Unreachable", _unreachable.Count.ToString(), _unreachable.Count > 0),
    ];

    private string HeaderSummary =>
        $"{_connectionCount} conn · {MetricValue("Queues")} q · {MetricValue("Topics")} t / {MetricValue("Subscriptions")} sub · {MetricValue("Dead-lettered")} dlq";

    private int MetricValue(string label) => _pluginMetrics.FirstOrDefault(m => m.Key == label).Value;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var connections = await ConnectionsHandler.HandleAsync();
        _connections = [.. connections];
        _connectionCount = connections.Count;
        _unreachable = [.. connections.Where(c => c.LastTestedAt is not null && c.LastTestSucceeded == false)];

        var metrics = new Dictionary<string, int>();
        var pluginProblems = new List<(PluginDashboardProblem, ConnectionInfo)>();
        foreach (var plugin in Registry.Plugins)
        {
            var store = PluginStoreFactory.For(plugin.Id);
            foreach (var connection in connections.Where(c => c.Kind == plugin.ConnectionKind))
            {
                try
                {
                    // GetSecretAsync inside the try, not before it -- see NavMenu.RefreshBadgesAsync's
                    // identical comment: a throw here must not stop the rest of the connections/plugins
                    // in this pass.
                    var secret = await ConnectionProvider.GetSecretAsync(connection.Id);
                    if (secret is null)
                    {
                        continue;
                    }

                    foreach (var metric in await plugin.GetDashboardMetricsAsync(secret))
                    {
                        metrics[metric.Label] = metrics.GetValueOrDefault(metric.Label) + metric.Count;
                    }

                    foreach (var problem in await plugin.GetDashboardProblemsAsync(connection.Id, secret, store))
                    {
                        pluginProblems.Add((problem, connection));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Computing dashboard metrics for connection {ConnectionId} failed; skipping it.", connection.Id);
                }
            }
        }

        _pluginMetrics = [.. metrics];
        _pluginProblems = pluginProblems;
        _connectionsWithProblems = [.. _unreachable.Select(c => c.Id), .. pluginProblems.Select(p => p.Item2.Id)];

        var page = await AuditHandler.HandleAsync(new AuditQuery(Page: 1, PageSize: 10));
        _recentActivity = page.Entries;
        _refreshedAt = DateTimeOffset.UtcNow;
    }

    private async Task RetryAsync(ConnectionInfo connection)
    {
        var actor = await ActorResolver.ResolveAsync(AuthState);
        _retryingConnectionId = connection.Id;
        try
        {
            var result = await TestHandler.HandleAsync(new TestConnectionCommand(connection.Id, actor));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!.Message, Severity.Error);
            }
        }
        finally
        {
            _retryingConnectionId = null;
        }

        await LoadAsync();
    }
}
```

Then, in the markup, temporarily append the new data so the tests above can see it before Task 7 does the real layout — replace the `<MudText Typo="Typo.h4">Dashboard</MudText>` header line with:

```razor
<MudText Typo="Typo.h4">Dashboard</MudText>
<MudText Typo="Typo.caption" Class="mud-text-secondary">@HeaderSummary</MudText>
```

and change the "Needs attention" empty-state check and add the plugin-problem loop right after the existing unreachable-connections loop (still inside the same `else` block, before its closing brace):

```razor
@if (_unreachable.Count == 0 && _pluginProblems.Count == 0)
{
    <MudAlert Severity="Severity.Success" Class="mb-4">All clear — nothing needs attention.</MudAlert>
}
else
{
    @foreach (var connection in _unreachable)
    {
        <MudAlert Severity="Severity.Error" Class="mb-2">
            <b>@connection.Name</b> unreachable since @connection.LastTestedAt?.ToLocalTime() — @connection.LastTestError
            <MudButton Class="retry-connection" Style="margin-left:16px" Disabled="@(_retryingConnectionId == connection.Id)" OnClick="@(() => RetryAsync(connection))">Retry</MudButton>
            @if (_retryingConnectionId == connection.Id)
            {
                <MudProgressCircular Class="retry-connection-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
            }
            <MudLink Href="/connections" Class="ml-4">Edit connection</MudLink>
        </MudAlert>
    }
    @foreach (var item in _pluginProblems.Where(p => p.Problem.Severity == "Error"))
    {
        <MudAlert Severity="Severity.Error" Class="mb-2 plugin-problem">
            <b>@item.Problem.Title</b> — @item.Connection.Name · @item.Problem.Detail
            @if (item.Problem.LinkHref is { } href)
            {
                <MudLink Href="@href" Class="ml-4">Open →</MudLink>
            }
        </MudAlert>
    }
    @foreach (var item in _pluginProblems.Where(p => p.Problem.Severity != "Error"))
    {
        <MudAlert Severity="Severity.Warning" Class="mb-2 plugin-problem">
            <b>@item.Problem.Title</b> — @item.Connection.Name · @item.Problem.Detail
            @if (item.Problem.LinkHref is { } href)
            {
                <MudLink Href="@href" Class="ml-4">Peek →</MudLink>
            }
        </MudAlert>
    }
}
```

(This is not the final two-column layout yet — Task 7 restructures the surrounding markup. This step only needs the data to render *somewhere* so this task's tests pass.)

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~HomeTests`
Expected: PASS (all `HomeTests`, old and new).

- [x] **Step 6: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [x] **Step 7: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Home.razor tests/SbConsole.Web.Tests/HomeTests.cs
git commit -m "feat: merge plugin-reported problems into Home's Needs attention

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: `Home.razor` layout — header bar, two columns, namespace chips, Activity panel

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Home.razor`
- Modify: `tests/SbConsole.Web.Tests/HomeTests.cs`

**Interfaces:**
- Consumes: `_connections`, `_pluginProblems`, `_connectionsWithProblems`, `HeaderSummary`, `_recentActivity` (all from Task 6).

This task is presentation only — no new fields, no new `LoadAsync` behavior.

- [x] **Step 1: Write the failing tests**

Add to `tests/SbConsole.Web.Tests/HomeTests.cs` (add `using MudBlazor;` to the top of the file for the `Color` enum):

```csharp
    [Fact]
    public async Task Namespace_chip_turns_red_when_its_connection_has_an_open_problem()
    {
        Guid problemConnectionId;
        Guid cleanConnectionId;
        await using (var db = _testDb.CreateDbContext())
        {
            var problematic = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var clean = new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(problematic, clean);
            await db.SaveChangesAsync();
            problemConnectionId = problematic.Id;
            cleanConnectionId = clean.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardProblemsAsync(problemConnectionId, "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem> { new("Warning", "payments-dlq", "5 dead-lettered", null) });
        plugin.GetDashboardProblemsAsync(cleanConnectionId, "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        var chips = cut.FindComponents<MudChip<string>>();
        var problemChip = chips.Single(c => c.Markup.Contains("sb-uk-prod"));
        var cleanChip = chips.Single(c => c.Markup.Contains("sb-dev"));
        problemChip.Instance.Color.Should().Be(Color.Error);
        cleanChip.Instance.Color.Should().Be(Color.Success);
    }

    [Fact]
    public async Task Activity_panel_still_shows_real_audit_entries_and_links_to_the_audit_log()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "queue.purge",
                Target = "sb-uk-prod / payments-dlq", Risk = ActionRisk.Destructive, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("queue.purge"));

        cut.Markup.Should().Contain("queue.purge");
        cut.Markup.Should().Contain("sb-uk-prod / payments-dlq");
        cut.Find("a[href='/audit']").Should().NotBeNull();
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~HomeTests`
Expected: FAIL — `Namespace_chip_turns_red_when_its_connection_has_an_open_problem` fails (no `MudChip<string>` rendered yet). `Activity_panel_still_shows_real_audit_entries_and_links_to_the_audit_log` currently passes against the old `MudTable`-based markup — that's fine, it pins behavior this task must preserve through the rewrite.

- [x] **Step 3: Rewrite `Home.razor`'s markup into the two-column layout**

Replace the entire markup portion of `src/SbConsole.Web/Components/Pages/Home.razor` (everything from `<PageTitle>` down to the `@code` block) with:

```razor
<PageTitle>Dashboard</PageTitle>
<div class="d-flex align-center gap-4 mb-4 flex-wrap">
    <MudText Typo="Typo.h4">Dashboard</MudText>
    <MudText Typo="Typo.body2" Class="mud-text-secondary">@HeaderSummary</MudText>
    <MudSpacer />
    <MudText Typo="Typo.caption" Class="mud-text-secondary">refreshed @_refreshedAt.ToLocalTime().ToString("HH:mm:ss")</MudText>
    <MudButton Class="refresh-dashboard" OnClick="RefreshAsync">Refresh</MudButton>
</div>

<div class="d-flex flex-wrap gap-4 mb-4">
    @foreach (var tile in Tiles)
    {
        <MudPaper Class="dashboard-tile pa-4" Style="min-width:160px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
            <MudText Typo="Typo.caption" Class="mud-text-secondary">@tile.Label.ToUpperInvariant()</MudText>
            <MudText Typo="Typo.h4" Color="@(tile.Alert ? Color.Error : Color.Default)">@tile.Value</MudText>
        </MudPaper>
    }
</div>

<div class="d-flex gap-4 flex-wrap">
    <div class="flex-grow-1" style="flex-basis:60%;min-width:320px">
        <MudText Typo="Typo.h6" Class="mb-2">Needs attention</MudText>
        @if (_unreachable.Count == 0 && _pluginProblems.Count == 0)
        {
            <MudAlert Severity="Severity.Success" Class="mb-4">All clear — nothing needs attention.</MudAlert>
        }
        else
        {
            @foreach (var connection in _unreachable)
            {
                <MudAlert Severity="Severity.Error" Class="mb-2">
                    <b>@connection.Name</b> unreachable since @connection.LastTestedAt?.ToLocalTime() — @connection.LastTestError
                    <MudButton Class="retry-connection" Style="margin-left:16px" Disabled="@(_retryingConnectionId == connection.Id)" OnClick="@(() => RetryAsync(connection))">Retry</MudButton>
                    @if (_retryingConnectionId == connection.Id)
                    {
                        <MudProgressCircular Class="retry-connection-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                    }
                    <MudLink Href="/connections" Class="ml-4">Edit connection</MudLink>
                </MudAlert>
            }
            @foreach (var item in _pluginProblems.Where(p => p.Problem.Severity == "Error"))
            {
                <MudAlert Severity="Severity.Error" Class="mb-2 plugin-problem">
                    <b>@item.Problem.Title</b> — @item.Connection.Name · @item.Problem.Detail
                    @if (item.Problem.LinkHref is { } href)
                    {
                        <MudLink Href="@href" Class="ml-4">Open →</MudLink>
                    }
                </MudAlert>
            }
            @foreach (var item in _pluginProblems.Where(p => p.Problem.Severity != "Error"))
            {
                <MudAlert Severity="Severity.Warning" Class="mb-2 plugin-problem">
                    <b>@item.Problem.Title</b> — @item.Connection.Name · @item.Problem.Detail
                    @if (item.Problem.LinkHref is { } href)
                    {
                        <MudLink Href="@href" Class="ml-4">Peek →</MudLink>
                    }
                </MudAlert>
            }
        }

        <MudText Typo="Typo.overline" Class="mud-text-secondary mt-4 mb-2 d-block">Namespaces</MudText>
        <div class="d-flex flex-wrap gap-2">
            @foreach (var connection in _connections)
            {
                <MudChip T="string" Class="namespace-chip" Color="@(_connectionsWithProblems.Contains(connection.Id) ? Color.Error : Color.Success)">@connection.Name</MudChip>
            }
        </div>
    </div>

    <div style="flex-basis:35%;min-width:280px">
        <div class="d-flex align-center mb-2">
            <MudText Typo="Typo.h6">Activity</MudText>
            <MudSpacer />
            <MudLink Href="/audit">All →</MudLink>
        </div>
        @foreach (var entry in _recentActivity)
        {
            <div class="d-flex align-start gap-2 mb-2 activity-row">
                <span style="@($"display:inline-block;width:8px;height:8px;margin-top:6px;border-radius:50%;background:{ActivityDotColor(entry)}")"></span>
                <div>
                    <MudText Typo="Typo.body2"><b>@entry.Actor</b> @entry.Action</MudText>
                    <MudText Typo="Typo.caption" Class="mud-text-secondary">@entry.Target · @entry.At.ToLocalTime().ToString("HH:mm")</MudText>
                </div>
            </div>
        }
    </div>
</div>
```

Add `ActivityDotColor` to the `@code` block (after `MetricValue`):

```csharp
    private static string ActivityDotColor(AuditEntry entry) => (entry.Succeeded, entry.Risk) switch
    {
        (false, _) => "var(--mud-palette-error)",
        (true, ActionRisk.Destructive) => "var(--mud-palette-error)",
        (true, ActionRisk.Mutating) => "var(--mud-palette-warning)",
        _ => "var(--mud-palette-text-disabled)",
    };
```

(`ActionRisk` is already in scope via the existing `@using SbConsole.Core.Data.Entities`.)

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~HomeTests`
Expected: PASS (all `HomeTests`).

- [x] **Step 5: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Home.razor tests/SbConsole.Web.Tests/HomeTests.cs
git commit -m "feat: redesign Home into a two-column Needs-attention/Activity layout

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 8: Manual verification

**Files:** none (verification only).

- [x] **Step 1: Full build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, all tests green.

- [x] **Step 2: Run the app and check the Dashboard in a browser**

Run: `dotnet run --project src/SbConsole.Web`

Open the Dashboard (`/`) and confirm, per spec §9:
- Header line reads `"{conn} conn · {q} q · {t} t / {sub} sub · {dlq} dlq"` matching the tile values exactly.
- With at least one connection whose queue has dead-lettered messages: a Warning card appears in Needs attention with the queue name and `"{count} dead-lettered"`.
- After two `MetricsCollectorService` ticks (~2 minutes apart, or trigger `TickAsync` manually if testing locally) with a growing DLQ count, the card gains a `", +N in the last hour"` suffix; on the very first tick there is no suffix.
- With a topic subscription set to Disabled or Receive disabled: a Warning card appears reading `"{topic} / {subscription}"` / `"subscription disabled"` (or `"subscription receivedisabled"`).
- Namespace chips: green for connections with no open problems, red for connections that appear in Needs attention (unreachable or plugin-reported).
- Activity panel shows real recent audit entries (actor + action, target + time), with a working "All →" link to `/audit`.
- Two-column layout stacks to one column at narrow widths (resize the browser) without overlapping content.
- Zero problems anywhere ⇒ "All clear" alert and every namespace chip green.

- [x] **Step 3: Report results**

If any check fails, fix the underlying code (not the check) and re-run from Step 1. Once everything passes, the feature is complete — no commit needed for this task (verification only).

---

## Plan self-review

**Spec coverage:** §2 (SDK) → Task 1. §3 (`MetricHistoryStore.ReadAsync`) → Task 2. §4 (DLQ backlog) → Tasks 4-5. §5 (disabled subscriptions) → Tasks 3-5. §6 (Dead-lettered tile) → Task 5. §7 (Home.razor layout: header, columns, chips, Activity) → Tasks 6-7. §8/§9 (testing/manual verification) → woven through every task plus Task 8. §10 (out of scope) → nothing in this plan touches the wallboard view, extra problem kinds, a second plugin, or thresholds/snooze controls.

**Placeholder scan:** no TBD/TODO; every step has complete, exact code.

**Type consistency:** `PluginDashboardProblem(Severity, Title, Detail, LinkHref)` (Task 1) used identically in Tasks 4, 5, 6, 7. `GetDashboardProblemsAsync(Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)` (Task 1) matches the implementation in Task 5 and every call site in Task 6/tests. `DashboardProblems.ForDeadLetterBacklogs`/`ForDisabledSubscriptions` signatures match between Task 4's definition and Task 5's call sites. `SubscriptionSummary`'s new `Status` field (Task 3) matches `DashboardProblems.ForDisabledSubscriptions`'s usage (Task 4). `MetricHistoryStore.ReadAsync` signature (Task 2) matches its Task 5 call site and Queues.razor's Task 2 refactor.
