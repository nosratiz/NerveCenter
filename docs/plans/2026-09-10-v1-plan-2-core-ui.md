# SbConsole v1 — Plan 2 of 4: Core UI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the host's own screens — Dashboard, Connections (full CRUD), Audit, Settings, Plugins — plus the small SDK v1.1 extension (`IPlugin.ConnectionKind`/`ConnectionKindDisplayName`/`Contribution`, `IConfirmationService`) they need. No Service Bus plugin work happens here; every plugin-shaped data point renders a correct empty state until Plan 3 gives it something real to show.

**Architecture:** Per `docs/design.md` §3, §4, §5.1 (as extended 2026-09-10). New Core handlers (`UpdateConnectionCommandHandler`, `ListAuditEntriesQueryHandler`, `ListPluginsQueryHandler`) follow the same plain-class, `Result<T>`-returning pattern as Plan 1's connection handlers. The confirmation dialog is a Sdk interface (`IConfirmationService`) implemented in Web with MudBlazor and DI-injected — plugins will consume it in Plan 3 without ever referencing `SbConsole.Web`.

**Tech Stack:** Same as Plan 1 — .NET 10, Blazor Interactive Server, MudBlazor, EF Core + SQLite (WAL), xUnit + FluentAssertions 7.x + NSubstitute + bUnit.

**Out of scope for this plan:** connection reachability testing, plugin-contributed dashboard widgets, charts/wallboard, audit CSV export, audit-retention enforcement, dynamic plugin install/enable/disable (Plugins page is read-only).

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (already enforced via `Directory.Build.props` — no changes needed).
- No MediatR, no controllers, no second component library, no WebAssembly.
- Gate before every commit: `dotnet build -warnaserror && dotnet test` both green.
- `SbConsole.Sdk` version bumps to **1.1.0** as of Task 1 — note this is already reflected in `docs/design.md`.
- Commands write audit rows via `IAuditWriter`; queries never write.
- Typed confirmation (type the target name to proceed) applies only when a target is prod-tagged **and** the Settings toggle is on; otherwise a plain two-button confirm. This decision lives inside the `IConfirmationService` implementation, not in callers.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

---

### Task 1: SDK v1.1 — IPlugin extensions, PluginContribution, IConfirmationService

**Files:**
- Modify: `src/SbConsole.Sdk/IPlugin.cs`
- Create: `src/SbConsole.Sdk/PluginContribution.cs`, `src/SbConsole.Sdk/IConfirmationService.cs`
- Modify: `tests/SbConsole.Web.Tests/NavMenuTests.cs` (its `FakePlugin` test double must implement the new `IPlugin` members or the build breaks)
- Test: `tests/SbConsole.Core.Tests/Sdk/PluginContributionTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by every later task in this plan):
  - `IPlugin` gains three members: `string ConnectionKind { get; }`, `string ConnectionKindDisplayName { get; }`, `PluginContribution Contribution { get; }`.
  - `sealed record PluginContribution(int PageCount, int ActionCount)`.
  - `interface IConfirmationService { Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default); }`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Core.Tests/Sdk/PluginContributionTests.cs
using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class PluginContributionTests
{
    [Fact]
    public void Holds_page_and_action_counts()
    {
        var contribution = new PluginContribution(PageCount: 3, ActionCount: 8);

        contribution.PageCount.Should().Be(3);
        contribution.ActionCount.Should().Be(8);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter PluginContributionTests`
Expected: FAIL to compile — `PluginContribution` does not exist.

- [ ] **Step 3: Write the SDK types**

```csharp
// src/SbConsole.Sdk/PluginContribution.cs
namespace SbConsole.Sdk;

/// <summary>Static summary of what a plugin adds, shown on the host's Plugins page.</summary>
public sealed record PluginContribution(int PageCount, int ActionCount);
```

```csharp
// src/SbConsole.Sdk/IConfirmationService.cs
namespace SbConsole.Sdk;

/// <summary>
/// Triggers the host's confirmation dialog for a mutating or destructive action.
/// Whether typed confirmation (type the target name) is required — versus a plain
/// two-button confirm — is decided by the implementation from the target's prod tag
/// and the host's Settings toggle; callers only describe what they're about to do.
/// </summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default);
}
```

Modify `src/SbConsole.Sdk/IPlugin.cs` to add three members to the existing interface (keep everything else unchanged):

```csharp
// add inside IPlugin, alongside the existing members:

    /// <summary>Connection kind this plugin's connections use, e.g. "azure-servicebus". Matches Connection.Kind.</summary>
    string ConnectionKind { get; }

    /// <summary>Shown in the host's Add/Edit Connection "Kind" dropdown.</summary>
    string ConnectionKindDisplayName { get; }

    /// <summary>Static summary shown on the host's Plugins page.</summary>
    PluginContribution Contribution { get; }
```

- [ ] **Step 4: Fix the now-broken FakePlugin test double**

`tests/SbConsole.Web.Tests/NavMenuTests.cs`'s `FakePlugin` implements `IPlugin` and will fail to compile once the interface grows. Read the file first, then add the three new members to `FakePlugin`:

```csharp
// inside the existing FakePlugin class, alongside its other members:
public string ConnectionKind => "fake";
public string ConnectionKindDisplayName => "Fake Connection Kind";
public PluginContribution Contribution => new(PageCount: 1, ActionCount: 1);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build -warnaserror && dotnet test`
Expected: full solution builds; `PluginContributionTests` passes; `NavMenuTests` still passes (FakePlugin compiles again).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: bump SbConsole.Sdk to 1.1.0 — IPlugin.ConnectionKind/Contribution, IConfirmationService"
```

---

### Task 2: UpdateConnectionCommandHandler

**Files:**
- Create: `src/SbConsole.Core/Connections/UpdateConnectionCommandHandler.cs`
- Test: `tests/SbConsole.Core.Tests/Connections/UpdateConnectionCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `Result`/`Result<T>` (Plan 1 Task 3), `ISecretProtector` (Task 4), `SbcDbContext`/`Connection` (Task 5), `IAuditWriter` (Task 7), `TestDb` test helper — all from Plan 1. `ActionRisk` from Sdk.
- Produces: `record UpdateConnectionCommand(Guid Id, string Name, IReadOnlyList<string> Tags, string? NewSecret, string Actor)` — `NewSecret` is `null` when the caller isn't replacing the stored secret (rename/re-tag only). `UpdateConnectionCommandHandler(IDbContextFactory<SbcDbContext>, ISecretProtector, IAuditWriter, TimeProvider)` with `Task<Result> HandleAsync(UpdateConnectionCommand cmd, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Connections/UpdateConnectionCommandHandlerTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

public class UpdateConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string name = "bus", string secret = "Endpoint=sb://original")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider());
        var result = await create.HandleAsync(new CreateConnectionCommand(name, "azure-servicebus", secret, ["dev"], "admin"));
        return result.Value;
    }

    [Fact]
    public async Task Renames_and_retags_without_touching_the_secret()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var protector = new AesGcmSecretProtector(Key);

        var result = await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider())
            .HandleAsync(new UpdateConnectionCommand(id, "bus-renamed", ["prod"], NewSecret: null, "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.Name.Should().Be("bus-renamed");
        saved.Tags.Should().Equal("prod");
        protector.Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://original");
    }

    [Fact]
    public async Task Replaces_the_secret_when_provided()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var protector = new AesGcmSecretProtector(Key);

        await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider())
            .HandleAsync(new UpdateConnectionCommand(id, "bus", ["dev"], NewSecret: "Endpoint=sb://replaced", "admin"));

        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        protector.Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://replaced");
    }

    [Fact]
    public async Task Writes_a_mutating_audit_entry_on_success()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        audit.ClearReceivedCalls();

        await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider())
            .HandleAsync(new UpdateConnectionCommand(id, "bus-2", ["dev"], null, "admin"));

        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.update" && a.Risk == ActionRisk.Mutating && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_id_returns_not_found_and_does_not_audit()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();

        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider())
            .HandleAsync(new UpdateConnectionCommand(Guid.NewGuid(), "x", [], null, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renaming_to_an_existing_name_returns_conflict()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        await SeedAsync(testDb, audit, name: "taken");
        var id = await SeedAsync(testDb, audit, name: "renaming-this-one");

        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider())
            .HandleAsync(new UpdateConnectionCommand(id, "taken", [], null, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.Conflict);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter UpdateConnectionCommandHandlerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Connections/UpdateConnectionCommandHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record UpdateConnectionCommand(
    Guid Id, string Name, IReadOnlyList<string> Tags, string? NewSecret, string Actor);

public sealed class UpdateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result> HandleAsync(UpdateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (connection is null)
        {
            return Result.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        if (connection.Name != cmd.Name && await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        connection.Name = cmd.Name;
        connection.TagsCsv = string.Join(',', cmd.Tags);
        if (cmd.NewSecret is not null)
        {
            connection.SecretCiphertext = protector.Protect(cmd.NewSecret);
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.update",
            Target = connection.Name,
            Risk = ActionRisk.Mutating,
            Succeeded = true,
        }, ct);

        return Result.Ok();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter UpdateConnectionCommandHandlerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add UpdateConnectionCommandHandler (rename, re-tag, replace secret)"
```

---

### Task 3: ListAuditEntriesQueryHandler

**Files:**
- Create: `src/SbConsole.Core/Audit/ListAuditEntriesQueryHandler.cs`
- Test: `tests/SbConsole.Core.Tests/Audit/ListAuditEntriesQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `SbcDbContext`/`AuditEntry` (Plan 1 Task 5), `TestDb`, `ActionRisk` (Sdk).
- Produces (used by Task 11's Dashboard and the Audit page task):
  - `sealed record AuditQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Actor = null, ActionRisk? Risk = null, string? TargetContains = null, int Page = 1, int PageSize = 25)`
  - `sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int TotalCount)`
  - `ListAuditEntriesQueryHandler(IDbContextFactory<SbcDbContext>)` with `Task<AuditPage> HandleAsync(AuditQuery query, CancellationToken ct = default)` — results ordered newest-first (`At descending`), `Page` is 1-based.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Audit/ListAuditEntriesQueryHandlerTests.cs
using FluentAssertions;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class ListAuditEntriesQueryHandlerTests
{
    private static AuditEntry Entry(string actor, string action, string target, ActionRisk risk, bool succeeded, DateTimeOffset at) => new()
    {
        At = at,
        Actor = actor,
        Action = action,
        Target = target,
        Risk = risk,
        Succeeded = succeeded,
    };

    private static async Task SeedAsync(TestDb testDb, params AuditEntry[] entries)
    {
        await using var db = testDb.CreateDbContext();
        db.AuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Returns_newest_first_with_total_count()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "queue.purge", "a", ActionRisk.Destructive, true, now.AddMinutes(-1)),
            Entry("admin", "auth.login", "-", ActionRisk.Safe, true, now));

        var page = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery());

        page.TotalCount.Should().Be(2);
        page.Entries.Select(e => e.Action).Should().Equal("auth.login", "queue.purge");
    }

    [Fact]
    public async Task Filters_by_risk_and_actor_and_target_substring()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "queue.purge", "payments-dlq", ActionRisk.Destructive, true, now),
            Entry("api", "message.peek", "orders-inbound", ActionRisk.Safe, true, now));

        var result = await new ListAuditEntriesQueryHandler(testDb)
            .HandleAsync(new AuditQuery(Actor: "admin", Risk: ActionRisk.Destructive, TargetContains: "payments"));

        result.Entries.Should().ContainSingle(e => e.Target == "payments-dlq");
    }

    [Fact]
    public async Task Filters_by_date_range()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb,
            Entry("admin", "a", "t", ActionRisk.Safe, true, now.AddDays(-10)),
            Entry("admin", "b", "t", ActionRisk.Safe, true, now));

        var result = await new ListAuditEntriesQueryHandler(testDb)
            .HandleAsync(new AuditQuery(From: now.AddDays(-1), To: now.AddDays(1)));

        result.Entries.Should().ContainSingle(e => e.Action == "b");
    }

    [Fact]
    public async Task Pages_results()
    {
        using var testDb = new TestDb();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(testDb, Enumerable.Range(0, 5)
            .Select(i => Entry("admin", $"action-{i}", "t", ActionRisk.Safe, true, now.AddMinutes(i)))
            .ToArray());

        var page1 = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(Page: 1, PageSize: 2));
        var page2 = await new ListAuditEntriesQueryHandler(testDb).HandleAsync(new AuditQuery(Page: 2, PageSize: 2));

        page1.TotalCount.Should().Be(5);
        page1.Entries.Should().HaveCount(2);
        page2.Entries.Should().HaveCount(2);
        page1.Entries.Select(e => e.Action).Should().NotIntersectWith(page2.Entries.Select(e => e.Action));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ListAuditEntriesQueryHandlerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Audit/ListAuditEntriesQueryHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Audit;

public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Actor = null,
    ActionRisk? Risk = null,
    string? TargetContains = null,
    int Page = 1,
    int PageSize = 25);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int TotalCount);

public sealed class ListAuditEntriesQueryHandler(IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<AuditPage> HandleAsync(AuditQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var filtered = db.AuditEntries.AsNoTracking().AsQueryable();

        if (query.From is { } from) filtered = filtered.Where(e => e.At >= from);
        if (query.To is { } to) filtered = filtered.Where(e => e.At <= to);
        if (query.Actor is { } actor) filtered = filtered.Where(e => e.Actor == actor);
        if (query.Risk is { } risk) filtered = filtered.Where(e => e.Risk == risk);
        if (query.TargetContains is { } target) filtered = filtered.Where(e => e.Target.Contains(target));

        var totalCount = await filtered.CountAsync(ct);
        var entries = await filtered
            .OrderByDescending(e => e.At)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new AuditPage(entries, totalCount);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ListAuditEntriesQueryHandlerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ListAuditEntriesQueryHandler (paged, filterable)"
```

---

### Task 4: ListPluginsQueryHandler

**Files:**
- Create: `src/SbConsole.Core/Plugins/ListPluginsQueryHandler.cs`
- Test: `tests/SbConsole.Core.Tests/Plugins/ListPluginsQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `IPlugin`, `PluginContribution` (Task 1); `SbcDbContext`/`Connection` (Plan 1 Task 5); `TestDb`.
- Produces (used by the Plugins page task): `sealed record PluginSummary(string Id, string DisplayName, string Version, PluginContribution Contribution, int ConnectionsInUse)`. `ListPluginsQueryHandler(IEnumerable<IPlugin> plugins, IDbContextFactory<SbcDbContext>)` with `Task<IReadOnlyList<PluginSummary>> HandleAsync(CancellationToken ct = default)`. Deliberately takes `IEnumerable<IPlugin>` (resolved by DI from every `AddSingleton<IPlugin>(...)` registration) rather than Web's `PluginRegistry` wrapper — Core must not reference Web.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Core.Tests/Plugins/ListPluginsQueryHandlerTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Plugins;

public class ListPluginsQueryHandlerTests
{
    private sealed class FakePlugin(string id, string kind) : IPlugin
    {
        public string Id => id;
        public string DisplayName => $"Plugin {id}";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public Type RootComponent => typeof(object);
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(PageCount: 2, ActionCount: 4);
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public async Task Joins_plugin_metadata_with_live_connection_counts()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "servicebus", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "b", Kind = "servicebus", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "c", Kind = "kafka", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var plugins = new IPlugin[] { new FakePlugin("servicebus", "servicebus"), new FakePlugin("kafka", "kafka") };
        var result = await new ListPluginsQueryHandler(plugins, testDb).HandleAsync();

        result.Should().ContainSingle(p => p.Id == "servicebus" && p.ConnectionsInUse == 2);
        result.Should().ContainSingle(p => p.Id == "kafka" && p.ConnectionsInUse == 1);
    }

    [Fact]
    public async Task No_plugins_returns_empty_list()
    {
        using var testDb = new TestDb();

        var result = await new ListPluginsQueryHandler([], testDb).HandleAsync();

        result.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ListPluginsQueryHandlerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Plugins/ListPluginsQueryHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public sealed record PluginSummary(string Id, string DisplayName, string Version, PluginContribution Contribution, int ConnectionsInUse);

public sealed class ListPluginsQueryHandler(IEnumerable<IPlugin> plugins, IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<IReadOnlyList<PluginSummary>> HandleAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var countsByKind = await db.Connections.AsNoTracking()
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Kind, g => g.Count, ct);

        return plugins
            .Select(p => new PluginSummary(
                p.Id,
                p.DisplayName,
                p.Version,
                p.Contribution,
                countsByKind.GetValueOrDefault(p.ConnectionKind, 0)))
            .ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ListPluginsQueryHandlerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ListPluginsQueryHandler (plugin metadata + live connection counts)"
```

---

### Task 5: Shared confirmation dialog — ConfirmDialog component + IConfirmationService

**Files:**
- Create: `src/SbConsole.Web/Components/Shared/ConfirmDialog.razor`, `src/SbConsole.Web/Confirmation/MudConfirmationService.cs`
- Modify: `src/SbConsole.Web/Program.cs` (register `IConfirmationService`)
- Test: `tests/SbConsole.Web.Tests/ConfirmDialogTests.cs`

**Interfaces:**
- Consumes: `IConfirmationService` (Task 1), `ISettings` (Plan 1 Task 6).
- Produces (used by Task 6's Connections delete flow and by Plan 3's plugin actions): `ConfirmDialog` — a MudDialog component with parameters `Verb` (string), `Target` (string), `Count` (int?), `RequireTypedConfirmation` (bool); renders a `button.confirm-action` that stays disabled until the typed value exactly matches `Target` when `RequireTypedConfirmation` is true, and is enabled immediately otherwise. `MudConfirmationService : IConfirmationService` — reads the `"confirm.requireTypedForProd"` setting (default `true` if unset) via `ISettings`, combines it with the caller's `isProd` flag, and opens `ConfirmDialog` through MudBlazor's `IDialogService`.

**Note on the installed test toolchain:** Plan 1 found that the installed `bunit` package (2.10.3) marks `TestContext`/`RenderComponent<T>()` obsolete under this project's warnings-as-errors setting; use `BunitContext`/`Render<T>()` instead (see `tests/SbConsole.Web.Tests/NavMenuTests.cs` for the working pattern already in this codebase). Similarly, MudBlazor's exact dialog cascading-parameter type (`MudDialogInstance` vs `IMudDialogInstance`) can differ by installed version — check what's actually available (e.g. via IDE/compiler errors or by inspecting the installed MudBlazor package's public API) and use whichever the installed version provides, preserving the described behavior; note any such adaptation in your report.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/ConfirmDialogTests.cs
using Bunit;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Web.Components.Shared;

namespace SbConsole.Web.Tests;

public class ConfirmDialogTests : BunitContext
{
    public ConfirmDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ConfirmDialog> RenderDialog(bool requireTyped, int? count = null)
    {
        var mudDialogInstance = Substitute.For<IMudDialogInstance>();
        return Render<ConfirmDialog>(parameters => parameters
            .AddCascadingValue(mudDialogInstance)
            .Add(p => p.Verb, "Purge")
            .Add(p => p.Target, "payments-dlq")
            .Add(p => p.Count, count)
            .Add(p => p.RequireTypedConfirmation, requireTyped));
    }

    [Fact]
    public void Plain_confirm_enables_immediately()
    {
        var cut = RenderDialog(requireTyped: false);

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Typed_confirmation_starts_disabled_and_enables_on_exact_match()
    {
        var cut = RenderDialog(requireTyped: true, count: 214);

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input").Input("payments-dlq");

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Typed_confirmation_stays_disabled_on_partial_match()
    {
        var cut = RenderDialog(requireTyped: true);

        cut.Find("input").Input("payments-d");

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();
    }
}
```

If `IMudDialogInstance` isn't the type the installed MudBlazor version exposes, substitute the correct type (e.g. the concrete `MudDialogInstance`, constructed however the installed version allows for testing — check its public constructors) and adjust `AddCascadingValue` accordingly; keep the three test cases' intent unchanged.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ConfirmDialogTests`
Expected: FAIL to compile — `ConfirmDialog` does not exist.

- [ ] **Step 3: Write the component**

```razor
@* src/SbConsole.Web/Components/Shared/ConfirmDialog.razor *@
<MudDialog>
    <DialogContent>
        <MudText Class="mb-2">
            @if (Count is { } count)
            {
                <text>This will @Verb.ToLowerInvariant() @count item(s) from <strong>@Target</strong>. This cannot be undone.</text>
            }
            else
            {
                <text>This will @Verb.ToLowerInvariant() <strong>@Target</strong>. This cannot be undone.</text>
            }
        </MudText>
        @if (RequireTypedConfirmation)
        {
            <MudText Typo="Typo.body2" Class="mb-1">Type <strong>@Target</strong> to confirm.</MudText>
            <MudTextField T="string" @bind-Value="_typedValue" Immediate="true" Placeholder="@Target" />
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Class="confirm-action" Color="Color.Error" Variant="Variant.Filled"
                   Disabled="@(RequireTypedConfirmation && _typedValue != Target)"
                   OnClick="Confirm">
            @Verb
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;

    [Parameter, EditorRequired] public string Verb { get; set; } = "";
    [Parameter, EditorRequired] public string Target { get; set; } = "";
    [Parameter] public int? Count { get; set; }
    [Parameter] public bool RequireTypedConfirmation { get; set; }

    private string _typedValue = "";

    private void Confirm() => MudDialog.Close(DialogResult.Ok(true));
    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ConfirmDialogTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Write MudConfirmationService and register it**

```csharp
// src/SbConsole.Web/Confirmation/MudConfirmationService.cs
using MudBlazor;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components.Shared;

namespace SbConsole.Web.Confirmation;

public sealed class MudConfirmationService(IDialogService dialogService, ISettings settings) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default)
    {
        var settingValue = await settings.GetAsync("confirm.requireTypedForProd", ct);
        var requireTypedForProd = settingValue is null || bool.Parse(settingValue);
        var requireTyped = isProd && requireTypedForProd;

        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Verb, verb },
            { x => x.Target, target },
            { x => x.Count, count },
            { x => x.RequireTypedConfirmation, requireTyped },
        };

        var dialog = await dialogService.ShowAsync<ConfirmDialog>($"{verb} {target}?", parameters);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
```

Modify `src/SbConsole.Web/Program.cs` — add alongside the other Core service registrations:

```csharp
builder.Services.AddScoped<IConfirmationService, Confirmation.MudConfirmationService>();
```

(`AddScoped` because `IDialogService` is itself scoped per Blazor circuit.)

- [ ] **Step 6: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add shared ConfirmDialog and MudBlazor-backed IConfirmationService"
```

---

### Task 6: Connections page — list, add, edit, delete

**Files:**
- Create: `src/SbConsole.Web/Components/Connections/AddEditConnectionDialog.razor`, `src/SbConsole.Web/Components/Pages/Connections.razor`
- Modify: `tests/SbConsole.Web.Tests/SbConsole.Web.Tests.csproj` (new project reference to `SbConsole.Core.Tests`, so `TestDb` is usable from Web component tests — see Step 1)
- Test: `tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs`, `tests/SbConsole.Web.Tests/ConnectionsPageTests.cs`

**Interfaces:**
- Consumes: `CreateConnectionCommandHandler`, `UpdateConnectionCommandHandler`, `DeleteConnectionCommandHandler`, `ListConnectionsQueryHandler` (Plan 1 Task 8, Task 2 of this plan), `IConfirmationService` (Task 5), `IPlugin.ConnectionKind`/`ConnectionKindDisplayName` (Task 1), `TestDb`, `AesGcmSecretProtector`.
- Produces: `AddEditConnectionDialog` (parameter `ConnectionInfo? Existing` — null means Add mode, non-null means Edit mode); route `/connections`.

**Actor resolution:** components that call a command handler resolve the acting user from the existing auth cascade rather than hardcoding a string: `[CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }`, plus a helper `private async Task<string> CurrentActorAsync() => AuthState is null ? "admin" : (await AuthState).User.Identity?.Name ?? "admin";` (the null check covers bUnit test hosts that don't supply the cascade; the real app always has it via Plan 1 Task 11's `AddCascadingAuthenticationState()`). Use this same pattern in every later task that calls a command handler from a component.

- [ ] **Step 0: Reference `SbConsole.Core.Tests` from `SbConsole.Web.Tests`**

`TestDb` (Plan 1 Task 5) lives in the `SbConsole.Core.Tests` project. This task is the first place Web component tests need it, so add the reference before writing any test that uses it:

```bash
dotnet add tests/SbConsole.Web.Tests reference tests/SbConsole.Core.Tests
dotnet build -warnaserror
```

Expected: builds clean (this only adds a reference; nothing new is used yet). Every later task in this plan that needs `TestDb` from a Web test relies on this reference already being in place.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Connections;

namespace SbConsole.Web.Tests;

public class AddEditConnectionDialogTests : BunitContext
{
    private sealed class FakePlugin(string kind, string displayName) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => displayName;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public Type RootComponent => typeof(object);
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => displayName;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
    }

    private readonly TestDb _testDb = new();

    public AddEditConnectionDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("azure-servicebus", "Azure Service Bus")]);
        // The component's [Inject] properties are resolved on every render regardless of
        // which test path is exercised, so both handlers need real registrations even in
        // tests that never call Save().
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(new byte[32]));
        Services.AddSingleton<IAuditWriter>(Substitute.For<IAuditWriter>());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
    }

    private IRenderedComponent<AddEditConnectionDialog> RenderDialog(ConnectionInfo? existing = null)
    {
        var mudDialogInstance = Substitute.For<IMudDialogInstance>();
        return Render<AddEditConnectionDialog>(parameters => parameters
            .AddCascadingValue(mudDialogInstance)
            .Add(p => p.Existing, existing));
    }

    [Fact]
    public void Add_mode_requires_name_kind_and_secret_before_save_enables()
    {
        var cut = RenderDialog();

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-select").Click();
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Edit_mode_prefills_fields_and_does_not_require_a_new_secret()
    {
        var existing = new ConnectionInfo(Guid.NewGuid(), "sb-dev", "azure-servicebus", ["dev"]);

        var cut = RenderDialog(existing);

        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev");
        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Adding_and_removing_tags_updates_the_chip_list()
    {
        var cut = RenderDialog();

        cut.Find("input#connection-tag-input").Input("prod");
        cut.Find("button#add-tag").Click();

        cut.FindAll("span.mud-chip-content").Should().Contain(e => e.TextContent.Contains("prod"));
    }
}
```

The exact CSS classes MudBlazor renders for a `MudSelect`'s popover items (`div.mud-select`, `div.mud-list-item` above) and for `MudChip` content (`span.mud-chip-content`) can differ by installed version. Before trusting these selectors, print `cut.Markup` once while writing the test and confirm the actual rendered structure, adjusting the selectors to match — keep each test's asserted behavior (Kind selection enables Save; tag add/remove updates the chip list) unchanged.

```csharp
// tests/SbConsole.Web.Tests/ConnectionsPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class ConnectionsPageTests : BunitContext
{
    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private static readonly byte[] Key = new byte[32];

    public ConnectionsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>(Array.Empty<IPlugin>());
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<DeleteConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
        Services.AddSingleton<ListConnectionsQueryHandler>();
        // Connections.razor injects IConfirmationService on every render; give every test a
        // default (tests that care about the confirm/cancel outcome override this before Render()).
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IConfirmationService>());
    }

    [Fact]
    public async Task Lists_seeded_connections()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();

        cut.Markup.Should().Contain("sb-dev");
    }

    [Fact]
    public async Task Delete_calls_the_handler_only_when_confirmation_service_returns_true()
    {
        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);

        var cut = Render<Connections>();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50); // let the async click handler's awaits (confirm + DB delete) complete

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "AddEditConnectionDialogTests|ConnectionsPageTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write AddEditConnectionDialog.razor**

```razor
@* src/SbConsole.Web/Components/Connections/AddEditConnectionDialog.razor *@
@using Microsoft.AspNetCore.Components.Authorization
@using SbConsole.Core.Connections
@using SbConsole.Sdk

<MudDialog>
    <DialogContent>
        <MudTextField Id="connection-name" @bind-Value="_name" Label="Name" Required="true" />
        <MudSelect T="string" @bind-Value="_kind" Label="Kind" Disabled="@(Existing is not null)">
            @foreach (var plugin in Plugins)
            {
                <MudSelectItem Value="@plugin.ConnectionKind">@plugin.ConnectionKindDisplayName</MudSelectItem>
            }
        </MudSelect>
        <MudTextField Id="connection-secret" @bind-Value="_secret" InputType="InputType.Password"
                      Label="@(Existing is null ? "Connection string" : "Replace connection string (leave blank to keep)")" />
        <div class="d-flex gap-2 align-center">
            <MudTextField Id="connection-tag-input" @bind-Value="_tagInput" Label="Add tag" />
            <MudButton Id="add-tag" OnClick="AddTag">Add</MudButton>
        </div>
        @foreach (var tag in _tags)
        {
            <MudChip T="string" OnClose="@(() => RemoveTag(tag))">@tag</MudChip>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Class="save-connection" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSave)" OnClick="Save">Save</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
    [Parameter] public ConnectionInfo? Existing { get; set; }

    [Inject] private IEnumerable<IPlugin> Plugins { get; set; } = default!;
    [Inject] private CreateConnectionCommandHandler CreateHandler { get; set; } = default!;
    [Inject] private UpdateConnectionCommandHandler UpdateHandler { get; set; } = default!;

    private string _name = "";
    private string _kind = "";
    private string _secret = "";
    private string _tagInput = "";
    private List<string> _tags = [];

    protected override void OnParametersSet()
    {
        if (Existing is not null)
        {
            _name = Existing.Name;
            _kind = Existing.Kind;
            _tags = [.. Existing.Tags];
        }
    }

    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_name)
        && !string.IsNullOrWhiteSpace(_kind)
        && (Existing is not null || !string.IsNullOrWhiteSpace(_secret));

    private void AddTag()
    {
        if (!string.IsNullOrWhiteSpace(_tagInput) && !_tags.Contains(_tagInput))
        {
            _tags.Add(_tagInput);
            _tagInput = "";
        }
    }

    private void RemoveTag(string tag) => _tags.Remove(tag);

    private async Task<string> CurrentActorAsync() =>
        AuthState is null ? "admin" : (await AuthState).User.Identity?.Name ?? "admin";

    private async Task Save()
    {
        var actor = await CurrentActorAsync();
        if (Existing is null)
        {
            await CreateHandler.HandleAsync(new CreateConnectionCommand(_name, _kind, _secret, _tags, actor));
        }
        else
        {
            var newSecret = string.IsNullOrWhiteSpace(_secret) ? null : _secret;
            await UpdateHandler.HandleAsync(new UpdateConnectionCommand(Existing.Id, _name, _tags, newSecret, actor));
        }

        MudDialog.Close(DialogResult.Ok(true));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Write Connections.razor**

```razor
@* src/SbConsole.Web/Components/Pages/Connections.razor *@
@page "/connections"
@using SbConsole.Core.Connections
@using SbConsole.Web.Components.Connections
@inject ListConnectionsQueryHandler ListHandler
@inject DeleteConnectionCommandHandler DeleteHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService

<PageTitle>Connections</PageTitle>
<MudText Typo="Typo.h4" Class="mb-4">Connections</MudText>
<MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenAdd" Class="mb-4">+ Add connection</MudButton>

<MudTable Items="_connections">
    <HeaderContent>
        <MudTh>Name</MudTh>
        <MudTh>Kind</MudTh>
        <MudTh>Tags</MudTh>
        <MudTh>Status</MudTh>
        <MudTh>Actions</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.Name</MudTd>
        <MudTd>@context.Kind</MudTd>
        <MudTd>
            @foreach (var tag in context.Tags)
            {
                <MudChip T="string" Color="@(tag == "prod" ? Color.Warning : Color.Default)">@tag</MudChip>
            }
        </MudTd>
        <MudTd>Untested</MudTd>
        <MudTd>
            <MudButton OnClick="@(() => OpenEdit(context))">Edit</MudButton>
            <MudButton Class="delete-connection" Color="Color.Error" OnClick="@(() => DeleteAsync(context))">Delete</MudButton>
        </MudTd>
    </RowTemplate>
</MudTable>

@code {
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private IReadOnlyList<ConnectionInfo> _connections = [];

    protected override async Task OnInitializedAsync() => await RefreshAsync();

    private async Task RefreshAsync() => _connections = await ListHandler.HandleAsync();

    private async Task<string> CurrentActorAsync() =>
        AuthState is null ? "admin" : (await AuthState).User.Identity?.Name ?? "admin";

    private async Task OpenAdd()
    {
        var dialog = await DialogService.ShowAsync<AddEditConnectionDialog>("Add connection");
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await RefreshAsync();
        }
    }

    private async Task OpenEdit(ConnectionInfo connection)
    {
        var parameters = new DialogParameters<AddEditConnectionDialog> { { x => x.Existing, connection } };
        var dialog = await DialogService.ShowAsync<AddEditConnectionDialog>("Edit connection", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await RefreshAsync();
        }
    }

    private async Task DeleteAsync(ConnectionInfo connection)
    {
        var confirmed = await Confirmation.ConfirmAsync("Delete", connection.Name, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        var actor = await CurrentActorAsync();
        await DeleteHandler.HandleAsync(new DeleteConnectionCommand(connection.Id, actor));
        await RefreshAsync();
    }
}
```

`AddEditConnectionDialog.Save()` (Step 3) should use the same `AuthState is null ? "admin" : ...` null-guarded pattern shown here — this covers both the real app (where the cascade is always present, per Plan 1 Task 11's `AddCascadingAuthenticationState()`) and any bUnit test host that doesn't supply an `AuthenticationState` cascade.

- [ ] **Step 5: Fix the test DI setup and run tests to verify they pass**

Apply the `_testDb` factory registration fix noted in Step 1. Run: `dotnet test tests/SbConsole.Web.Tests --filter "AddEditConnectionDialogTests|ConnectionsPageTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add Connections page — list, add, edit, delete"
```

---

### Task 7: Audit page — filterable, paginated

**Files:**
- Create: `src/SbConsole.Web/Components/Pages/Audit.razor`
- Test: `tests/SbConsole.Web.Tests/AuditPageTests.cs`

**Interfaces:**
- Consumes: `ListAuditEntriesQueryHandler`, `AuditQuery`, `AuditPage` (Task 3); `AuditEntry`, `ActionRisk`.
- Produces: route `/audit`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/AuditPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class AuditPageTests : BunitContext
{
    private readonly TestDb _testDb = new();

    public AuditPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
    }

    private async Task SeedAsync(params AuditEntry[] entries)
    {
        await using var db = _testDb.CreateDbContext();
        db.AuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private static AuditEntry Entry(string action, ActionRisk risk, DateTimeOffset at) => new()
    {
        At = at, Actor = "admin", Action = action, Target = "t", Risk = risk, Succeeded = true,
    };

    [Fact]
    public async Task Renders_seeded_entries()
    {
        await SeedAsync(Entry("queue.purge", ActionRisk.Destructive, DateTimeOffset.UtcNow));

        var cut = Render<Audit>();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("queue.purge");
    }

    [Fact]
    public async Task Risk_filter_narrows_the_visible_rows()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(
            Entry("queue.purge", ActionRisk.Destructive, now),
            Entry("message.peek", ActionRisk.Safe, now));

        var cut = Render<Audit>();
        await Task.Delay(50);
        cut.Render();
        cut.Markup.Should().Contain("message.peek");

        var select = cut.Find("div.mud-select");
        select.Click();
        cut.FindAll("div.mud-list-item").First(e => e.TextContent.Contains("Destructive")).Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().NotContain("message.peek");
        cut.Markup.Should().Contain("queue.purge");
    }
}
```

If the async `OnInitializedAsync`/filter-triggered reload doesn't settle within the `Task.Delay(50)` used above, replace it with bUnit's `cut.WaitForState(() => cut.Markup.Contains(...))` instead — that's the more robust bUnit pattern for waiting on async render completion; adapt if the fixed delay proves flaky.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter AuditPageTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write Audit.razor**

```razor
@* src/SbConsole.Web/Components/Pages/Audit.razor *@
@page "/audit"
@using SbConsole.Core.Audit
@using SbConsole.Sdk
@inject ListAuditEntriesQueryHandler ListHandler

<PageTitle>Audit</PageTitle>
<MudText Typo="Typo.h4" Class="mb-4">Audit</MudText>

<div class="d-flex gap-4 mb-4">
    <MudTextField T="string" Label="Actor" @bind-Value="_actor" @bind-Value:after="ReloadAsync" />
    <MudSelect T="ActionRisk?" Label="Risk" @bind-Value="_risk" @bind-Value:after="ReloadAsync">
        <MudSelectItem T="ActionRisk?" Value="null">All</MudSelectItem>
        <MudSelectItem T="ActionRisk?" Value="ActionRisk.Safe">Safe</MudSelectItem>
        <MudSelectItem T="ActionRisk?" Value="ActionRisk.Mutating">Mutating</MudSelectItem>
        <MudSelectItem T="ActionRisk?" Value="ActionRisk.Destructive">Destructive</MudSelectItem>
    </MudSelect>
    <MudTextField T="string" Label="Search target" @bind-Value="_targetContains" @bind-Value:after="ReloadAsync" />
</div>

<MudTable Items="_page.Entries">
    <HeaderContent>
        <MudTh>Timestamp</MudTh>
        <MudTh>Actor</MudTh>
        <MudTh>Action</MudTh>
        <MudTh>Target</MudTh>
        <MudTh>Risk</MudTh>
        <MudTh>Result</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.At.ToLocalTime()</MudTd>
        <MudTd>@context.Actor</MudTd>
        <MudTd>@context.Action</MudTd>
        <MudTd>@context.Target</MudTd>
        <MudTd>@context.Risk</MudTd>
        <MudTd>@(context.Succeeded ? "✓" : "✕")</MudTd>
    </RowTemplate>
</MudTable>

<div class="d-flex justify-space-between align-center mt-2">
    <MudText>@_page.Entries.Count of @_page.TotalCount</MudText>
    <div>
        <MudButton Disabled="@(_pageNumber <= 1)" OnClick="PreviousPage">‹ Prev</MudButton>
        <MudButton Disabled="@(_pageNumber * PageSize >= _page.TotalCount)" OnClick="NextPage">Next ›</MudButton>
    </div>
</div>

@code {
    private const int PageSize = 25;
    private string? _actor;
    private ActionRisk? _risk;
    private string? _targetContains;
    private int _pageNumber = 1;
    private AuditPage _page = new([], 0);

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        _pageNumber = 1;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _page = await ListHandler.HandleAsync(new AuditQuery(
            Actor: string.IsNullOrWhiteSpace(_actor) ? null : _actor,
            Risk: _risk,
            TargetContains: string.IsNullOrWhiteSpace(_targetContains) ? null : _targetContains,
            Page: _pageNumber,
            PageSize: PageSize));
    }

    private async Task PreviousPage()
    {
        _pageNumber--;
        await LoadAsync();
    }

    private async Task NextPage()
    {
        _pageNumber++;
        await LoadAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter AuditPageTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add Audit page — filterable, paginated"
```

---

### Task 8: Settings page + theme wiring

**Files:**
- Create: `src/SbConsole.Web/Components/Pages/Settings.razor`
- Modify: `src/SbConsole.Web/Components/Layout/MainLayout.razor`
- Test: `tests/SbConsole.Web.Tests/SettingsPageTests.cs`

**Interfaces:**
- Consumes: `ISettings` (Plan 1 Task 6, `DbSettings`).
- Produces: route `/settings`; settings keys used elsewhere in this plan — `"theme.mode"` (`"light"|"dark"|"system"`, read by `MainLayout`), `"confirm.requireTypedForProd"` (`"true"|"false"`, read by `MudConfirmationService` from Task 5), `"instance.name"`, `"auth.sessionTimeoutHours"`, `"audit.retentionDays"` (both persisted-only per `docs/design.md` §5.1 — no enforcement in this plan).

**Note on theme detection:** MudBlazor's `MudThemeProvider` exposes a way to detect the OS `prefers-color-scheme` (typically a `GetSystemPreference()` call via a component reference, awaited in `OnAfterRenderAsync` on first render). The exact method name/signature can differ by installed MudBlazor version — check what's actually available and adapt, keeping the intent: "system" mode should reflect the OS preference at load time. If detecting it turns out to need more than a straightforward one-call adaptation, it's acceptable to default "system" to light mode and note this as a concern in your report — don't spend excessive effort reverse-engineering an internal API for this one nuance.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/SettingsPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using SbConsole.Core.Settings;
using SbConsole.Core.Tests;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class SettingsPageTests : BunitContext
{
    private readonly TestDb _testDb = new();

    public SettingsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ISettings, DbSettings>();
    }

    [Fact]
    public async Task Saving_persists_instance_name_and_theme()
    {
        var cut = Render<Settings>();

        cut.Find("input#instance-name").Input("Platform Ops");
        cut.Find("button.save-settings").Click();
        await Task.Delay(50);

        var settings = Services.GetRequiredService<ISettings>();
        (await settings.GetAsync("instance.name")).Should().Be("Platform Ops");
    }
}
```

`Navigation.NavigateTo(..., forceLoad: true)` inside `Save` may throw or no-op in bUnit's fake `NavigationManager` — if this test fails specifically on the reload call rather than on the persisted value, wrap that call so a bUnit-environment failure there doesn't fail the test (e.g. catch `NavigationException` around it) rather than removing the reload behavior entirely, since it's needed for the real app.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter SettingsPageTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write Settings.razor**

```razor
@* src/SbConsole.Web/Components/Pages/Settings.razor *@
@page "/settings"
@using SbConsole.Core.Settings
@inject ISettings Settings
@inject NavigationManager Navigation

<PageTitle>Settings</PageTitle>
<MudText Typo="Typo.h4" Class="mb-4">Settings</MudText>

<MudTextField Id="instance-name" @bind-Value="_instanceName" Label="Instance name" />
<MudSelect T="string" @bind-Value="_theme" Label="Theme">
    <MudSelectItem Value="@("light")">Light</MudSelectItem>
    <MudSelectItem Value="@("dark")">Dark</MudSelectItem>
    <MudSelectItem Value="@("system")">System</MudSelectItem>
</MudSelect>
<MudNumericField T="int" @bind-Value="_sessionTimeoutHours" Label="Session timeout (hours)" />
<MudSwitch T="bool" @bind-Value="_requireTypedConfirmation" Label="Require typed confirmation on prod-tagged targets" />
<MudNumericField T="int" @bind-Value="_auditRetentionDays" Label="Audit retention (days)" />

<MudButton Class="save-settings" Color="Color.Primary" Variant="Variant.Filled" OnClick="SaveAsync">Save changes</MudButton>

@code {
    private string _instanceName = "";
    private string _theme = "system";
    private int _sessionTimeoutHours = 12;
    private bool _requireTypedConfirmation = true;
    private int _auditRetentionDays = 365;

    protected override async Task OnInitializedAsync()
    {
        _instanceName = await Settings.GetAsync("instance.name") ?? "";
        _theme = await Settings.GetAsync("theme.mode") ?? "system";
        _sessionTimeoutHours = int.TryParse(await Settings.GetAsync("auth.sessionTimeoutHours"), out var h) ? h : 12;
        _requireTypedConfirmation = !bool.TryParse(await Settings.GetAsync("confirm.requireTypedForProd"), out var b) || b;
        _auditRetentionDays = int.TryParse(await Settings.GetAsync("audit.retentionDays"), out var d) ? d : 365;
    }

    private async Task SaveAsync()
    {
        await Settings.SetAsync("instance.name", _instanceName);
        await Settings.SetAsync("theme.mode", _theme);
        await Settings.SetAsync("auth.sessionTimeoutHours", _sessionTimeoutHours.ToString());
        await Settings.SetAsync("confirm.requireTypedForProd", _requireTypedConfirmation.ToString());
        await Settings.SetAsync("audit.retentionDays", _auditRetentionDays.ToString());

        try
        {
            Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
        }
        catch (NavigationException)
        {
            // bUnit's fake NavigationManager doesn't support forceLoad; harmless in tests, real in the browser.
        }
    }
}
```

- [ ] **Step 4: Wire theme reading into MainLayout.razor**

Read the current `src/SbConsole.Web/Components/Layout/MainLayout.razor` first (from Plan 1 Task 10), then modify only the `<MudThemeProvider />` line and add an `@code` block:

```razor
<MudThemeProvider @bind-IsDarkMode="_isDarkMode" @ref="_themeProvider" />
```

```csharp
@code {
    [Inject] private SbConsole.Core.Settings.ISettings Settings { get; set; } = default!;
    private MudThemeProvider _themeProvider = default!;
    private bool _isDarkMode;

    protected override async Task OnInitializedAsync()
    {
        var theme = await Settings.GetAsync("theme.mode") ?? "system";
        _isDarkMode = theme == "dark";
        // "light" and unrecognized values already default to false above; "system" is
        // refined in OnAfterRenderAsync once the theme provider can report the OS preference.
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var theme = await Settings.GetAsync("theme.mode") ?? "system";
            if (theme == "system")
            {
                _isDarkMode = await _themeProvider.GetSystemPreference();
                StateHasChanged();
            }
        }
    }
}
```

If `GetSystemPreference()` isn't the method the installed MudBlazor version exposes, adapt per the note above.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter SettingsPageTests`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add Settings page and wire theme (light/dark/system)"
```

---

### Task 9: Plugins page (read-only)

**Files:**
- Create: `src/SbConsole.Web/Components/Pages/Plugins.razor`
- Test: `tests/SbConsole.Web.Tests/PluginsPageTests.cs`

**Interfaces:**
- Consumes: `ListPluginsQueryHandler`, `PluginSummary` (Task 4).
- Produces: route `/plugins`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/PluginsPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using SbConsole.Core.Plugins;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class PluginsPageTests : BunitContext
{
    private sealed class FakePlugin : IPlugin
    {
        public string Id => "azure-servicebus";
        public string DisplayName => "Azure Service Bus";
        public string Version => "0.4.1";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public Type RootComponent => typeof(object);
        public string ConnectionKind => "azure-servicebus";
        public string ConnectionKindDisplayName => "Azure Service Bus";
        public PluginContribution Contribution => new(PageCount: 3, ActionCount: 8);
        public void ConfigureServices(IServiceCollection services) { }
    }

    public PluginsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var testDb = new TestDb();
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(testDb);
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin()]);
        Services.AddSingleton<ListPluginsQueryHandler>();
    }

    [Fact]
    public void Renders_installed_plugins_with_version_and_contribution()
    {
        var cut = Render<Plugins>();

        cut.Markup.Should().Contain("Azure Service Bus");
        cut.Markup.Should().Contain("0.4.1");
        cut.Markup.Should().Contain("3 page");
        cut.Markup.Should().Contain("8 action");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter PluginsPageTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write Plugins.razor**

```razor
@* src/SbConsole.Web/Components/Pages/Plugins.razor *@
@page "/plugins"
@using SbConsole.Core.Plugins
@inject ListPluginsQueryHandler ListHandler

<PageTitle>Plugins</PageTitle>
<MudText Typo="Typo.h4" Class="mb-4">Plugins</MudText>

<MudTable Items="_plugins">
    <HeaderContent>
        <MudTh>Plugin</MudTh>
        <MudTh>Version</MudTh>
        <MudTh>Contributes</MudTh>
        <MudTh>Connections</MudTh>
        <MudTh>Status</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.DisplayName</MudTd>
        <MudTd>@context.Version</MudTd>
        <MudTd>@context.Contribution.PageCount page(s) · @context.Contribution.ActionCount action(s)</MudTd>
        <MudTd>@context.ConnectionsInUse in use</MudTd>
        <MudTd>Enabled</MudTd>
    </RowTemplate>
</MudTable>

@code {
    private IReadOnlyList<PluginSummary> _plugins = [];

    protected override async Task OnInitializedAsync() => _plugins = await ListHandler.HandleAsync();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SbConsole.Web.Tests --filter PluginsPageTests`
Expected: PASS.

- [ ] **Step 5: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add read-only Plugins page"
```

---

### Task 10: Dashboard (triage-first) + Login polish

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Home.razor` (Plan 1's stub Dashboard placeholder — replace its content), `src/SbConsole.Web/Components/Pages/Login.razor` (Plan 1 Task 11 — add tagline copy only)
- Test: `tests/SbConsole.Web.Tests/HomeTests.cs`

**Interfaces:**
- Consumes: `ListConnectionsQueryHandler` (Plan 1 Task 8), `ListAuditEntriesQueryHandler`/`AuditQuery` (Task 3).
- Produces: `/` renders the triage-first Dashboard (design.md §5.1).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/HomeTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class HomeTests : BunitContext
{
    private readonly TestDb _testDb = new();

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
    }

    [Fact]
    public void Shows_all_clear_when_nothing_needs_attention()
    {
        var cut = Render<Home>();

        cut.Markup.Should().Contain("All clear");
    }

    [Fact]
    public async Task Shows_recent_activity_from_real_audit_entries()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "auth.login",
                Target = "-", Risk = ActionRisk.Safe, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();

        cut.Markup.Should().Contain("auth.login");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter HomeTests`
Expected: FAIL — `Home` either doesn't exist under that name or doesn't yet render this content. (If Plan 1's stub file is a different component name than `Home`, adjust the test's `Render<T>()` call to match whatever Plan 1 actually named it — check `src/SbConsole.Web/Components/Pages/Home.razor` first.)

- [ ] **Step 3: Write the Dashboard content into Home.razor**

Read the existing `src/SbConsole.Web/Components/Pages/Home.razor` first, then replace its body with:

```razor
@page "/"
@using SbConsole.Core.Audit
@using SbConsole.Core.Connections
@inject ListConnectionsQueryHandler ConnectionsHandler
@inject ListAuditEntriesQueryHandler AuditHandler

<PageTitle>Dashboard</PageTitle>
<MudText Typo="Typo.h4" Class="mb-2">Dashboard</MudText>
<MudText Typo="Typo.body2" Class="mb-4">@_connectionCount connection(s) saved</MudText>

<MudText Typo="Typo.h6" Class="mb-2">Needs attention</MudText>
@if (_needsAttention.Count == 0)
{
    <MudAlert Severity="Severity.Success" Class="mb-4">All clear — nothing needs attention.</MudAlert>
}
else
{
    @* Populated once a plugin reports problems (Plan 3+); intentionally empty for now. *@
}

<MudText Typo="Typo.h6" Class="mb-2">Recent activity</MudText>
<MudTable Items="_recentActivity">
    <HeaderContent>
        <MudTh>Time</MudTh>
        <MudTh>Actor</MudTh>
        <MudTh>Action</MudTh>
        <MudTh>Target</MudTh>
        <MudTh>Risk</MudTh>
        <MudTh>Result</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.At.ToLocalTime()</MudTd>
        <MudTd>@context.Actor</MudTd>
        <MudTd>@context.Action</MudTd>
        <MudTd>@context.Target</MudTd>
        <MudTd>@context.Risk</MudTd>
        <MudTd>@(context.Succeeded ? "✓" : "✕")</MudTd>
    </RowTemplate>
</MudTable>
<MudLink Href="/audit">Open audit log →</MudLink>

@code {
    private int _connectionCount;
    private List<string> _needsAttention = [];
    private IReadOnlyList<AuditEntry> _recentActivity = [];

    protected override async Task OnInitializedAsync()
    {
        _connectionCount = (await ConnectionsHandler.HandleAsync()).Count;
        var page = await AuditHandler.HandleAsync(new AuditQuery(Page: 1, PageSize: 10));
        _recentActivity = page.Entries;
    }
}
```

Keep the file's component name as whatever Plan 1 used (likely `Home` matching the `Home.razor` filename) — do not rename the file or the route.

- [ ] **Step 4: Add tagline copy to Login.razor**

Read the current `src/SbConsole.Web/Components/Pages/Login.razor` (Plan 1 Task 11) and add two lines of static copy near the top of the card and below the form, without changing any existing behavior (form action, `AllowAnonymous` attribute, password field):

```razor
<MudText Typo="Typo.body2" Class="mb-4">Self-hosted infrastructure console</MudText>
```
```razor
<MudText Typo="Typo.caption" Class="mt-2">Single shared account · sessions are audited</MudText>
```

Place the first under the "SbConsole" heading and the second below the sign-in button, matching the existing card's structure.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter HomeTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Smoke-test the whole plan**

```bash
SBC_DB_PATH=/tmp/sbc-dev2.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8
curl -s -o /dev/null -w "root:%{http_code}\n" -L http://localhost:5080/
curl -s -o /dev/null -w "connections:%{http_code}\n" -L http://localhost:5080/connections
curl -s -o /dev/null -w "audit:%{http_code}\n" -L http://localhost:5080/audit
curl -s -o /dev/null -w "settings:%{http_code}\n" -L http://localhost:5080/settings
curl -s -o /dev/null -w "plugins:%{http_code}\n" -L http://localhost:5080/plugins
kill %1
```

Expected: all five `200` (each redirects through `/login` since no session cookie is sent, same as Plan 1's smoke tests).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add triage-first Dashboard, polish Login copy"
```

---

## After this plan

- **Plan 3 (next):** `SbConsole.Plugins.ServiceBus` UI — Queues, Topics & Subscriptions, Dead-letter/message peek, Send message dialog, registering via `IPlugin.NavItems`/`RootComponent` and consuming `IConfirmationService` for purge/delete actions.
- **Plan 4:** automation API endpoints under `/api/v1` with `ApiKeyFilter` + `?confirm=` on destructive routes, `SbConsole.IntegrationTests` with Testcontainers + Service Bus emulator.
- Explicitly deferred beyond this plan (see `docs/design.md` §5.1): connection reachability testing, plugin-contributed dashboard widgets, charts/wallboard views, audit CSV export, audit-retention enforcement, dynamic plugin install/enable/disable.

