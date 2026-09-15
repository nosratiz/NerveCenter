# Ops Wallboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a separate `/wallboard` kiosk-style page — live tiles, an Active/Dead-lettered trend chart with real event markers, a dead-letter growth chart, and a per-namespace backlog breakdown — per `docs/superpowers/specs/2026-09-15-wallboard-design.md`.

**Architecture:** Everything reuses Piece A's existing plugin SDK surface (`GetDashboardMetricsAsync`, `GetResourceMetricsAsync`, `MetricHistoryStore.ReadAsync`) except one new SDK method (`GetOldestDeadLetterAsync`) for the one tile that genuinely needs a live peek. Two new pure, directly-unit-tested host-side helpers (`WallboardAggregator.BucketAndSum`/`SummarizeGrowth`) turn already-retained metric history into chart-ready series — no new background service, no new storage.

**Tech Stack:** .NET/Blazor Server, MudBlazor (including its built-in `MudChart`/`ChartType.Line` — no new charting dependency), EF Core (SQLite), Azure.Messaging.ServiceBus (7.20.2), xUnit + bUnit + FluentAssertions + NSubstitute.

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` must stay green after every task.
- No new background service, no new EF migration, no new `MetricHistoryStore` retention change (still 24h) — only `/wallboard`'s 1h/24h tabs, no 7d tab (spec §7).
- The `azure-servicebus` plugin never gets a DI container of its own — its `IPlugin` methods construct `AzureServiceBusOperations` directly, same standing convention as Piece A.
- No fabricated numbers: no separate "incoming/min"/"completed/min" (merged into one Active-trend line, spec §1), no "accelerating since HH:MM" inflection detection (replaced with a directly-computable delta+top-contributor summary, spec §4).
- No new CSS files/stylesheets — inline `Style="..."` and MudBlazor CSS custom properties only, matching the rest of the codebase.

---

### Task 1: SDK — `OldestDeadLetterEntry` and `IPlugin.GetOldestDeadLetterAsync`

**Files:**
- Create: `src/SbConsole.Sdk/OldestDeadLetterEntry.cs`
- Modify: `src/SbConsole.Sdk/IPlugin.cs`

**Interfaces:**
- Produces: `OldestDeadLetterEntry(string ResourceName, DateTimeOffset EnqueuedTime, long DeadLetterCount)` and `IPlugin.GetOldestDeadLetterAsync(Guid connectionId, string connectionString, CancellationToken ct = default) -> Task<OldestDeadLetterEntry?>`, default no-op (`null`). Task 4 (ServiceBusPlugin wiring) and Task 5 (Wallboard.razor) consume these exact names/types.

No dedicated test — same "no `SbConsole.Sdk` test project, verified by build + consumers" convention as every other SDK record/default-method addition in this codebase (`PluginDashboardProblem`, `PluginDashboardMetric`, etc.).

- [ ] **Step 1: Add the `OldestDeadLetterEntry` record**

```csharp
// src/SbConsole.Sdk/OldestDeadLetterEntry.cs
namespace SbConsole.Sdk;

/// <summary>
/// The single oldest dead-lettered message a plugin found across all of one connection's
/// resources, for the wallboard's "Oldest message" tile. Real data from a live peek -- there is
/// no honest way to approximate one message's enqueue time from aggregate counts.
/// </summary>
public sealed record OldestDeadLetterEntry(string ResourceName, DateTimeOffset EnqueuedTime, long DeadLetterCount);
```

- [ ] **Step 2: Add the default `GetOldestDeadLetterAsync` method to `IPlugin`**

In `src/SbConsole.Sdk/IPlugin.cs`, add after the existing `GetDashboardProblemsAsync` default method (before the closing `}` of the interface):

```csharp
    /// <summary>
    /// Optional single oldest dead-lettered message across all of this connection's resources, for
    /// the wallboard's "Oldest message" tile. connectionId is accepted for signature symmetry with
    /// GetDashboardProblemsAsync but unused by the reference implementation -- a peek needs no
    /// per-connection state. Returning null means "nothing dead-lettered" -- the default
    /// implementation does exactly that, so a plugin written before this method existed needs no
    /// change at all.
    /// </summary>
    Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default) =>
        Task.FromResult<OldestDeadLetterEntry?>(null);
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build -warnaserror`
Expected: Build succeeds (0 errors, 0 warnings).

- [ ] **Step 4: Commit**

```bash
git add src/SbConsole.Sdk/OldestDeadLetterEntry.cs src/SbConsole.Sdk/IPlugin.cs
git commit -m "feat: add IPlugin.GetOldestDeadLetterAsync for the wallboard's oldest-message tile

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `WallboardAggregator.BucketAndSum`

**Files:**
- Create: `src/SbConsole.Web/Wallboard/WallboardAggregator.cs`
- Create: `tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs`

**Interfaces:**
- Consumes: `MetricSnapshotPoint` (existing, `SbConsole.Sdk`).
- Produces: `WallboardAggregator.Bucket(DateTimeOffset At, long TotalActive, long TotalDeadLetter)` and `WallboardAggregator.BucketAndSum(IReadOnlyList<IReadOnlyList<MetricSnapshotPoint>> perResourceHistories, TimeSpan bucketSize, DateTimeOffset now, TimeSpan window) -> IReadOnlyList<Bucket>`. Task 5 (`Wallboard.razor`) consumes this exact signature.

Pure, synchronous, no I/O — the test leverage point for the wallboard's chart data, same role `DashboardProblems` played for Piece A.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs
using FluentAssertions;
using SbConsole.Sdk;
using SbConsole.Web.Wallboard;

namespace SbConsole.Web.Tests;

public class WallboardAggregatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

    [Fact]
    public void BucketAndSum_sums_the_most_recent_point_at_or_before_each_bucket_across_resources()
    {
        var queueA = new List<MetricSnapshotPoint>
        {
            new(Now.AddMinutes(-3), 10, 1),
            new(Now.AddMinutes(-1), 12, 1),
        };
        var queueB = new List<MetricSnapshotPoint>
        {
            new(Now.AddMinutes(-2), 5, 0),
        };

        var buckets = WallboardAggregator.BucketAndSum(
            [queueA, queueB], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(3));

        // Bucket ends: Now-2min, Now-1min, Now.
        buckets.Should().HaveCount(3);
        buckets[0].At.Should().Be(Now.AddMinutes(-2));
        buckets[0].TotalActive.Should().Be(10 + 5); // queueA's point at -3min, queueB's at -2min
        buckets[1].At.Should().Be(Now.AddMinutes(-1));
        buckets[1].TotalActive.Should().Be(12 + 5); // queueA's point at -1min, queueB still at -2min (no later point)
        buckets[2].At.Should().Be(Now);
        buckets[2].TotalActive.Should().Be(12 + 5); // no point at or before Now newer than -1min/-2min
    }

    [Fact]
    public void BucketAndSum_treats_a_resource_with_no_point_yet_as_zero()
    {
        var queueWithNoHistoryYet = new List<MetricSnapshotPoint>
        {
            new(Now.AddSeconds(-30), 7, 2), // only point is inside the last bucket
        };

        var buckets = WallboardAggregator.BucketAndSum(
            [queueWithNoHistoryYet], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(2));

        buckets.Should().HaveCount(2);
        buckets[0].TotalActive.Should().Be(0); // bucket at Now-1min: no point that early yet
        buckets[0].TotalDeadLetter.Should().Be(0);
        buckets[1].TotalActive.Should().Be(7); // bucket at Now: the -30s point qualifies
        buckets[1].TotalDeadLetter.Should().Be(2);
    }

    [Fact]
    public void BucketAndSum_returns_empty_when_given_no_resources()
    {
        var buckets = WallboardAggregator.BucketAndSum([], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(5));

        buckets.Should().HaveCount(5);
        buckets.Should().OnlyContain(b => b.TotalActive == 0 && b.TotalDeadLetter == 0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardAggregatorTests`
Expected: FAIL to compile — `WallboardAggregator` does not exist yet.

- [ ] **Step 3: Implement `WallboardAggregator.BucketAndSum`**

```csharp
// src/SbConsole.Web/Wallboard/WallboardAggregator.cs
using SbConsole.Sdk;

namespace SbConsole.Web.Wallboard;

/// <summary>
/// Turns already-retained per-resource metric history (SbConsole.Sdk.MetricHistoryStore) into
/// wallboard chart data. Pure and synchronous on purpose -- this is the test leverage point for
/// the wallboard's charts, the same role DashboardProblems plays for the Dashboard's problem list.
/// </summary>
public static class WallboardAggregator
{
    public sealed record Bucket(DateTimeOffset At, long TotalActive, long TotalDeadLetter);

    /// <summary>
    /// Produces one bucket per bucketSize step from (now - window) to now inclusive, each summing
    /// every resource's most-recent point at or before that bucket's end time (step-function
    /// interpolation -- correct for periodically-sampled point-in-time counts). A resource with no
    /// qualifying point yet (e.g. added after the window started) contributes zero to that bucket.
    /// </summary>
    public static IReadOnlyList<Bucket> BucketAndSum(
        IReadOnlyList<IReadOnlyList<MetricSnapshotPoint>> perResourceHistories,
        TimeSpan bucketSize, DateTimeOffset now, TimeSpan window)
    {
        var bucketCount = (int)(window / bucketSize);
        var buckets = new List<Bucket>(bucketCount);

        for (var i = bucketCount; i >= 1; i--)
        {
            var bucketEnd = now - (bucketSize * i);
            long totalActive = 0;
            long totalDeadLetter = 0;

            foreach (var history in perResourceHistories)
            {
                var latest = history.Where(p => p.At <= bucketEnd).OrderByDescending(p => p.At).FirstOrDefault();
                if (latest is not null)
                {
                    totalActive += latest.ActiveCount;
                    totalDeadLetter += latest.DeadLetterCount;
                }
            }

            buckets.Add(new Bucket(bucketEnd, totalActive, totalDeadLetter));
        }

        return buckets;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardAggregatorTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Web/Wallboard/WallboardAggregator.cs tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs
git commit -m "feat: add WallboardAggregator.BucketAndSum for the wallboard's trend chart

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: `WallboardAggregator.SummarizeGrowth`

**Files:**
- Modify: `src/SbConsole.Web/Wallboard/WallboardAggregator.cs`
- Modify: `tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs`

**Interfaces:**
- Produces: `WallboardAggregator.SummarizeGrowth(IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByResourceLabel, TimeSpan window, DateTimeOffset now) -> string`. Task 5 (`Wallboard.razor`) consumes this exact signature. The dictionary key is a caller-supplied display label (`Wallboard.razor` will pass `"{connectionName} / {resourceName}"` so two different connections' same-named queues don't collide) — this function treats it as an opaque display string, not a parsed identifier.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs`:

```csharp
    [Fact]
    public void SummarizeGrowth_reports_the_total_delta_and_top_contributor_when_backlog_grew()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["sb-uk-prod / payments-dlq"] =
            [
                new(Now.AddHours(-12), 0, 10),
                new(Now, 0, 48), // +38
            ],
            ["sb-uk-prod / orders-inbound"] =
            [
                new(Now.AddHours(-12), 0, 5),
                new(Now, 0, 7), // +2
            ],
        };

        var summary = WallboardAggregator.SummarizeGrowth(history, TimeSpan.FromHours(12), Now);

        summary.Should().Be("+40 in the last 12h — sb-uk-prod / payments-dlq accounts for 95% of the rise.");
    }

    [Fact]
    public void SummarizeGrowth_reports_no_change_when_the_backlog_did_not_grow()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["sb-uk-prod / payments-dlq"] =
            [
                new(Now.AddHours(-12), 0, 20),
                new(Now, 0, 15), // shrank
            ],
        };

        var summary = WallboardAggregator.SummarizeGrowth(history, TimeSpan.FromHours(12), Now);

        summary.Should().Be("No change in the last 12h.");
    }

    [Fact]
    public void SummarizeGrowth_treats_a_resource_with_no_baseline_point_as_starting_at_zero()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["sb-dev / new-queue"] = [new(Now.AddMinutes(-5), 0, 3)], // only exists near "now"
        };

        var summary = WallboardAggregator.SummarizeGrowth(history, TimeSpan.FromHours(12), Now);

        summary.Should().Be("+3 in the last 12h — sb-dev / new-queue accounts for 100% of the rise.");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardAggregatorTests`
Expected: FAIL to compile — `SummarizeGrowth` does not exist yet.

- [ ] **Step 3: Implement `SummarizeGrowth`**

Add to `src/SbConsole.Web/Wallboard/WallboardAggregator.cs`, inside the `WallboardAggregator` class:

```csharp
    /// <summary>
    /// A one-line summary of dead-letter backlog growth over the window: the total delta across
    /// every resource plus whichever single resource contributed the most of it, or an honest
    /// "no change" when the total did not grow. Deliberately does not attempt to detect *when*
    /// growth accelerated (see spec's "no fabricated numbers" constraint) -- just the delta.
    /// </summary>
    public static string SummarizeGrowth(
        IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByResourceLabel,
        TimeSpan window, DateTimeOffset now)
    {
        var windowStart = now - window;
        var deltas = new Dictionary<string, long>();

        foreach (var (label, history) in historyByResourceLabel)
        {
            var baseline = history.Where(p => p.At <= windowStart).OrderByDescending(p => p.At).FirstOrDefault();
            var latest = history.Where(p => p.At <= now).OrderByDescending(p => p.At).FirstOrDefault();
            var startCount = baseline?.DeadLetterCount ?? 0;
            var endCount = latest?.DeadLetterCount ?? 0;
            deltas[label] = endCount - startCount;
        }

        var totalDelta = deltas.Values.Sum();
        if (totalDelta <= 0)
        {
            return $"No change in the last {FormatWindow(window)}.";
        }

        var top = deltas.OrderByDescending(kv => kv.Value).First();
        var pct = (int)Math.Round(top.Value * 100.0 / totalDelta);
        return $"+{totalDelta} in the last {FormatWindow(window)} — {top.Key} accounts for {pct}% of the rise.";
    }

    private static string FormatWindow(TimeSpan window) => $"{(int)window.TotalHours}h";
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardAggregatorTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Wallboard/WallboardAggregator.cs tests/SbConsole.Web.Tests/WallboardAggregatorTests.cs
git commit -m "feat: add WallboardAggregator.SummarizeGrowth for the dead-letter growth narrative

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Wire `ServiceBusPlugin.GetOldestDeadLetterAsync`

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`

**Interfaces:**
- Consumes: `AzureServiceBusOperations.ListQueuesAsync`/`PeekMessagesAsync` (existing), `OldestDeadLetterEntry` (Task 1).
- Produces: `ServiceBusPlugin` now implements `GetOldestDeadLetterAsync` for real. Task 5 (`Wallboard.razor`) consumes it via `IPlugin`.

Like `GetDashboardProblemsAsync`/`GetDashboardMetricsAsync`, this talks to the real Azure SDK and has no dedicated unit test — verified by build plus the manual check in Task 8.

- [ ] **Step 1: Implement `GetOldestDeadLetterAsync`**

Add to `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, after `GetDashboardProblemsAsync`:

```csharp
    public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);
        var dlqQueues = queues.Where(q => q.DeadLetterMessageCount > 0).ToList();
        if (dlqQueues.Count == 0)
        {
            return null;
        }

        // One peek per DLQ-bearing queue, in parallel -- the exact fan-out shape the Dashboard
        // redesign's final review flagged for GetDashboardProblemsAsync's subscription fetch;
        // built parallel here from the start rather than serial-then-fixed.
        var peeks = await Task.WhenAll(dlqQueues.Select(q =>
            ops.PeekMessagesAsync(connectionString, q.Name, fromDeadLetter: true, maxMessages: 1, fromSequenceNumber: null, ct)));

        OldestDeadLetterEntry? oldest = null;
        for (var i = 0; i < dlqQueues.Count; i++)
        {
            var message = peeks[i].FirstOrDefault();
            if (message is null)
            {
                continue;
            }

            if (oldest is null || message.EnqueuedTime < oldest.EnqueuedTime)
            {
                oldest = new OldestDeadLetterEntry(dlqQueues[i].Name, message.EnqueuedTime, dlqQueues[i].DeadLetterMessageCount);
            }
        }

        return oldest;
    }
```

- [ ] **Step 2: Build and run the full suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green (no test count change — this method has no dedicated test).

- [ ] **Step 3: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs
git commit -m "feat: report the oldest dead-lettered message from ServiceBusPlugin

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: `Wallboard.razor` data layer

**Files:**
- Create: `src/SbConsole.Web/Components/Pages/Wallboard.razor`
- Create: `tests/SbConsole.Web.Tests/WallboardTests.cs`

**Interfaces:**
- Consumes: `ListConnectionsQueryHandler`, `IConnectionProvider`, `PluginRegistry`, `IPluginStoreFactory`, `ListAuditEntriesQueryHandler` (all existing, same as `Home.razor`); `plugin.GetDashboardMetricsAsync`/`GetResourceMetricsAsync`/`GetOldestDeadLetterAsync` (existing SDK + Task 1); `MetricHistoryStore.ReadAsync` (existing); `WallboardAggregator.BucketAndSum`/`SummarizeGrowth` (Tasks 2-3).
- Produces: `Wallboard` component with fields/properties Task 6 (layout) consumes: `_connections`, `_unreachable`, `_namespaceBacklog : List<(ConnectionInfo Connection, int DeadLetterCount)>`, `_activeCount`, `_activeDeltaLastMinute`, `_deadLetterTotal`, `_deadLetterDeltaLastHour`, `_oldestDeadLetter : OldestDeadLetterEntry?`, `_buckets1h`/`_buckets24h : IReadOnlyList<WallboardAggregator.Bucket>`, `_growthSummary : string`, `_events : List<(DateTimeOffset At, string ConnectionName)>`.

This task is data/merge logic only — rendered as plain-text markup for now (no chart/final layout yet; that's Task 6) so it's independently testable, same split Piece A used between its Task 6 and Task 7.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/WallboardTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class WallboardTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IConnectionProvider _connectionProvider = Substitute.For<IConnectionProvider>();
    private readonly IPluginStoreFactory _pluginStoreFactory = Substitute.For<IPluginStoreFactory>();
    private static readonly byte[] Key = new byte[32];
    private IPlugin[] _plugins = [];

    public WallboardTests()
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
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(Substitute.For<IPluginStore>());
        Services.AddSingleton(_pluginStoreFactory);
    }

    [Fact]
    public async Task Tiles_reflect_aggregated_metrics_across_connections()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            db.Connections.Add(new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Dead-lettered", 100) });
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("200")); // 100 + 100 across two connections

        cut.Markup.Should().Contain("200");
    }

    [Fact]
    public async Task Namespace_backlog_groups_dead_lettered_count_by_connection()
    {
        Guid uk, staging;
        await using (var db = _testDb.CreateDbContext())
        {
            var ukConn = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var stagingConn = new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(ukConn, stagingConn);
            await db.SaveChangesAsync();
            uk = ukConn.Id;
            staging = stagingConn.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([new("Dead-lettered", 214)]));
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("sb-uk-prod"));

        cut.Markup.Should().Contain("sb-uk-prod");
        cut.Markup.Should().Contain("214");
        cut.Markup.Should().Contain("sb-staging");
    }

    [Fact]
    public async Task Oldest_dead_letter_tile_shows_the_minimum_across_connections()
    {
        Guid uk, staging;
        await using (var db = _testDb.CreateDbContext())
        {
            var ukConn = new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] };
            var stagingConn = new Connection { Name = "sb-staging", Kind = "azure-servicebus", SecretCiphertext = [1] };
            db.Connections.AddRange(ukConn, stagingConn);
            await db.SaveChangesAsync();
            uk = ukConn.Id;
            staging = stagingConn.Id;
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        Services.AddSingleton(clock);
        Services.AddSingleton<TimeProvider>(clock);

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>()).Returns(new List<PluginDashboardMetric>());
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>()).Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(uk, "secret", Arg.Any<CancellationToken>())
            .Returns(new OldestDeadLetterEntry("payments-dlq", clock.GetUtcNow().AddHours(-4).AddMinutes(-12), 214));
        plugin.GetOldestDeadLetterAsync(staging, "secret", Arg.Any<CancellationToken>())
            .Returns(new OldestDeadLetterEntry("test-queue", clock.GetUtcNow().AddMinutes(-5), 1));
        _plugins = [plugin];

        var cut = Render<Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Markup.Should().Contain("payments-dlq");
        cut.Markup.Should().NotContain("test-queue");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardTests`
Expected: FAIL to compile — `Wallboard` component does not exist yet.

- [ ] **Step 3: Implement `Wallboard.razor`'s data layer**

```razor
@page "/wallboard"
@layout EmptyLayout
@using SbConsole.Core.Audit
@using SbConsole.Core.Connections
@using SbConsole.Sdk
@using SbConsole.Web.Plugins
@using SbConsole.Web.Wallboard
@inject ListConnectionsQueryHandler ConnectionsHandler
@inject ListAuditEntriesQueryHandler AuditHandler
@inject PluginRegistry Registry
@inject IConnectionProvider ConnectionProvider
@inject IPluginStoreFactory PluginStoreFactory
@inject ILogger<Wallboard> Logger
@inject TimeProvider Clock

<PageTitle>Wallboard</PageTitle>
<div>
    <p>Active: @_activeCount (@(_activeDeltaLastMinute >= 0 ? "+" : "")@_activeDeltaLastMinute)</p>
    <p>Dead-lettered: @_deadLetterTotal (@(_deadLetterDeltaLastHour >= 0 ? "+" : "")@_deadLetterDeltaLastHour / 1h)</p>
    @if (_oldestDeadLetter is { } oldest)
    {
        <p>Oldest: @FormatAge(Clock.GetUtcNow() - oldest.EnqueuedTime) — @oldest.ResourceName · @oldest.DeadLetterCount msgs</p>
    }
    <p>@_growthSummary</p>
    @foreach (var (connection, count) in _namespaceBacklog)
    {
        <p class="namespace-backlog-row">@connection.Name: @count</p>
    }
</div>

@code {
    private List<ConnectionInfo> _connections = [];
    private List<ConnectionInfo> _unreachable = [];
    private List<(ConnectionInfo Connection, int DeadLetterCount)> _namespaceBacklog = [];
    private long _activeCount;
    private long _activeDeltaLastMinute;
    private long _deadLetterTotal;
    private long _deadLetterDeltaLastHour;
    private OldestDeadLetterEntry? _oldestDeadLetter;
    private IReadOnlyList<WallboardAggregator.Bucket> _buckets1h = [];
    private IReadOnlyList<WallboardAggregator.Bucket> _buckets24h = [];
    private string _growthSummary = "";
    private List<(DateTimeOffset At, string ConnectionName)> _events = [];

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m" : $"{age.Minutes}m";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var connections = await ConnectionsHandler.HandleAsync();
        _connections = [.. connections];
        _unreachable = [.. connections.Where(c => c.LastTestedAt is not null && c.LastTestSucceeded == false)];

        var namespaceBacklog = new List<(ConnectionInfo, int)>();
        var allHistories = new List<IReadOnlyList<MetricSnapshotPoint>>();
        var historyByLabel = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        OldestDeadLetterEntry? oldest = null;

        foreach (var plugin in Registry.Plugins)
        {
            var store = PluginStoreFactory.For(plugin.Id);
            foreach (var connection in connections.Where(c => c.Kind == plugin.ConnectionKind))
            {
                try
                {
                    var secret = await ConnectionProvider.GetSecretAsync(connection.Id);
                    if (secret is null)
                    {
                        continue;
                    }

                    var deadLettered = 0;
                    foreach (var metric in await plugin.GetDashboardMetricsAsync(secret))
                    {
                        if (metric.Label == "Dead-lettered")
                        {
                            deadLettered += metric.Count;
                        }
                    }

                    if (deadLettered > 0)
                    {
                        namespaceBacklog.Add((connection, deadLettered));
                    }

                    var resources = await plugin.GetResourceMetricsAsync(secret);
                    var histories = await Task.WhenAll(resources.Select(async r =>
                        (Resource: r, History: await MetricHistoryStore.ReadAsync(store, connection.Id, r.ResourceName))));

                    foreach (var (resource, history) in histories)
                    {
                        allHistories.Add(history);
                        historyByLabel[$"{connection.Name} / {resource.ResourceName}"] = history;
                    }

                    var connectionOldest = await plugin.GetOldestDeadLetterAsync(connection.Id, secret);
                    if (connectionOldest is not null && (oldest is null || connectionOldest.EnqueuedTime < oldest.EnqueuedTime))
                    {
                        oldest = connectionOldest;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Computing wallboard data for connection {ConnectionId} failed; it may be incomplete or missing.", connection.Id);
                }
            }
        }

        _namespaceBacklog = [.. namespaceBacklog.OrderByDescending(n => n.Item2)];
        _oldestDeadLetter = oldest;

        var now = Clock.GetUtcNow();
        _buckets1h = WallboardAggregator.BucketAndSum(allHistories, TimeSpan.FromMinutes(1), now, TimeSpan.FromHours(1));
        _buckets24h = WallboardAggregator.BucketAndSum(allHistories, TimeSpan.FromMinutes(5), now, TimeSpan.FromHours(24));
        _growthSummary = WallboardAggregator.SummarizeGrowth(historyByLabel, TimeSpan.FromHours(12), now);

        _activeCount = _buckets1h.Count > 0 ? _buckets1h[^1].TotalActive : 0;
        _activeDeltaLastMinute = _buckets1h.Count >= 2 ? _buckets1h[^1].TotalActive - _buckets1h[^2].TotalActive : 0;
        _deadLetterTotal = _namespaceBacklog.Sum(n => (long)n.DeadLetterCount);
        var hourAgoBucket = _buckets24h.Count > 0 ? _buckets24h[0] : null;
        _deadLetterDeltaLastHour = hourAgoBucket is not null ? _deadLetterTotal - hourAgoBucket.TotalDeadLetter : 0;

        var windowStart = now - TimeSpan.FromHours(24);
        var auditPage = await AuditHandler.HandleAsync(new AuditQuery(From: windowStart, To: now, PageSize: 200));
        _events =
        [
            .. auditPage.Entries
                .Where(e => e.Action == "connection.test" && !e.Succeeded)
                .Select(e => (e.At, ConnectionName: e.Target)),
        ];
    }
}
```

**Note:** `_buckets24h[0]` as the "1 hour ago" baseline is an approximation at 5-minute
bucket width (off by up to 5 minutes) — acceptable for a tile annotation, not claimed as
exact. `EmptyLayout` is the existing minimal layout (`ThemedRoot` + `@Body`, already used
by Login) — no new layout component.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Wallboard.razor tests/SbConsole.Web.Tests/WallboardTests.cs
git commit -m "feat: add the wallboard page's data layer

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: `Wallboard.razor` layout — tiles, charts, namespace backlog, 1h/24h toggle

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Wallboard.razor`
- Modify: `tests/SbConsole.Web.Tests/WallboardTests.cs`

**Interfaces:**
- Consumes: every field/property Task 5 produces.

This task is presentation only — no new fields beyond a `_selectedRange` UI-state string, no new `LoadAsync` behavior.

**A note on risk before you start:** this task uses MudBlazor's built-in `MudChart`
component (`ChartType.Line`, `MudBlazor.ChartSeries<double>`, `ChartLabels`) to draw the
trend and growth charts — confirmed to exist in the installed MudBlazor 9.9.0 via its
XML documentation, but not verified against a compiled example in this codebase (no
chart existed anywhere in this app before this task). If `<MudChart T="double" ... />`
doesn't compile exactly as shown below, check the installed package's actual component
signature (IDE go-to-definition on `MudChart`/`ChartSeries`/`ChartOptions`) rather than
guessing further — and report back with what you found if it differs, rather than
silently reshaping the data layer to fit a guess.

- [ ] **Step 1: Write the failing tests**

Add to `tests/SbConsole.Web.Tests/WallboardTests.cs`:

```csharp
    [Fact]
    public async Task Range_toggle_switches_between_the_1h_and_24h_bucket_sets()
    {
        var cut = Render<Wallboard>();
        cut.WaitForState(() => cut.FindAll(".range-toggle-1h").Count > 0);

        cut.Find(".range-toggle-24h").Click();
        cut.WaitForAssertion(() => cut.Find(".range-toggle-24h").ClassList.Should().Contain("range-toggle-active"));

        cut.Find(".range-toggle-1h").Click();
        cut.WaitForAssertion(() => cut.Find(".range-toggle-1h").ClassList.Should().Contain("range-toggle-active"));
    }

    [Fact]
    public async Task Unreachable_connection_shows_a_Fix_link_in_the_namespace_backlog()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Name = "sb-eu-prod", Kind = "azure-servicebus", SecretCiphertext = [1],
                LastTestedAt = DateTimeOffset.UtcNow, LastTestSucceeded = false, LastTestError = "Unauthorized (401)",
            });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _plugins = [];

        var cut = Render<Wallboard>();
        cut.WaitForState(() => cut.Markup.Contains("sb-eu-prod"));

        cut.Find("a.namespace-fix-link").GetAttribute("href").Should().Be("/connections");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardTests`
Expected: FAIL — no `.range-toggle-1h`/`.range-toggle-24h`/`a.namespace-fix-link` elements exist yet.

- [ ] **Step 3: Replace `Wallboard.razor`'s markup with the full layout**

Replace everything from `<PageTitle>` to the `@code` block with:

```razor
<PageTitle>Wallboard</PageTitle>
<div class="pa-6">
    <div class="d-flex align-center gap-4 mb-4">
        <MudText Typo="Typo.h4">Wallboard</MudText>
        <MudSpacer />
        <MudButton Class="@($"range-toggle-1h{(_selectedRange == "1h" ? " range-toggle-active" : "")}")" OnClick='@(() => _selectedRange = "1h")'>1h</MudButton>
        <MudButton Class="@($"range-toggle-24h{(_selectedRange == "24h" ? " range-toggle-active" : "")}")" OnClick='@(() => _selectedRange = "24h")'>24h</MudButton>
    </div>

    <div class="d-flex flex-wrap gap-4 mb-4">
        <MudPaper Class="pa-4" Style="min-width:180px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
            <MudText Typo="Typo.caption" Class="mud-text-secondary">ACTIVE</MudText>
            <MudText Typo="Typo.h4">@_activeCount</MudText>
            <MudText Typo="Typo.caption" Class="mud-text-secondary">@(_activeDeltaLastMinute >= 0 ? "+" : "")@_activeDeltaLastMinute / min</MudText>
        </MudPaper>
        <MudPaper Class="pa-4" Style="@($"min-width:180px;border:1px solid {(_deadLetterTotal > 0 ? "var(--mud-palette-error)" : "var(--mud-palette-lines-default)")}")" Elevation="0">
            <MudText Typo="Typo.caption" Class="mud-text-secondary">DEAD-LETTERED</MudText>
            <MudText Typo="Typo.h4" Color="@(_deadLetterTotal > 0 ? Color.Error : Color.Default)">@_deadLetterTotal</MudText>
            <MudText Typo="Typo.caption" Class="mud-text-secondary">@(_deadLetterDeltaLastHour >= 0 ? "+" : "")@_deadLetterDeltaLastHour / 1h</MudText>
        </MudPaper>
        <MudPaper Class="pa-4" Style="min-width:180px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
            <MudText Typo="Typo.caption" Class="mud-text-secondary">OLDEST MESSAGE</MudText>
            @if (_oldestDeadLetter is { } oldest)
            {
                <MudText Typo="Typo.h4">@FormatAge(Clock.GetUtcNow() - oldest.EnqueuedTime)</MudText>
                <MudText Typo="Typo.caption" Class="mud-text-secondary">@oldest.ResourceName · @oldest.DeadLetterCount msgs</MudText>
            }
            else
            {
                <MudText Typo="Typo.h4">—</MudText>
            }
        </MudPaper>
    </div>

    <MudPaper Class="pa-4 mb-4" Style="border:1px solid var(--mud-palette-lines-default)" Elevation="0">
        <MudText Typo="Typo.h6" Class="mb-2">Throughput</MudText>
        <MudChart T="double" ChartType="ChartType.Line" ChartSeries="@TrendSeries" ChartLabels="@TrendLabels" Width="100%" Height="260px" />
    </MudPaper>

    <div class="d-flex gap-4 flex-wrap">
        <MudPaper Class="pa-4 flex-grow-1" Style="min-width:320px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
            <MudText Typo="Typo.h6" Class="mb-2">Dead-letter growth</MudText>
            <MudChart T="double" ChartType="ChartType.Bar" ChartSeries="@GrowthSeries" ChartLabels="@GrowthLabels" Width="100%" Height="200px" />
            <MudText Typo="Typo.body2" Class="mt-2">@_growthSummary</MudText>
        </MudPaper>

        <MudPaper Class="pa-4" Style="flex-basis:320px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
            <MudText Typo="Typo.h6" Class="mb-2">Backlog by namespace</MudText>
            @foreach (var (connection, count) in _namespaceBacklog)
            {
                <div class="d-flex align-center justify-space-between mb-1 namespace-backlog-row">
                    <MudText Typo="Typo.body2">@connection.Name</MudText>
                    <div class="d-flex align-center gap-2">
                        <MudText Typo="Typo.body2">@count</MudText>
                        @if (_unreachable.Any(u => u.Id == connection.Id))
                        {
                            <MudLink Href="/connections" Class="namespace-fix-link">Fix →</MudLink>
                        }
                    </div>
                </div>
            }
        </MudPaper>
    </div>
</div>

@code {
```

Then, inside the existing `@code` block, add (after the existing field declarations, before `FormatAge`):

```csharp
    private string _selectedRange = "1h";

    private IReadOnlyList<WallboardAggregator.Bucket> SelectedBuckets => _selectedRange == "24h" ? _buckets24h : _buckets1h;

    private List<ChartSeries<double>> TrendSeries =>
    [
        new() { Name = "Active", Data = [.. SelectedBuckets.Select(b => (double)b.TotalActive)] },
        new() { Name = "Dead-lettered", Data = [.. SelectedBuckets.Select(b => (double)b.TotalDeadLetter)] },
    ];

    private string[] TrendLabels => [.. SelectedBuckets.Select(b => b.At.ToLocalTime().ToString("HH:mm"))];

    private List<ChartSeries<double>> GrowthSeries =>
    [
        new() { Name = "Dead-lettered", Data = [.. _buckets24h.TakeLast(12).Select(b => (double)b.TotalDeadLetter)] },
    ];

    private string[] GrowthLabels => [.. _buckets24h.TakeLast(12).Select(b => b.At.ToLocalTime().ToString("HH:mm"))];
```

Remove the old placeholder `@code` markup block's plain `<div>...</div>` (the one with
`<p>` tags from Task 5) — it's fully superseded by the layout above.

Add `@using MudBlazor.Charts` near the top of the file (alongside the existing `@using`
lines) if `ChartSeries<T>`/`ChartType` don't resolve without it — check by building
first; MudBlazor's top-level namespace may already export these.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~WallboardTests`
Expected: PASS (all `WallboardTests`, old and new).

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Wallboard.razor tests/SbConsole.Web.Tests/WallboardTests.cs
git commit -m "feat: build out the wallboard's tiles, charts, and namespace backlog layout

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: Link to the wallboard from `Home.razor`

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Home.razor`
- Modify: `tests/SbConsole.Web.Tests/HomeTests.cs`

**Interfaces:** none new — pure markup addition.

- [ ] **Step 1: Write the failing test**

Add to `tests/SbConsole.Web.Tests/HomeTests.cs`:

```csharp
    [Fact]
    public void Header_links_to_the_wallboard()
    {
        var cut = Render<Home>();

        cut.Find("a.open-wallboard").GetAttribute("href").Should().Be("/wallboard");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Header_links_to_the_wallboard`
Expected: FAIL — no `a.open-wallboard` element exists yet.

- [ ] **Step 3: Add the link**

In `src/SbConsole.Web/Components/Pages/Home.razor`, in the header `<div>` (the one containing the "Dashboard" title, `HeaderSummary`, and the Refresh button), add a link before the Refresh `MudButton`:

```razor
<MudLink Href="/wallboard" Class="open-wallboard">Wallboard →</MudLink>
```

placed immediately after the `<MudSpacer />` line and before the "refreshed" caption `MudText`, so it reads left-to-right as: title, summary, spacer, wallboard link, refreshed-at caption, refresh button.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Header_links_to_the_wallboard`
Expected: PASS.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: All green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Home.razor tests/SbConsole.Web.Tests/HomeTests.cs
git commit -m "feat: link to the wallboard from the Dashboard header

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 8: Manual verification

**Files:** none (verification only).

- [ ] **Step 1: Full build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, all tests green.

- [ ] **Step 2: Run the app and check `/wallboard` in a browser**

Run: `dotnet run --project src/SbConsole.Web` (with the required `SBC_*` bootstrap
environment variables — see `BootstrapOptions.FromEnvironment`).

Confirm, per spec §9 and this plan's constraints:
- Clicking "Wallboard →" on the Dashboard opens `/wallboard` with no sidebar chrome
  (full-bleed `EmptyLayout`).
- With zero connections: tiles show `0`/`—` honestly, no crash, no fabricated data.
- With a connection whose queues have dead-lettered messages: the "Dead-lettered" tile,
  the trend chart's red line, the growth chart, and the namespace backlog list all show
  consistent, matching numbers.
- The "Oldest message" tile shows a real duration and resource name when a DLQ-bearing
  queue exists, `—` otherwise — never a guessed value.
- The 1h/24h toggle changes the trend chart's bucket width/window without changing the
  tile values (per spec §6, tiles are tab-independent).
- With an unreachable connection: its namespace-backlog row shows a working "Fix →" link
  to `/connections`.
- No literal "Incoming/min" or "Completed/min" label appears anywhere — only "Active".

- [ ] **Step 3: Report results**

If any check fails, fix the underlying code (not the check) and re-run from Step 1. Once
everything passes, the feature is complete — no commit needed for this task (verification
only).

---

## Plan self-review

**Spec coverage:** §1 (Active-trend merge, no fabricated incoming/completed) → Tasks 2, 5, 6. §2 (`GetOldestDeadLetterAsync`) → Tasks 1, 4. §3 (throughput chart aggregation) → Tasks 2, 5, 6. §4 (dead-letter growth + honest narrative) → Tasks 3, 5, 6. §5 (backlog by namespace) → Tasks 5, 6. §6 (tiles) → Tasks 5, 6. §7 (route/layout/1h-24h-no-7d) → Tasks 5, 6, 8. §8 (testing) → woven through every task plus Task 8. §9 (out of scope) → nothing in this plan attempts Azure Monitor integration, a 7d tab, inflection-point detection, or subscription-level DLQ data.

**Placeholder scan:** no TBD/TODO. Task 6's MudChart risk note is a documented, bounded uncertainty (with an explicit fallback instruction: check the actual component, report back, don't guess further) rather than an unresolved placeholder — the data layer it renders (Task 5) is fully exact and independently tested regardless of how the chart markup resolves.

**Type consistency:** `OldestDeadLetterEntry(ResourceName, EnqueuedTime, DeadLetterCount)` (Task 1) used identically in Task 4's implementation and Task 5/6's consumption. `WallboardAggregator.Bucket(At, TotalActive, TotalDeadLetter)` and `BucketAndSum`/`SummarizeGrowth` signatures (Tasks 2-3) match their Task 5/6 call sites exactly. `IPlugin.GetOldestDeadLetterAsync(Guid, string, CancellationToken)` signature consistent between Task 1's interface addition, Task 4's implementation, and Task 5's call site.
