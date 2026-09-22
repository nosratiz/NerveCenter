# Connections page redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single generic connection-editing dialog with a per-plugin-extensible editor, so AWS (and any future plugin) can offer structured fields and rich test-connection diagnostics, while Service Bus and Kafka keep working unchanged.

**Architecture:** Three new optional `IPlugin` members (all defaulted, zero-impact on existing plugins): `ConnectionFormComponentType` (a plugin-owned Blazor component the host hosts via `DynamicComponent`), `GetConnectionSummary` (safe, non-secret display fields persisted at save time), and a richer `ConnectionTestResult` (optional `Identity`/`Checks`). The host's Connections page becomes a two-pane layout (table + a conditionally-shown side panel) instead of a modal dialog. AWS is the only adopter in this plan.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor 9.9.0, EF Core + SQLite, xUnit + FluentAssertions + NSubstitute + bUnit.

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` must be green before every commit (CLAUDE.md, design spec §6).
- Spec: `docs/superpowers/specs/2026-09-22-connections-page-redesign-design.md`. Read it before starting Task 1 — this plan implements it, with two deliberate deviations from its literal wording, both justified below because they were discovered only while grounding the spec against the actual current code:
  1. **No `ConnectionFormComponentBase` base class in `SbConsole.Sdk`.** The spec's §3 sketch has plugins inherit a base class living in the SDK. `SbConsole.Sdk.csproj` currently has zero dependency beyond `Microsoft.Extensions.DependencyInjection.Abstractions` (no reference to `Microsoft.AspNetCore.Components`, i.e. no Blazor) — CLAUDE.md's architecture section calls this out explicitly ("Zero deps beyond BCL + Microsoft.Extensions.*.Abstractions"). Adding a `ComponentBase`-derived type to the SDK would require adding a `FrameworkReference` to `Microsoft.AspNetCore.App` there, which no other SDK type needs. Instead, `IPlugin.ConnectionFormComponentType` is just `System.Type?` (pure BCL, no new SDK dependency), and the plugin's own component (built in the plugin project, which already references `Microsoft.AspNetCore.App`) declares three parameters by a documented naming convention — `InitialSecret` (`string?`), `SecretChanged` (`EventCallback<string>`), `IsProd` (`bool`) — that the host's `DynamicComponent` binds by name. This is documented on `IPlugin.ConnectionFormComponentType`'s XML doc comment (Task 1) so it's discoverable without reading this plan.
  2. **No `MudDrawer` in `Connections.razor`.** The spec's §4 sketch uses `MudDrawer` (`Anchor.End`, `DrawerVariant.Persistent`). The app's only existing `MudDrawer` is the nav rail, declared as a direct child of the single `MudLayout` in `MainLayout.razor` (`src/SbConsole.Web/Components/Layout/MainLayout.razor`) — `Connections.razor` renders inside `MudMainContent`'s `@Body`, one level further in, and MudBlazor's drawer positioning model is not documented or tested for a second `MudDrawer` nested that deep outside the layout root. The mockup's panel has no slide/overlay animation anyway — it is a plain persistent side-by-side split. This plan implements it as a conditionally-rendered `MudPaper` in a flex row next to the table, which achieves the same visual result (list and editor both visible, editor takes a fixed-width column) with no dependency on `MudDrawer`'s layout-root assumptions.
- MudBlazor API note: this plan uses `MudToggleGroup<T>`/`MudToggleItem<T>` (confirmed present in the installed `mudblazor` 9.9.0 package via `strings` on the DLL) and `MudAutocomplete<T>`'s `SearchFunc`/`ToStringFunc`. If `dotnet build` reports a compile error against any MudBlazor component signature in this plan, do not guess a workaround — inspect the actual installed API (e.g. `grep`/`ILSpy`/IDE "Go to Definition" against `~/.nuget/packages/mudblazor/9.9.0/lib/net10.0/MudBlazor.dll`) and adapt, the same "confirmed against the installed package, not assumed" discipline `docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md` held AWS SDK usage to.
- Every new/changed handler constructor argument list must be updated at **every** call site — this plan lists every file each task touches; do not assume "find references" catches everything if your editor's index is stale.
- Known, accepted UX limitation (not a bug to fix in this plan): a plugin's custom connection form cannot pre-fill previously-saved secret fields when editing (secrets are write-only — never decrypted back to the browser, per `docs/design.md`'s existing principle). Editing any field in a custom form when editing an existing connection replaces the **entire** stored secret, so the user must re-enter every field their chosen auth mode needs, not just the one they changed. This is the same all-or-nothing contract the existing flat-textbox editor already has (leave blank to keep everything; type anything to replace everything) — Task 8 surfaces it as a visible caption rather than trying to validate per-field completeness.

---

## Task 1: SDK — richer test results and the two new optional `IPlugin` members

**Files:**
- Create: `src/SbConsole.Sdk/ConnectionCheck.cs`
- Modify: `src/SbConsole.Sdk/ConnectionTestResult.cs`
- Modify: `src/SbConsole.Sdk/IPlugin.cs`
- Test: `tests/SbConsole.Core.Tests/Sdk/ConnectionTestResultTests.cs`
- Test: `tests/SbConsole.Core.Tests/Sdk/ConnectionCheckTests.cs`

**Interfaces:**
- Produces: `ConnectionCheckStatus` enum (`Passed`, `Failed`), `ConnectionCheck(string Label, ConnectionCheckStatus Status, string? Detail = null)` record, `ConnectionTestResult(bool Success, string? ErrorMessage = null, string? Identity = null, IReadOnlyList<ConnectionCheck>? Checks = null)`, `IPlugin.ConnectionFormComponentType` (`Type?`, default `null`), `IPlugin.GetConnectionSummary(string secret)` (`IReadOnlyDictionary<string, string>`, default empty). Every later task consumes these exact names/signatures.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Core.Tests/Sdk/ConnectionCheckTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ConnectionCheckTests
{
    [Fact]
    public void Passed_check_carries_an_optional_detail()
    {
        var check = new ConnectionCheck("Queues visible", ConnectionCheckStatus.Passed, "12");

        check.Label.Should().Be("Queues visible");
        check.Status.Should().Be(ConnectionCheckStatus.Passed);
        check.Detail.Should().Be("12");
    }

    [Fact]
    public void Detail_is_optional()
    {
        var check = new ConnectionCheck("Topics visible", ConnectionCheckStatus.Failed);

        check.Detail.Should().BeNull();
    }
}
```

Add to `tests/SbConsole.Core.Tests/Sdk/ConnectionTestResultTests.cs` (append inside the existing `ConnectionTestResultTests` class, after `Failure_result_carries_an_error_message`):

```csharp
    [Fact]
    public void Existing_two_argument_construction_still_compiles_with_null_identity_and_checks()
    {
        var result = new ConnectionTestResult(Success: true, ErrorMessage: null);

        result.Identity.Should().BeNull();
        result.Checks.Should().BeNull();
    }

    [Fact]
    public void Success_result_can_carry_identity_and_passed_checks()
    {
        var result = new ConnectionTestResult(
            Success: true,
            Identity: "123456789012",
            Checks: [new ConnectionCheck("Queues visible", ConnectionCheckStatus.Passed, "12")]);

        result.Identity.Should().Be("123456789012");
        result.Checks.Should().ContainSingle(c => c.Label == "Queues visible" && c.Status == ConnectionCheckStatus.Passed);
    }

    [Fact]
    public void Under_permissioned_result_is_success_true_with_a_failed_check()
    {
        var result = new ConnectionTestResult(
            Success: true,
            Identity: "123456789012",
            Checks: [new ConnectionCheck("Topics visible", ConnectionCheckStatus.Failed, "sns:ListTopics denied")]);

        result.Success.Should().BeTrue();
        result.Checks.Should().ContainSingle(c => c.Status == ConnectionCheckStatus.Failed);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~ConnectionCheckTests|FullyQualifiedName~ConnectionTestResultTests"`
Expected: build error — `ConnectionCheck`, `ConnectionCheckStatus`, `ConnectionTestResult.Identity`, `ConnectionTestResult.Checks` don't exist yet.

- [ ] **Step 3: Implement**

`src/SbConsole.Sdk/ConnectionCheck.cs`:

```csharp
namespace SbConsole.Sdk;

/// <summary>One named diagnostic probe a plugin ran as part of TestConnectionAsync (e.g. "Queues visible").</summary>
public sealed record ConnectionCheck(string Label, ConnectionCheckStatus Status, string? Detail = null);

public enum ConnectionCheckStatus { Passed, Failed }
```

Replace `src/SbConsole.Sdk/ConnectionTestResult.cs` entirely:

```csharp
namespace SbConsole.Sdk;

/// <summary>
/// Outcome of IPlugin.TestConnectionAsync — shown in the Connections page's Status column and, for
/// a plugin that populates Identity/Checks, in the connection editor's richer Test-connection panel
/// (design spec docs/superpowers/specs/2026-09-22-connections-page-redesign-design.md §3). Identity
/// and Checks are optional: a plugin that never sets them (Service Bus, Kafka today) renders exactly
/// the plain single-line message it always has.
///
/// Three outcomes share this one shape, with no separate enum needed:
///  - success: Success=true, Checks all Passed.
///  - invalid credentials: Success=false, ErrorMessage set.
///  - valid but under-permissioned: Success=true, at least one Failed entry in Checks.
/// </summary>
public sealed record ConnectionTestResult(
    bool Success,
    string? ErrorMessage = null,
    string? Identity = null,
    IReadOnlyList<ConnectionCheck>? Checks = null);
```

In `src/SbConsole.Sdk/IPlugin.cs`, add these two members inside the `IPlugin` interface, right after `ConnectionKindDisplayName` (before `Contribution`):

```csharp
    /// <summary>
    /// Optional: a Blazor component type this plugin wants hosted in place of the host's generic
    /// flat-secret-string textbox on the Connections page's editor. The host renders it via
    /// &lt;DynamicComponent Type="..." Parameters="..."/&gt; and never parses the secret itself. The
    /// component must declare exactly these three parameters, matched by name (not by a shared base
    /// type — see docs/superpowers/plans/2026-09-22-connections-page-redesign.md's Global
    /// Constraints for why there's no SDK-level base class):
    ///   [Parameter] public string? InitialSecret { get; set; }         // read once, in OnInitialized
    ///   [Parameter] public EventCallback&lt;string&gt; SecretChanged { get; set; }
    ///   [Parameter] public bool IsProd { get; set; }
    /// Returning null (the default) means "no custom form" -- the host falls back to its existing
    /// flat textbox, so a plugin written before this member existed needs no change at all.
    /// </summary>
    Type? ConnectionFormComponentType => null;

    /// <summary>
    /// Optional: safe, non-secret display fields extracted from a connection's secret (e.g.
    /// {"Region": "eu-west-1"}), persisted once at Create/Update time and shown as small chips on
    /// the Connections list. Must never include anything credential-shaped -- this dictionary is
    /// stored in the database in plaintext (SbConsole.Core.Data.Entities.Connection.SummaryJson),
    /// unlike the secret itself. Returning an empty dictionary (the default) means "nothing to
    /// show" -- the default implementation does exactly that, so a plugin written before this
    /// member existed needs no change at all.
    /// </summary>
    IReadOnlyDictionary<string, string> GetConnectionSummary(string secret) => new Dictionary<string, string>();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~ConnectionCheckTests|FullyQualifiedName~ConnectionTestResultTests"`
Expected: PASS (all tests in both files).

Run: `dotnet build -warnaserror`
Expected: 0 warnings, 0 errors (confirms the two new defaulted `IPlugin` members don't break `ServiceBusPlugin`/`KafkaPlugin`/`AwsPlugin`, none of which implement them yet).

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Sdk/ConnectionCheck.cs src/SbConsole.Sdk/ConnectionTestResult.cs src/SbConsole.Sdk/IPlugin.cs tests/SbConsole.Core.Tests/Sdk/ConnectionCheckTests.cs tests/SbConsole.Core.Tests/Sdk/ConnectionTestResultTests.cs
git commit -m "feat(sdk): add ConnectionCheck, richer ConnectionTestResult, and optional per-plugin connection-form/summary hooks"
```

---

## Task 2: Core — `Connection.SummaryJson` column, `ConnectionInfo.Summary`

**Files:**
- Modify: `src/SbConsole.Core/Data/Entities/Connection.cs`
- Modify: `src/SbConsole.Sdk/ConnectionInfo.cs`
- Modify: `src/SbConsole.Core/Connections/EfConnectionProvider.cs`
- Modify: `src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs`
- Create: EF migration (via `dotnet ef`, filename generated)
- Test: `tests/SbConsole.Core.Tests/Data/Entities/ConnectionTests.cs`
- Test: `tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs` (append)
- Test: `tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs` (append)

**Interfaces:**
- Consumes: nothing new from Task 1.
- Produces: `Connection.SummaryJson` (`string?`), `Connection.Summary` (`IReadOnlyDictionary<string, string>`, computed, mirrors the existing `Tags` computed-property pattern), `ConnectionInfo.Summary` (`IReadOnlyDictionary<string, string>`, new optional trailing parameter defaulting to an empty dictionary — every existing `new ConnectionInfo(...)` call site across the codebase keeps compiling unchanged). Task 3 populates `SummaryJson` on write; Task 9 renders `ConnectionInfo.Summary` as chips.

- [ ] **Step 1: Write the failing test**

`tests/SbConsole.Core.Tests/Data/Entities/ConnectionTests.cs` (new file — check if the `Data/Entities` directory exists under `tests/SbConsole.Core.Tests`; create it if not):

```csharp
using FluentAssertions;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Tests.Data.Entities;

public class ConnectionTests
{
    [Fact]
    public void Summary_is_empty_when_SummaryJson_is_null()
    {
        var connection = new Connection { Name = "x", Kind = "k", SecretCiphertext = [] };

        connection.Summary.Should().BeEmpty();
    }

    [Fact]
    public void Summary_deserializes_the_stored_json()
    {
        var connection = new Connection
        {
            Name = "x", Kind = "k", SecretCiphertext = [],
            SummaryJson = """{"Region":"eu-west-1"}""",
        };

        connection.Summary.Should().ContainSingle(kv => kv.Key == "Region" && kv.Value == "eu-west-1");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~ConnectionTests"`
Expected: build error — `Connection.SummaryJson`/`Connection.Summary` don't exist yet.

- [ ] **Step 3: Implement the entity and `ConnectionInfo`**

Modify `src/SbConsole.Core/Data/Entities/Connection.cs` — add `using System.Text.Json;` at the top, add a `SummaryJson` property and a `Summary` computed property (mirroring the existing `Tags` pattern):

```csharp
using System.Text.Json;

namespace SbConsole.Core.Data.Entities;

public sealed class Connection
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string TagsCsv { get; set; } = "";
    public required byte[] SecretCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public string? LastTestError { get; set; }
    public string? SummaryJson { get; set; }

    public IReadOnlyList<string> Tags => TagsCsv.Length == 0 ? [] : TagsCsv.Split(',');

    public IReadOnlyDictionary<string, string> Summary => SummaryJson is null
        ? new Dictionary<string, string>()
        : JsonSerializer.Deserialize<Dictionary<string, string>>(SummaryJson) ?? new Dictionary<string, string>();
}
```

Modify `src/SbConsole.Sdk/ConnectionInfo.cs` — add a trailing optional `Summary` parameter:

```csharp
namespace SbConsole.Sdk;

/// <summary>Connection metadata safe to show in UI. Never carries the secret.</summary>
public sealed record ConnectionInfo(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Tags,
    bool? LastTestSucceeded = null,
    DateTimeOffset? LastTestedAt = null,
    string? LastTestError = null,
    DateTimeOffset CreatedAt = default,
    IReadOnlyDictionary<string, string>? Summary = null)
{
    public bool IsProd => Tags.Contains("prod", StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Summary { get; init; } = Summary ?? new Dictionary<string, string>();
}
```

Note: `Summary` is declared both as a positional constructor parameter (nullable, so callers can omit it) and re-exposed as an `init` property with a non-null default — this is the standard C# record pattern for "optional positional parameter with a non-nullable public type."

Update `src/SbConsole.Core/Connections/EfConnectionProvider.cs`'s `ListAsync` mapping line:

```csharp
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags, c.LastTestSucceeded, c.LastTestedAt, c.LastTestError, c.CreatedAt, c.Summary)).ToList();
```

Update `src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs`'s identical mapping line the same way:

```csharp
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags, c.LastTestSucceeded, c.LastTestedAt, c.LastTestError, c.CreatedAt, c.Summary)).ToList();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~ConnectionTests"`
Expected: PASS.

- [ ] **Step 5: Add the EF migration**

Run from the repo root:

```bash
dotnet ef migrations add AddConnectionSummaryColumn --project src/SbConsole.Core
```

Expected: a new pair of files under `src/SbConsole.Core/Migrations/` (`<timestamp>_AddConnectionSummaryColumn.cs` and `.Designer.cs`) plus an updated `SbcDbContextModelSnapshot.cs`. Open the generated `.cs` migration file and confirm it contains exactly one `AddColumn<string>(name: "SummaryJson", table: "Connections", type: "TEXT", nullable: true)` in `Up` and a matching `DropColumn` in `Down` — if `dotnet ef` produced anything else (e.g. it also picked up unrelated pending model changes), stop and investigate before continuing; do not hand-edit the generated files to force them to match if they don't.

- [ ] **Step 6: Add the missing tests for the two read paths**

Append to `tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs`, inside the existing test class. This inserts the `Connection` row directly via the DbContext (not through `CreateConnectionCommandHandler`, which doesn't gain summary-computing behavior until Task 3) so this task's tests and gate are self-contained and don't depend on a later task:

```csharp
    [Fact]
    public async Task List_includes_the_stored_summary()
    {
        using var testDb = new TestDb();
        var protector = new AesGcmSecretProtector(new byte[32]);
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                Name = "bus",
                Kind = "azure-servicebus",
                SecretCiphertext = protector.Protect("Endpoint=sb://real"),
                SummaryJson = """{"Region":"eu-west-1"}""",
            });
            await db.SaveChangesAsync();
        }

        var provider = new EfConnectionProvider(testDb, protector);
        var listed = await provider.ListAsync("azure-servicebus");

        listed.Should().ContainSingle(c => c.Summary.GetValueOrDefault("Region") == "eu-west-1");
    }
```

This test file will need `using SbConsole.Core.Data.Entities;` added if not already present — check the top of the file before adding.

Append to `tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs`, inside the existing test class, using the same direct-insert approach:

```csharp
    [Fact]
    public async Task Includes_the_stored_summary()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                Name = "bus",
                Kind = "azure-servicebus",
                SecretCiphertext = [],
                SummaryJson = """{"Region":"eu-west-1"}""",
            });
            await db.SaveChangesAsync();
        }

        var listed = await new ListConnectionsQueryHandler(testDb).HandleAsync();

        listed.Should().ContainSingle(c => c.Summary.GetValueOrDefault("Region") == "eu-west-1");
    }
```

This test file will need `using SbConsole.Core.Data.Entities;` added if not already present.

- [ ] **Step 7: Run full Core test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Core.Tests`
Expected: 0 warnings, 0 errors, all tests pass. This task is fully self-contained and does not depend on Task 3.

- [ ] **Step 8: Commit**

```bash
git add src/SbConsole.Core/Data/Entities/Connection.cs src/SbConsole.Sdk/ConnectionInfo.cs src/SbConsole.Core/Connections/EfConnectionProvider.cs src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs src/SbConsole.Core/Migrations tests/SbConsole.Core.Tests/Data/Entities/ConnectionTests.cs tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs
git commit -m "feat(core): add Connection.SummaryJson column and ConnectionInfo.Summary"
```

---

## Task 3: Core — persist `GetConnectionSummary` on Create/Update; fix every existing call site

**Files:**
- Modify: `src/SbConsole.Core/Connections/CreateConnectionCommandHandler.cs`
- Modify: `src/SbConsole.Core/Connections/UpdateConnectionCommandHandler.cs`
- Modify: `tests/SbConsole.Core.Tests/Connections/CreateConnectionCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Core.Tests/Connections/UpdateConnectionCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Core.Tests/Connections/DeleteConnectionCommandHandlerTests.cs`
- Modify: `tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs` (the pre-existing test, not Task 2's new one)
- Modify: `tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs` (the pre-existing test, not Task 2's new one)
- Modify: `tests/SbConsole.Core.Tests/Connections/TestConnectionCommandHandlerTests.cs` (its `SeedAsync` helper)
- Modify: `tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs` (its constructor)

**Interfaces:**
- Consumes: `IPlugin.GetConnectionSummary(string secret)` (Task 1), `Connection.SummaryJson` (Task 2).
- Produces: `CreateConnectionCommandHandler`/`UpdateConnectionCommandHandler` now take a 5th constructor parameter, `IEnumerable<IPlugin> plugins`, in that position (after `IAuditWriter audit`, before `TimeProvider clock` — see exact order below). Every test file that constructs either handler directly needs its argument list updated to match.

- [ ] **Step 1: Write the failing test**

Append to `tests/SbConsole.Core.Tests/Connections/CreateConnectionCommandHandlerTests.cs`, inside the existing test class (and update its `Handler` helper — see Step 1a):

```csharp
    private sealed class FakePluginWithSummary(string kind, IReadOnlyDictionary<string, string> summary) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => kind;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
        public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret) => summary;
    }

    [Fact]
    public async Task Persists_the_plugins_connection_summary()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var plugin = new FakePluginWithSummary("aws", new Dictionary<string, string> { ["Region"] = "eu-west-1" });
        var handler = new CreateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [plugin]);

        var result = await handler.HandleAsync(new CreateConnectionCommand("aws-prod", "aws", "region=eu-west-1", [], "admin"));

        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == result.Value);
        saved.Summary.Should().ContainSingle(kv => kv.Key == "Region" && kv.Value == "eu-west-1");
    }

    [Fact]
    public async Task Persists_an_empty_summary_when_no_plugin_matches_the_kind()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var handler = new CreateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), []);

        var result = await handler.HandleAsync(new CreateConnectionCommand("mystery", "unregistered-kind", "s", [], "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == result.Value);
        saved.Summary.Should().BeEmpty();
    }
```

This requires adding `using Microsoft.Extensions.DependencyInjection;` and `using SbConsole.Sdk;` at the top of the file if not already present (`SbConsole.Sdk` is already imported; `Microsoft.Extensions.DependencyInjection` is needed for `IServiceCollection` in the fake plugin).

Also update the file's existing `Handler` helper method to the new 5-argument signature (Step 1a — do this now so the two new tests above and every pre-existing test in the file compile together):

```csharp
    private static CreateConnectionCommandHandler Handler(TestDb db, IAuditWriter audit) =>
        new(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), []);
```

(This is a one-line change to an existing line — the two pre-existing tests in this file that call `Handler(testDb, audit)` need no other change, since they go through the helper.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~CreateConnectionCommandHandlerTests"`
Expected: build error — `CreateConnectionCommandHandler`'s constructor doesn't accept a 5th argument yet.

- [ ] **Step 3: Implement**

Replace `src/SbConsole.Core/Connections/CreateConnectionCommandHandler.cs` entirely:

```csharp
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;
using System.Text.Json;

namespace SbConsole.Core.Connections;

public sealed record CreateConnectionCommand(
    string Name, string Kind, string Secret, IReadOnlyList<string> Tags, string Actor);

public sealed class CreateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock,
    IEnumerable<IPlugin> plugins)
{
    public async Task<Result<Guid>> HandleAsync(CreateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result<Guid>.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        // No plugin matching cmd.Kind is not an error here (unlike TestConnectionCommandHandler,
        // which genuinely needs a plugin to test against) -- a summary is a nice-to-have display
        // aid, not a requirement to save a connection at all.
        var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == cmd.Kind);
        var summary = plugin?.GetConnectionSummary(cmd.Secret) ?? new Dictionary<string, string>();

        var connection = new Connection
        {
            Name = cmd.Name,
            Kind = cmd.Kind,
            SecretCiphertext = protector.Protect(cmd.Secret),
            SummaryJson = JsonSerializer.Serialize(summary),
        };
        connection.Id = Guid.NewGuid();
        connection.TagsCsv = string.Join(',', cmd.Tags);
        connection.CreatedAt = clock.GetUtcNow();
        db.Connections.Add(connection);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.create",
            Target = cmd.Name,
            Risk = ActionRisk.Mutating,
            Succeeded = true,
        }, ct);

        return Result<Guid>.Ok(connection.Id);
    }
}
```

Replace `src/SbConsole.Core/Connections/UpdateConnectionCommandHandler.cs` entirely:

```csharp
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;
using System.Text.Json;

namespace SbConsole.Core.Connections;

public sealed record UpdateConnectionCommand(
    Guid Id, string Name, IReadOnlyList<string> Tags, string? NewSecret, string Actor);

public sealed class UpdateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock,
    IEnumerable<IPlugin> plugins)
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

            // Only recompute the summary when a new secret was actually provided -- there is no
            // plaintext to recompute it from otherwise (the old secret stays encrypted at rest and
            // is never decrypted just to refresh a display chip), so the existing SummaryJson is
            // left untouched.
            var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == connection.Kind);
            var summary = plugin?.GetConnectionSummary(cmd.NewSecret) ?? new Dictionary<string, string>();
            connection.SummaryJson = JsonSerializer.Serialize(summary);
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

Now fix every remaining call site that constructs either handler directly with the old 4-argument list. Each of these needs a trailing `[]` (or a real plugin list where noted) added as the 5th argument:

`tests/SbConsole.Core.Tests/Connections/UpdateConnectionCommandHandlerTests.cs` — its `SeedAsync` helper constructs `CreateConnectionCommandHandler`, and every test method constructs `UpdateConnectionCommandHandler` directly. Change the helper:

```csharp
    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string name = "bus", string secret = "Endpoint=sb://original")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), []);
        var result = await create.HandleAsync(new CreateConnectionCommand(name, "azure-servicebus", secret, ["dev"], "admin"));
        return result.Value;
    }
```

And change every one of the five `new UpdateConnectionCommandHandler(...)` call sites in that same file by appending `, []` before the closing paren of the constructor call, exactly as follows:

In `Renames_and_retags_without_touching_the_secret`:

```csharp
        var result = await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus-renamed", ["prod"], NewSecret: null, "admin"));
```

In `Replaces_the_secret_when_provided`:

```csharp
        await new UpdateConnectionCommandHandler(testDb, protector, audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus", ["dev"], NewSecret: "Endpoint=sb://replaced", "admin"));
```

In `Writes_a_mutating_audit_entry_on_success`:

```csharp
        await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "bus-2", ["dev"], null, "admin"));
```

In `Unknown_id_returns_not_found_and_does_not_audit`:

```csharp
        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(Guid.NewGuid(), "x", [], null, "admin"));
```

In `Renaming_to_an_existing_name_returns_conflict`:

```csharp
        var result = await new UpdateConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), [])
            .HandleAsync(new UpdateConnectionCommand(id, "taken", [], null, "admin"));
```

`tests/SbConsole.Core.Tests/Connections/DeleteConnectionCommandHandlerTests.cs` — its one `new CreateConnectionCommandHandler(testDb, new AesGcmSecretProtector(new byte[32]), audit, new FakeTimeProvider())` call (inside `Deletes_and_audits_as_destructive`) becomes:

```csharp
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), audit, new FakeTimeProvider(), []);
```

`tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs` — its pre-existing `Lists_metadata_without_secrets_filtered_by_kind` test's `new CreateConnectionCommandHandler(testDb, new AesGcmSecretProtector(new byte[32]), Substitute.For<IAuditWriter>(), new FakeTimeProvider())` becomes:

```csharp
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), Substitute.For<IAuditWriter>(), new FakeTimeProvider(), []);
```

`tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs` — its pre-existing `Get_secret_decrypts_just_in_time_and_returns_null_for_unknown` test's `new CreateConnectionCommandHandler(testDb, protector, Substitute.For<IAuditWriter>(), new FakeTimeProvider())` becomes:

```csharp
        var create = new CreateConnectionCommandHandler(testDb, protector, Substitute.For<IAuditWriter>(), new FakeTimeProvider(), []);
```

`tests/SbConsole.Core.Tests/Connections/TestConnectionCommandHandlerTests.cs` — its `SeedAsync` helper's `new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider())` becomes:

```csharp
    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string kind = "azure-servicebus", string secret = "Endpoint=sb://x")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider(), []);
        var result = await create.HandleAsync(new CreateConnectionCommand("bus", kind, secret, [], "admin"));
        return result.Value;
    }
```

`tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs` — its constructor registers `CreateConnectionCommandHandler`/`UpdateConnectionCommandHandler` via `Services.AddSingleton<...>()` (DI-resolved, not directly `new`'d), so it needs no argument-list change — DI will resolve the new `IEnumerable<IPlugin>` parameter from the `Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin(...)])` line already present in that constructor. No action needed here; listed for completeness so this task's file list is a true "every call site" accounting. (This file is rewritten wholesale in Task 8 anyway.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests`
Expected: PASS, all tests in `SbConsole.Core.Tests` (this exercises every file touched above).

- [ ] **Step 5: Run the full solution build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, all tests pass across every project (confirms `SbConsole.Web.Tests`' DI-based construction still resolves correctly and no other project references either handler's constructor directly).

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Core/Connections/CreateConnectionCommandHandler.cs src/SbConsole.Core/Connections/UpdateConnectionCommandHandler.cs tests/SbConsole.Core.Tests/Connections/CreateConnectionCommandHandlerTests.cs tests/SbConsole.Core.Tests/Connections/UpdateConnectionCommandHandlerTests.cs tests/SbConsole.Core.Tests/Connections/DeleteConnectionCommandHandlerTests.cs tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs tests/SbConsole.Core.Tests/Connections/TestConnectionCommandHandlerTests.cs
git commit -m "feat(core): persist a plugin's connection summary on create/update"
```

---

## Task 4: AWS — `AwsConfigParser.Serialize`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/AwsConfigParser.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/AwsConfigParserTests.cs` (append — check this file exists; if not, create it following the existing test project's namespace convention `SbConsole.Plugins.Aws.Tests.Client`)

**Interfaces:**
- Produces: `AwsConfigParser.Serialize(IReadOnlyDictionary<string, string> fields)` → `string`. Task 5's `AwsConnectionFields.razor` is the only consumer.

- [ ] **Step 1: Write the failing test**

Append to `tests/SbConsole.Plugins.Aws.Tests/Client/AwsConfigParserTests.cs` (inside the existing `AwsConfigParserTests` class — read the file first to match its exact existing style before appending):

```csharp
    [Fact]
    public void Serialize_writes_known_keys_in_a_fixed_order()
    {
        var fields = new Dictionary<string, string>
        {
            ["region"] = "eu-west-1",
            ["mode"] = "access-keys",
            ["accessKeyId"] = "AKIA123",
            ["secretAccessKey"] = "shh",
        };

        var serialized = AwsConfigParser.Serialize(fields);

        serialized.Should().Be("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=shh");
    }

    [Fact]
    public void Serialize_omits_empty_values()
    {
        var fields = new Dictionary<string, string> { ["mode"] = "default-chain", ["region"] = "", ["endpoint"] = "" };

        AwsConfigParser.Serialize(fields).Should().Be("mode=default-chain");
    }

    [Theory]
    [InlineData("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=shh;sessionToken=tok")]
    [InlineData("mode=assume-role;region=us-east-1;roleArn=arn:aws:iam::123456789012:role/Reader;externalId=ext;sessionName=sess")]
    [InlineData("mode=default-chain;region=eu-west-1")]
    [InlineData("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=shh;endpoint=http://localhost:4566;pathStyle=True")]
    public void Parse_then_Serialize_round_trips(string original)
    {
        var parsed = AwsConfigParser.Parse(original);

        var serialized = AwsConfigParser.Serialize(parsed);

        AwsConfigParser.Parse(serialized).Should().BeEquivalentTo(parsed);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConfigParserTests"`
Expected: build error — `AwsConfigParser.Serialize` doesn't exist yet.

- [ ] **Step 3: Implement**

Add to `src/SbConsole.Plugins.Aws/Client/AwsConfigParser.cs`, inside the `AwsConfigParser` class (after `Parse`):

```csharp
    // Fixed key order purely for stable, human-diffable output when a saved secret is inspected
    // directly (e.g. in a DB browser) -- Parse itself is order-independent. AwsConnectionFields
    // (the only caller) never sets a key outside this list; an unrecognized key is silently
    // dropped rather than appended in arbitrary order, matching Parse's own "skip, don't throw"
    // tolerance for anything it doesn't recognize.
    private static readonly string[] SerializeKeyOrder =
    [
        "mode", "region", "accessKeyId", "secretAccessKey", "sessionToken",
        "roleArn", "externalId", "sessionName", "endpoint", "pathStyle",
    ];

    public static string Serialize(IReadOnlyDictionary<string, string> fields)
    {
        var parts = new List<string>();
        foreach (var key in SerializeKeyOrder)
        {
            if (fields.TryGetValue(key, out var value) && value.Length > 0)
            {
                parts.Add($"{key}={value}");
            }
        }

        return string.Join(';', parts);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConfigParserTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Client/AwsConfigParser.cs tests/SbConsole.Plugins.Aws.Tests/Client/AwsConfigParserTests.cs
git commit -m "feat(aws): add AwsConfigParser.Serialize, the inverse of Parse"
```

---

## Task 5: AWS — `AwsConnectionFields.razor`

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Client/AwsConnectionFields.razor`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/AwsConnectionFieldsTests.cs`

**Interfaces:**
- Consumes: `AwsConfigParser.Parse`/`Serialize` (Task 4).
- Produces: `AwsConnectionFields` (public Razor component), matching the `ConnectionFormComponentType` convention documented in Task 1 (`InitialSecret`, `SecretChanged`, `IsProd`). Task 6 wires `AwsPlugin.ConnectionFormComponentType` to `typeof(AwsConnectionFields)`.

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Plugins.Aws.Tests/Client/AwsConnectionFieldsTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public AwsConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AwsConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<AwsConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Defaults_to_access_keys_mode_when_no_secret_is_supplied()
    {
        var cut = RenderFields(null, out _);

        cut.Find("input#aws-access-key-id").Should().NotBeNull();
    }

    [Fact]
    public void Hydrates_non_secret_fields_from_the_initial_secret()
    {
        var cut = RenderFields("mode=assume-role;region=eu-west-1;roleArn=arn:aws:iam::123456789012:role/Reader", out _);

        cut.Find("input#aws-role-arn").GetAttribute("value").Should().Be("arn:aws:iam::123456789012:role/Reader");
    }

    [Fact]
    public void Switching_to_default_chain_hides_every_credential_field()
    {
        var cut = RenderFields("mode=access-keys;region=eu-west-1", out _);

        cut.Find("div.mud-toggle-item[data-value='default-chain']").Click();

        cut.FindAll("input#aws-access-key-id").Should().BeEmpty();
        cut.FindAll("input#aws-role-arn").Should().BeEmpty();
    }

    [Fact]
    public void Editing_a_field_emits_a_serialized_secret_via_SecretChanged()
    {
        var cut = RenderFields("mode=access-keys;region=eu-west-1", out var emitted);

        cut.Find("input#aws-access-key-id").Input("AKIA123");

        emitted.Should().ContainSingle(s => s.Contains("accessKeyId=AKIA123") && s.Contains("mode=access-keys") && s.Contains("region=eu-west-1"));
    }

    [Fact]
    public void Advanced_section_starts_collapsed_unless_an_endpoint_was_already_set()
    {
        var collapsed = RenderFields("mode=access-keys;region=eu-west-1", out _);
        collapsed.FindAll("input#aws-endpoint").Should().BeEmpty();

        var expanded = RenderFields("mode=access-keys;region=eu-west-1;endpoint=http://localhost:4566", out _);
        expanded.FindAll("input#aws-endpoint").Should().ContainSingle();
    }

    [Fact]
    public void Warns_when_a_custom_endpoint_is_set_on_a_prod_connection()
    {
        var cut = Render<AwsConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, "mode=access-keys;region=eu-west-1;endpoint=http://localhost:4566")
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, _ => { })));

        cut.Markup.Should().Contain("almost always a mistake");
    }
}
```

Note: the exact CSS selector `div.mud-toggle-item[data-value='default-chain']` in `Switching_to_default_chain_hides_every_credential_field` is a best guess at `MudToggleGroup`/`MudToggleItem`'s rendered markup — after writing the component in Step 3, run this test first and, if the selector doesn't match, inspect `cut.Markup` (add a temporary `Console.WriteLine(cut.Markup)` or use the test failure's rendered-output dump) to find the real element/attribute to select on, then fix the test to match reality. Do not change the component's behavior to match a guessed selector.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConnectionFieldsTests"`
Expected: build error — `AwsConnectionFields` doesn't exist yet.

- [ ] **Step 3: Implement**

Create `src/SbConsole.Plugins.Aws/Client/AwsConnectionFields.razor`:

```razor
@* src/SbConsole.Plugins.Aws/Client/AwsConnectionFields.razor *@
@using Amazon
@using MudBlazor

<MudAutocomplete T="string" Label="Region" Value="_region" ValueChanged="OnRegionChanged"
                 SearchFunc="SearchRegions" ToStringFunc="RegionDisplay" Immediate="true" CoerceText="false" />

<MudToggleGroup T="string" Value="_mode" ValueChanged="OnModeChanged" Fixed="true" Class="mt-3">
    <MudToggleItem Value="@("access-keys")">Access keys</MudToggleItem>
    <MudToggleItem Value="@("assume-role")">Assume role</MudToggleItem>
    <MudToggleItem Value="@("default-chain")">Default chain</MudToggleItem>
</MudToggleGroup>

@if (_mode == "access-keys")
{
    <MudTextField id="aws-access-key-id" Label="Access key ID" Value="_accessKeyId" ValueChanged="@(v => OnFieldChanged(v, x => _accessKeyId = x))" Immediate="true" Class="mt-3" />
    <MudTextField id="aws-secret-access-key" Label="Secret access key" InputType="InputType.Password" Value="_secretAccessKey" ValueChanged="@(v => OnFieldChanged(v, x => _secretAccessKey = x))" Immediate="true" Class="mt-3" />
    <MudTextField id="aws-session-token" Label="Session token (optional)" Value="_sessionToken" ValueChanged="@(v => OnFieldChanged(v, x => _sessionToken = x))" Immediate="true" Class="mt-3" />
}
else if (_mode == "assume-role")
{
    <MudTextField id="aws-role-arn" Label="Role ARN" Value="_roleArn" ValueChanged="@(v => OnFieldChanged(v, x => _roleArn = x))" Immediate="true" Class="mt-3" />
    <MudTextField id="aws-external-id" Label="External ID (optional)" Value="_externalId" ValueChanged="@(v => OnFieldChanged(v, x => _externalId = x))" Immediate="true" Class="mt-3" />
    <MudTextField id="aws-session-name" Label="Session name (optional)" Value="_sessionName" ValueChanged="@(v => OnFieldChanged(v, x => _sessionName = x))" Immediate="true" Class="mt-3" />
    <MudText Typo="Typo.caption" Class="mud-text-secondary">The base credentials for the sts:AssumeRole call come from the default chain.</MudText>
}
else
{
    <MudText Typo="Typo.body2" Class="mt-3">No credentials stored. SbConsole uses the role of the machine it runs on (environment variables, shared config/credentials file, or container/instance metadata).</MudText>
}

<MudButton Class="mt-3" OnClick="@(() => _advancedExpanded = !_advancedExpanded)">Advanced</MudButton>
@if (_advancedExpanded)
{
    <MudTextField id="aws-endpoint" Label="Custom endpoint URL (optional)" Value="_endpoint" ValueChanged="@(v => OnFieldChanged(v, x => _endpoint = x))" Immediate="true" Class="mt-2" />
    <MudSwitch T="bool" Label="Path-style addressing" Value="_pathStyle" ValueChanged="OnPathStyleChanged" Class="mt-2" />
    @if (IsProd && !string.IsNullOrWhiteSpace(_endpoint))
    {
        <MudAlert Severity="Severity.Warning" Class="mt-2">A custom endpoint with a prod tag is almost always a mistake.</MudAlert>
    }
}

@code {
    [Parameter] public string? InitialSecret { get; set; }
    [Parameter] public EventCallback<string> SecretChanged { get; set; }
    [Parameter] public bool IsProd { get; set; }

    private string _mode = "access-keys";
    private string _region = "";
    private string _accessKeyId = "";
    private string _secretAccessKey = "";
    private string _sessionToken = "";
    private string _roleArn = "";
    private string _externalId = "";
    private string _sessionName = "";
    private string _endpoint = "";
    private bool _pathStyle;
    private bool _advancedExpanded;

    private static readonly List<(string SystemName, string DisplayName)> AllRegions =
        [.. RegionEndpoint.EnumerableAllRegions.Select(r => (r.SystemName, r.DisplayName)).OrderBy(r => r.SystemName)];

    // Read once -- see IPlugin.ConnectionFormComponentType's doc comment (SbConsole.Sdk) and
    // docs/superpowers/plans/2026-09-22-connections-page-redesign.md's Global Constraints for why:
    // the host round-trips whatever this component last emitted back into InitialSecret on every
    // parent render, and re-parsing on OnParametersSet (instead of just once here) would fight the
    // user's in-progress typing in a field this component doesn't (yet) hold state for consistently.
    protected override void OnInitialized()
    {
        var parsed = AwsConfigParser.Parse(InitialSecret ?? "");
        _mode = parsed.GetValueOrDefault("mode", "access-keys");
        _region = parsed.GetValueOrDefault("region", "");
        _accessKeyId = parsed.GetValueOrDefault("accessKeyId", "");
        _secretAccessKey = parsed.GetValueOrDefault("secretAccessKey", "");
        _sessionToken = parsed.GetValueOrDefault("sessionToken", "");
        _roleArn = parsed.GetValueOrDefault("roleArn", "");
        _externalId = parsed.GetValueOrDefault("externalId", "");
        _sessionName = parsed.GetValueOrDefault("sessionName", "");
        _endpoint = parsed.GetValueOrDefault("endpoint", "");
        _pathStyle = bool.TryParse(parsed.GetValueOrDefault("pathStyle"), out var pathStyle) && pathStyle;
        _advancedExpanded = _endpoint.Length > 0;
    }

    private Task<IEnumerable<string>> SearchRegions(string text, CancellationToken ct)
    {
        var matches = string.IsNullOrWhiteSpace(text)
            ? AllRegions
            : AllRegions.Where(r =>
                r.SystemName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(matches.Select(r => r.SystemName));
    }

    private static string RegionDisplay(string? systemName)
    {
        if (string.IsNullOrEmpty(systemName))
        {
            return "";
        }

        var match = AllRegions.FirstOrDefault(r => r.SystemName == systemName);
        return match.SystemName is null ? systemName : $"{match.SystemName} · {match.DisplayName}";
    }

    private Task OnRegionChanged(string value)
    {
        _region = value;
        return NotifyChangedAsync();
    }

    private Task OnModeChanged(string value)
    {
        _mode = value;
        return NotifyChangedAsync();
    }

    private Task OnPathStyleChanged(bool value)
    {
        _pathStyle = value;
        return NotifyChangedAsync();
    }

    private Task OnFieldChanged(string value, Action<string> assign)
    {
        assign(value);
        return NotifyChangedAsync();
    }

    private Task NotifyChangedAsync()
    {
        var fields = new Dictionary<string, string> { ["mode"] = _mode };
        if (_region.Length > 0)
        {
            fields["region"] = _region;
        }

        switch (_mode)
        {
            case "access-keys":
                if (_accessKeyId.Length > 0) fields["accessKeyId"] = _accessKeyId;
                if (_secretAccessKey.Length > 0) fields["secretAccessKey"] = _secretAccessKey;
                if (_sessionToken.Length > 0) fields["sessionToken"] = _sessionToken;
                break;
            case "assume-role":
                if (_roleArn.Length > 0) fields["roleArn"] = _roleArn;
                if (_externalId.Length > 0) fields["externalId"] = _externalId;
                if (_sessionName.Length > 0) fields["sessionName"] = _sessionName;
                break;
        }

        if (_endpoint.Length > 0)
        {
            fields["endpoint"] = _endpoint;
            fields["pathStyle"] = _pathStyle.ToString();
        }

        return SecretChanged.InvokeAsync(AwsConfigParser.Serialize(fields));
    }
}
```

This component needs `Microsoft.AspNetCore.Components`/Razor support, already available in `SbConsole.Plugins.Aws.csproj` via its existing `FrameworkReference Include="Microsoft.AspNetCore.App"` and `PackageReference Include="MudBlazor"` — no `.csproj` change needed for this task.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConnectionFieldsTests"`
Expected: PASS. If `Switching_to_default_chain_hides_every_credential_field`'s selector doesn't match the real rendered markup, fix the test's selector per the note in Step 1 — do not change component behavior to match a wrong guess.

- [ ] **Step 5: Run the AWS plugin's full test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Client/AwsConnectionFields.razor tests/SbConsole.Plugins.Aws.Tests/Client/AwsConnectionFieldsTests.cs
git commit -m "feat(aws): add AwsConnectionFields, a structured connection-form component"
```

---

## Task 6: AWS — wire `AwsPlugin.ConnectionFormComponentType` and `GetConnectionSummary`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Modify: `tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs`

**Interfaces:**
- Consumes: `AwsConnectionFields` (Task 5), `AwsConfigParser.Parse` (existing).
- Produces: `AwsPlugin.ConnectionFormComponentType => typeof(AwsConnectionFields)`, `AwsPlugin.GetConnectionSummary(secret) => {"Region": ...}`.

- [ ] **Step 1: Write the failing test**

Append to `tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs`, inside the existing `AwsPluginTests` class:

```csharp
    [Fact]
    public void Declares_AwsConnectionFields_as_its_connection_form_component()
    {
        var plugin = new AwsPlugin();

        plugin.ConnectionFormComponentType.Should().Be(typeof(SbConsole.Plugins.Aws.Client.AwsConnectionFields));
    }

    [Fact]
    public void GetConnectionSummary_echoes_only_the_region()
    {
        var plugin = new AwsPlugin();

        var summary = plugin.GetConnectionSummary("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=shh");

        summary.Should().ContainSingle(kv => kv.Key == "Region" && kv.Value == "eu-west-1");
        summary.Values.Should().NotContain(v => v.Contains("AKIA123") || v.Contains("shh"));
    }

    [Fact]
    public void GetConnectionSummary_falls_back_to_a_placeholder_when_no_region_is_set()
    {
        var plugin = new AwsPlugin();

        var summary = plugin.GetConnectionSummary("mode=default-chain");

        summary["Region"].Should().Be("?");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsPluginTests"`
Expected: build error — `AwsPlugin.ConnectionFormComponentType`/`GetConnectionSummary` aren't overridden yet (compiles against the SDK defaults, so the two new tests fail on assertion, not compilation — `Declares_AwsConnectionFields...` fails because the default returns `null`, and `GetConnectionSummary_*` fail because the default returns an empty dictionary, so `summary["Region"]` throws `KeyNotFoundException`). Confirm both fail before continuing.

- [ ] **Step 3: Implement**

Modify `src/SbConsole.Plugins.Aws/AwsPlugin.cs` — add `using SbConsole.Plugins.Aws.Client;` is already present; add two new members after `ConnectionKindDisplayName`:

```csharp
    public string ConnectionKind => "aws";
    public string ConnectionKindDisplayName => "AWS SQS/SNS";

    public Type? ConnectionFormComponentType => typeof(AwsConnectionFields);

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        return new Dictionary<string, string> { ["Region"] = parsed.GetValueOrDefault("region", "?") };
    }
```

(Insert these two members directly below the existing `ConnectionKindDisplayName` line and above the `// Queues: ...` comment that precedes `Contribution`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsPluginTests"`
Expected: PASS, all tests in the file (including the three pre-existing ones — confirms no regression).

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs
git commit -m "feat(aws): wire AwsConnectionFields and GetConnectionSummary into AwsPlugin"
```

---

## Task 7: AWS — `SqsOperations.TestConnectionAsync` populates `Identity`/`Checks`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`
- Modify: `tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs`

**Interfaces:**
- Consumes: `ConnectionCheck`/`ConnectionCheckStatus` (Task 1).
- Produces: no new public signatures — `ISqsOperations.TestConnectionAsync`'s return values are richer, not its shape.

- [ ] **Step 1: Write the failing test**

The existing `TestConnectionAsync_against_an_unreachable_address_fails_cleanly_and_never_throws` test in `tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs` already covers the invalid-credentials/unreachable path (`Success` false, `ErrorMessage` set) and needs no change — `Identity`/`Checks` stay null on that path, which the existing assertions don't contradict. Add one new test to the same file confirming they stay unset on failure (documenting the contract, since there's no live AWS account to test the success path against — same "light coverage by necessity" limitation design spec §8 already accepts for this class):

```csharp
    [Fact]
    public async Task TestConnectionAsync_leaves_identity_and_checks_null_when_credentials_are_rejected()
    {
        var plugin = new AwsPlugin();

        var result = await plugin.TestConnectionAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        result.Identity.Should().BeNull();
        result.Checks.Should().BeNull();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~TestConnectionAsync_leaves_identity_and_checks_null"`
Expected: this actually PASSES against the current code too, since `Identity`/`Checks` already default to `null` and nothing sets them — this is a characterization test for behavior that shouldn't change, not a red-green test for new behavior. Run it now to confirm it passes before Step 3, then again after Step 3 to confirm Step 3 didn't regress it.

- [ ] **Step 3: Implement**

Replace `TestConnectionAsync` in `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`:

```csharp
    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        string identity;
        try
        {
            using var sts = BuildStsClient(secret);
            var response = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
            identity = response.Account;
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyAwsError.From(ex));
        }

        // Credentials are valid (GetCallerIdentity succeeded). A denied ListQueues probe is
        // reported as a Failed check, not a failed connection test -- design spec §3's "under-
        // permissioned" outcome: Success stays true, nothing is persisted, no button anywhere is
        // hidden as a result (design spec §8, Out of scope).
        try
        {
            using var sqs = BuildSqsClient(secret);
            var response = await sqs.ListQueuesAsync(new ListQueuesRequest { MaxResults = 1 }, ct);
            return new ConnectionTestResult(
                true, Identity: identity,
                Checks: [new ConnectionCheck("Queues visible", ConnectionCheckStatus.Passed, response.QueueUrls.Count.ToString())]);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(
                true, Identity: identity,
                Checks: [new ConnectionCheck("Queues visible", ConnectionCheckStatus.Failed, FriendlyAwsError.From(ex))]);
        }
    }
```

This requires `Amazon.SecurityToken.Model.GetCallerIdentityResponse.Account` — confirm this property exists on the installed `AWSSDK.SecurityToken` version (it is the account ID string, already implicitly relied upon by the mockup's "Account 123456789012" display; if the compiler reports it doesn't exist under this exact name, inspect the actual response type's members and adapt, same verification discipline as every other AWS SDK call in this plugin).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: PASS, full AWS plugin test suite (confirms this change doesn't regress `SqsOperationsTests.cs` or any handler test that depends on `ISqsOperations.TestConnectionAsync`'s shape).

- [ ] **Step 5: Run the full solution build**

Run: `dotnet build -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Client/SqsOperations.cs tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs
git commit -m "feat(aws): populate ConnectionTestResult.Identity/Checks from the existing STS/ListQueues probes"
```

---

## Task 8: Web — `ConnectionEditor.razor` (replaces `AddEditConnectionDialog.razor`)

**Files:**
- Create: `src/SbConsole.Web/Components/Connections/ConnectionEditor.razor`
- Delete: `src/SbConsole.Web/Components/Connections/AddEditConnectionDialog.razor`
- Create: `tests/SbConsole.Web.Tests/ConnectionEditorTests.cs`
- Delete: `tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs`

**Interfaces:**
- Consumes: `IPlugin.ConnectionFormComponentType`, `TestConnectionCommand`/`TestConnectionCommandHandler` (existing), `CreateConnectionCommand`/`UpdateConnectionCommand` (Task 3's 5-arg handlers, DI-resolved).
- Produces: `ConnectionEditor` component — `[Parameter] ConnectionInfo? Existing`, `[Parameter] EventCallback<bool> OnClosed` (`true` = saved, `false` = cancelled). Task 9's `Connections.razor` is the only consumer, and must render it with `@key` set to a value that changes whenever the target connection changes (a fresh `Guid` for "Add", `Existing.Id` for "Edit") — otherwise Blazor reuses the same component instance across different connections and `OnInitialized`'s once-only secret hydration (Task 5's note) never re-runs for the newly selected connection. This is called out again in Task 9.

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Web.Tests/ConnectionEditorTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Connections;

namespace SbConsole.Web.Tests;

public class ConnectionEditorTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private sealed class FakePlugin(string kind, string displayName, Type? formType = null) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => displayName;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => displayName;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Type? ConnectionFormComponentType => formType;
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true, Identity: "111122223333", Checks: [new ConnectionCheck("Widgets visible", ConnectionCheckStatus.Passed, "3")]));
    }

    // A trivial ConnectionFormComponentType, so tests can assert the DynamicComponent path is
    // taken without depending on the real AwsConnectionFields (a different project/test suite).
    private sealed class FakeFormComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter] public string? InitialSecret { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public Microsoft.AspNetCore.Components.EventCallback<string> SecretChanged { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public bool IsProd { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "id", "fake-form-field");
            builder.AddAttribute(2, "value", InitialSecret);
            builder.AddAttribute(3, "onchange", Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.ChangeEventArgs>(
                this, e => SecretChanged.InvokeAsync((string?)e.Value ?? "")));
            builder.CloseElement();
        }
    }

    private readonly TestDb _testDb = new();

    public ConnectionEditorTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("azure-servicebus", "Azure Service Bus")]);
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(new byte[32]));
        Services.AddSingleton<IAuditWriter>(Substitute.For<IAuditWriter>());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
    }

    [Fact]
    public void Add_mode_requires_name_kind_and_secret_before_save_enables()
    {
        var cut = Render<ConnectionEditor>();

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Edit_mode_prefills_name_and_does_not_require_a_new_secret()
    {
        var existing = new ConnectionInfo(Guid.NewGuid(), "sb-dev", "azure-servicebus", ["dev"]);

        var cut = Render<ConnectionEditor>(p => p.Add(x => x.Existing, existing));

        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev");
        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Save_in_add_mode_invokes_OnClosed_with_true()
    {
        var closedWith = new List<bool>();
        var cut = Render<ConnectionEditor>(p => p
            .Add(x => x.OnClosed, EventCallback.Factory.Create<bool>(this, v => closedWith.Add(v))));

        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");
        cut.Find("button.save-connection").Click();
        await Task.Delay(50);

        closedWith.Should().Equal(true);
    }

    [Fact]
    public void Cancel_invokes_OnClosed_with_false()
    {
        var closedWith = new List<bool>();
        var cut = Render<ConnectionEditor>(p => p
            .Add(x => x.OnClosed, EventCallback.Factory.Create<bool>(this, v => closedWith.Add(v))));

        cut.Find("button.cancel-connection").Click();

        closedWith.Should().Equal(false);
    }

    [Fact]
    public void A_plugin_with_a_custom_form_component_hosts_it_instead_of_the_flat_textbox()
    {
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("aws", "AWS SQS/SNS", typeof(FakeFormComponent))]);

        var cut = Render<ConnectionEditor>();
        cut.Find("input#connection-name").Input("aws-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();

        cut.FindAll("#fake-form-field").Should().ContainSingle();
        cut.FindAll("input#connection-secret").Should().BeEmpty();
    }

    [Fact]
    public async Task Testing_an_unsaved_connection_calls_the_plugin_directly_and_shows_the_result()
    {
        var cut = Render<ConnectionEditor>();
        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("111122223333");
        cut.Markup.Should().Contain("Widgets visible");
    }

    [Fact]
    public async Task Testing_an_existing_connection_routes_through_the_persisted_handler()
    {
        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "Endpoint=sb://x", ["dev"], "admin"));
        var existing = new ConnectionInfo(created.Value, "sb-dev", "azure-servicebus", ["dev"]);

        var cut = Render<ConnectionEditor>(p => p.Add(x => x.Existing, existing));
        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("111122223333");
        await using var db = _testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == existing.Id);
        saved.LastTestSucceeded.Should().BeTrue();
    }
}
```

Note: unlike the old `AddEditConnectionDialogTests.cs`, this test file renders `ConnectionEditor` as a plain component with `Render<ConnectionEditor>(...)`, not wrapped in a `CascadingValue<IMudDialogInstance>`/`MudPopoverProvider` scaffold — `ConnectionEditor` is no longer a `MudDialog`, so none of that machinery applies. `MudSelect`'s popover still needs `MudPopoverProvider` to render its options; if `cut.Find("div.mud-list-item")` fails to find anything, add a sibling `<MudPopoverProvider/>` the same way the old test did, via a wrapping `RenderFragment` — try the plain `Render<ConnectionEditor>()` form first, since bUnit's `Render<T>()` may already satisfy this without the old dialog-specific wrapping that existed only to work around `MudDialog`'s internal cascading-parameter gate.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ConnectionEditorTests"`
Expected: build error — `ConnectionEditor` doesn't exist yet, and `AddEditConnectionDialogTests.cs` (not yet deleted) still references `AddEditConnectionDialog`, which will still exist until Step 3 — so at this point both old and new test files coexist and both build. This is fine; Step 3 replaces the source file and Step 3 also deletes the old test file.

- [ ] **Step 3: Implement**

Create `src/SbConsole.Web/Components/Connections/ConnectionEditor.razor`:

```razor
@* src/SbConsole.Web/Components/Connections/ConnectionEditor.razor *@
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Authorization
@using SbConsole.Core.Connections
@using SbConsole.Sdk
@using SbConsole.Web.Auth

<MudText Typo="Typo.h6" Class="mb-3">@(Existing is null ? "Add connection" : "Edit connection")</MudText>

<MudTextField id="connection-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
<MudSelect T="string" @bind-Value="_kind" Label="Kind" Disabled="@(Existing is not null)">
    @foreach (var plugin in Plugins)
    {
        <MudSelectItem Value="@plugin.ConnectionKind">@plugin.ConnectionKindDisplayName</MudSelectItem>
    }
</MudSelect>

@if (Existing is not null && SelectedPlugin?.ConnectionFormComponentType is not null)
{
    <MudAlert Severity="Severity.Info" Dense="true" Class="mt-2">
        Editing any field below replaces the entire stored secret — fill in every field your chosen auth mode needs.
    </MudAlert>
}

@if (SelectedPlugin?.ConnectionFormComponentType is { } formType)
{
    <DynamicComponent Type="formType" Parameters="FormParameters()" />
}
else
{
    <MudTextField id="connection-secret" @bind-Value="_secret" InputType="InputType.Password" Immediate="true"
                  Label="@(Existing is null ? "Connection string" : "Replace connection string (leave blank to keep)")" />
}
<MudText Typo="Typo.caption" Class="mud-text-secondary mb-2">Encrypted at rest · never displayed again</MudText>

<div class="d-flex gap-2 align-center">
    <MudTextField id="connection-tag-input" @bind-Value="_tagInput" Label="Add tag" Immediate="true" />
    <MudButton id="add-tag" OnClick="AddTag">Add</MudButton>
</div>
@foreach (var tag in _tags)
{
    <MudChip T="string" OnClose="@(() => RemoveTag(tag))">@tag</MudChip>
}

<div class="d-flex align-center gap-2 mt-4">
    <MudButton Class="test-connection" Disabled="@(_testing || string.IsNullOrWhiteSpace(_secret))" OnClick="TestAsync">Test connection</MudButton>
    @if (_testing)
    {
        <MudProgressCircular Class="test-connection-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
    }
</div>

@if (_testResult is { } result)
{
    <div class="test-result mt-2">
        @if (result.Success)
        {
            <MudAlert Severity="Severity.Success" Dense="true">Credentials valid@(result.Identity is { } identity ? $" · {identity}" : "")</MudAlert>
            @if (result.Checks is { Count: > 0 } checks)
            {
                @foreach (var check in checks)
                {
                    <div class="d-flex align-center gap-2 mt-1">
                        <MudIcon Icon="@(check.Status == ConnectionCheckStatus.Passed ? Icons.Material.Filled.Check : Icons.Material.Filled.Warning)"
                                 Color="@(check.Status == ConnectionCheckStatus.Passed ? Color.Success : Color.Warning)" Size="Size.Small" />
                        <MudText Typo="Typo.body2">@check.Label@(check.Detail is { } detail ? $" — {detail}" : "")</MudText>
                    </div>
                }
            }
        }
        else
        {
            <MudAlert Severity="Severity.Error" Dense="true">@result.ErrorMessage</MudAlert>
        }
    </div>
}

<div class="d-flex gap-2 mt-4">
    <MudButton Class="save-connection" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSave)" OnClick="Save">Save</MudButton>
    <MudButton Class="cancel-connection" OnClick="Cancel">Cancel</MudButton>
</div>

@code {
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
    [Parameter] public ConnectionInfo? Existing { get; set; }
    [Parameter] public EventCallback<bool> OnClosed { get; set; }

    [Inject] private IEnumerable<IPlugin> Plugins { get; set; } = default!;
    [Inject] private CreateConnectionCommandHandler CreateHandler { get; set; } = default!;
    [Inject] private UpdateConnectionCommandHandler UpdateHandler { get; set; } = default!;
    [Inject] private TestConnectionCommandHandler TestHandler { get; set; } = default!;

    private string _name = "";
    private string _kind = "";
    private string _secret = "";
    private string _tagInput = "";
    private List<string> _tags = [];
    private bool _testing;
    private ConnectionTestResult? _testResult;

    private IPlugin? SelectedPlugin => Plugins.FirstOrDefault(p => p.ConnectionKind == _kind);

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

    // A fresh Dictionary each render is harmless -- DynamicComponent re-applies parameters by
    // value each render, and the child (see AwsConnectionFields) reads InitialSecret exactly once
    // in OnInitialized, so the round-tripped value on later renders is simply ignored by it.
    private Dictionary<string, object> FormParameters() => new()
    {
        ["InitialSecret"] = _secret,
        ["SecretChanged"] = EventCallback.Factory.Create<string>(this, OnSecretChanged),
        ["IsProd"] = _tags.Contains("prod", StringComparer.OrdinalIgnoreCase),
    };

    private void OnSecretChanged(string newSecret)
    {
        _secret = newSecret;
        _testResult = null;
    }

    private async Task TestAsync()
    {
        _testing = true;
        _testResult = null;
        try
        {
            if (Existing is null)
            {
                // Nothing saved yet -- call the plugin directly with the in-progress secret. No
                // persistence, no audit row: there is no connection row to attach either to.
                if (SelectedPlugin is { } plugin)
                {
                    _testResult = await plugin.TestConnectionAsync(_secret);
                }
            }
            else
            {
                // Tests the connection's PERSISTED secret, not any in-progress edits above -- the
                // same handler the old row-level "Test" action called, preserving
                // LastTestSucceeded/LastTestedAt/LastTestError persistence and the connection.test
                // audit row.
                var actor = await ActorResolver.ResolveAsync(AuthState);
                var result = await TestHandler.HandleAsync(new TestConnectionCommand(Existing.Id, actor));
                _testResult = result.Value;
            }
        }
        finally
        {
            _testing = false;
        }
    }

    private async Task Save()
    {
        var actor = await ActorResolver.ResolveAsync(AuthState);
        if (Existing is null)
        {
            await CreateHandler.HandleAsync(new CreateConnectionCommand(_name, _kind, _secret, _tags, actor));
        }
        else
        {
            var newSecret = string.IsNullOrWhiteSpace(_secret) ? null : _secret;
            await UpdateHandler.HandleAsync(new UpdateConnectionCommand(Existing.Id, _name, _tags, newSecret, actor));
        }

        await OnClosed.InvokeAsync(true);
    }

    private async Task Cancel() => await OnClosed.InvokeAsync(false);
}
```

Delete `src/SbConsole.Web/Components/Connections/AddEditConnectionDialog.razor` and `tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs`:

```bash
git rm src/SbConsole.Web/Components/Connections/AddEditConnectionDialog.razor tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ConnectionEditorTests"`
Expected: PASS. If `MudSelect`'s popover doesn't render its options without the old dialog's `MudPopoverProvider`/`CascadingValue` wrapping, add that wrapping back around `Render<ConnectionEditor>(...)` in the test helper (build a small `RenderFragment`-based helper mirroring the old `RenderDialog` method's popover-provider half, minus the now-irrelevant `IMudDialogInstance` cascading value) — do not skip or weaken the assertion.

- [ ] **Step 5: Run the full Web test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Web.Tests`
Expected: 0 warnings, 0 errors. `ConnectionsPageTests.cs` will currently fail to build/pass at this point, since it still references the old dialog-based flow — that's expected and fixed by Task 9. If your workflow requires every task to leave the whole solution green, do Task 9 in the same commit as this one instead of separately.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Connections/ConnectionEditor.razor tests/SbConsole.Web.Tests/ConnectionEditorTests.cs
git commit -m "feat(web): replace AddEditConnectionDialog with ConnectionEditor, a non-modal panel with inline test-connection"
```

---

## Task 9: Web — `Connections.razor` two-pane layout

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Connections.razor`
- Modify: `tests/SbConsole.Web.Tests/ConnectionsPageTests.cs`

**Interfaces:**
- Consumes: `ConnectionEditor` (Task 8), `ConnectionInfo.Summary` (Task 2).

- [ ] **Step 1: Write the failing test**

Replace `tests/SbConsole.Web.Tests/ConnectionsPageTests.cs` entirely:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

public class ConnectionsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

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
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
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
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);

        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50);

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().NotContain(c => c.Name == "sb-dev");
    }

    [Fact]
    public async Task Delete_leaves_the_connection_when_confirmation_service_returns_false()
    {
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(false);
        Services.AddSingleton(confirmation);

        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50);

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().Contain(c => c.Name == "sb-dev");
    }

    [Fact]
    public async Task Shows_never_tested_for_a_connection_that_has_no_recorded_test()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();

        cut.Markup.Should().Contain("Never tested");
    }

    [Fact]
    public async Task Editor_panel_is_hidden_until_Add_or_Edit_is_clicked()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = Render<Connections>();

        cut.FindAll("input#connection-name").Should().BeEmpty();

        cut.Find("button.add-connection-action").Click();

        cut.FindAll("input#connection-name").Should().ContainSingle();
    }

    [Fact]
    public async Task Editing_a_different_row_swaps_the_panel_to_that_connections_data()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev-a", "azure-servicebus", "secret-a", ["dev"], "admin"));
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev-b", "azure-servicebus", "secret-b", ["dev"], "admin"));

        var cut = Render<Connections>();
        cut.FindAll("button.edit-connection")[0].Click();
        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev-a");

        cut.FindAll("button.edit-connection")[1].Click();
        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev-b");
    }

    [Fact]
    public async Task Saving_from_the_panel_refreshes_the_list_and_closes_the_panel()
    {
        var cut = Render<Connections>();
        cut.Find("button.add-connection-action").Click();

        cut.Find("input#connection-name").Input("sb-new");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://new");
        cut.Find("button.save-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("sb-new");
        cut.FindAll("input#connection-name").Should().BeEmpty();
    }

    [Fact]
    public async Task Renders_a_connections_summary_as_chips()
    {
        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("aws-dev", "aws", "region=eu-west-1", [], "admin"));
        await using (var db = _testDb.CreateDbContext())
        {
            var row = await db.Connections.SingleAsync(c => c.Id == created.Value);
            row.SummaryJson = """{"Region":"eu-west-1"}""";
            await db.SaveChangesAsync();
        }

        var cut = Render<Connections>();

        cut.Markup.Should().Contain("eu-west-1");
    }
}
```

This file will need `using Microsoft.EntityFrameworkCore;` added for the `SingleAsync`/`SaveChangesAsync` calls in the new summary test.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ConnectionsPageTests"`
Expected: build error or failures — `Connections.razor` still opens `AddEditConnectionDialog` via `IDialogService`, which no longer exists (deleted in Task 8), and there's no `button.add-connection-action`-driven inline panel yet.

- [ ] **Step 3: Implement**

Replace `src/SbConsole.Web/Components/Pages/Connections.razor` entirely:

```razor
@* src/SbConsole.Web/Components/Pages/Connections.razor *@
@page "/connections"
@using SbConsole.Core.Connections
@using SbConsole.Web.Auth
@using SbConsole.Web.Components.Connections
@inject ListConnectionsQueryHandler ListHandler
@inject DeleteConnectionCommandHandler DeleteHandler
@inject IConfirmationService Confirmation

<PageTitle>Connections</PageTitle>
<MudText Typo="Typo.h4" Class="mb-2">Connections</MudText>
<MudText Typo="Typo.body2" Class="mud-text-secondary mb-4">@_connections.Count saved</MudText>

<div class="d-flex align-center flex-wrap gap-4 mb-4">
    <MudTextField T="string" Class="connection-filter" Placeholder="Filter by name or tag..." @bind-Value="_filterText" Immediate="true" Style="max-width:280px" Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search" />
    <MudSpacer />
    <MudButton Class="add-connection-action" Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenAdd">+ Add connection</MudButton>
</div>

<div class="d-flex gap-4" style="align-items:flex-start;">
    <div style="flex:1; min-width:0;">
        <MudTable Items="FilteredConnections">
            <HeaderContent>
                <MudTh>Name</MudTh>
                <MudTh>Kind</MudTh>
                <MudTh>Tags</MudTh>
                <MudTh></MudTh>
                <MudTh>Created</MudTh>
                <MudTh>Status</MudTh>
                <MudTh>Actions</MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>@context.Name</MudTd>
                <MudTd>@context.Kind</MudTd>
                <MudTd>
                    @foreach (var tag in context.Tags)
                    {
                        <MudChip T="string" Color="@(string.Equals(tag, "prod", StringComparison.OrdinalIgnoreCase) ? Color.Warning : Color.Default)">@tag</MudChip>
                    }
                </MudTd>
                <MudTd>
                    @foreach (var kv in context.Summary)
                    {
                        <MudChip T="string" Size="Size.Small">@kv.Value</MudChip>
                    }
                </MudTd>
                <MudTd>@context.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd")</MudTd>
                <MudTd>@StatusText(context)</MudTd>
                <MudTd>
                    <MudButton Class="edit-connection" OnClick="@(() => OpenEdit(context))">Edit</MudButton>
                    <MudButton Class="delete-connection" Color="Color.Error" OnClick="@(() => DeleteAsync(context))">Delete</MudButton>
                </MudTd>
            </RowTemplate>
        </MudTable>
        <MudText Typo="Typo.caption" Class="mud-text-secondary mt-2">Secrets are write-only — stored encrypted, never returned to the browser.</MudText>
    </div>

    @if (_editorOpen)
    {
        <MudPaper Class="pa-4" Style="width:420px; flex:none;" Elevation="1">
            <ConnectionEditor @key="@_editorKey" Existing="@_editing" OnClosed="HandleEditorClosed" />
        </MudPaper>
    }
</div>

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private string _filterText = "";
    private bool _editorOpen;
    private ConnectionInfo? _editing;
    private Guid _editorKey = Guid.NewGuid();

    private IEnumerable<ConnectionInfo> FilteredConnections => string.IsNullOrWhiteSpace(_filterText)
        ? _connections
        : _connections.Where(c =>
            c.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
            c.Tags.Any(t => t.Contains(_filterText, StringComparison.OrdinalIgnoreCase)));

    protected override async Task OnInitializedAsync() => await RefreshAsync();

    private async Task RefreshAsync() => _connections = await ListHandler.HandleAsync();

    private static string StatusText(ConnectionInfo connection) => connection switch
    {
        { LastTestedAt: null } => "Never tested",
        { LastTestSucceeded: true } => "OK",
        { LastTestError: { } error } => error,
        _ => "Failed",
    };

    // @key is set to a fresh Guid for "Add" and to the target connection's own Id for "Edit" --
    // without a changing @key, Blazor reuses the same ConnectionEditor instance when switching
    // between two different rows' Edit buttons, and its Existing parameter update alone does NOT
    // re-run OnInitialized, so its (and any hosted DynamicComponent's) once-only secret hydration
    // would never re-run for the newly selected connection.
    private void OpenAdd()
    {
        _editing = null;
        _editorKey = Guid.NewGuid();
        _editorOpen = true;
    }

    private void OpenEdit(ConnectionInfo connection)
    {
        _editing = connection;
        _editorKey = connection.Id;
        _editorOpen = true;
    }

    private async Task HandleEditorClosed(bool saved)
    {
        _editorOpen = false;
        if (saved)
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

        var actor = await ActorResolver.ResolveAsync(AuthState);
        await DeleteHandler.HandleAsync(new DeleteConnectionCommand(connection.Id, actor));
        await RefreshAsync();
    }

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ConnectionsPageTests"`
Expected: PASS, every test in the file. `Saving_from_the_panel_refreshes_the_list_and_closes_the_panel` clicks through the "Kind" `MudSelect`'s popover the same way Task 8's `ConnectionEditorTests.cs` tests do — if `cut.Find("div.mud-list-item")` throws element-not-found here too, apply the same fix noted in Task 8's Step 4 (render a `<MudPopoverProvider/>` alongside `<Connections/>` via a wrapping `RenderFragment`, the same role it plays in `MainLayout.razor`'s real app shell).

- [ ] **Step 5: Run the full solution build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test in every project passes. This is the first point in the plan where the whole solution should be fully green again after Task 8 — confirm this explicitly before moving on.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Connections.razor tests/SbConsole.Web.Tests/ConnectionsPageTests.cs
git commit -m "feat(web): two-pane Connections page with an inline editor panel and summary chips"
```

---

## Task 10: Docs — reconcile `docs/design.md`

**Files:**
- Modify: `docs/design.md`

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Read the current end of the relevant sections**

Read `docs/design.md`'s §6.7 (AWS plugin) and §8 (Testing) to find their exact current end, and read its table of contents / section numbering to pick the next free top-level section number for this cross-cutting (not single-plugin) change — this doesn't belong nested under §6 (plugin-specific sections), since it changed `SbConsole.Sdk`, `SbConsole.Core`, and `SbConsole.Web` on top of the AWS plugin.

- [ ] **Step 2: Append a new dated section**

Add a new top-level section (pick the next unused number after reading the file, referred to here as `§N`) documenting, in the same style as §6.7:

- What shipped: `IPlugin.ConnectionFormComponentType`/`GetConnectionSummary`, the richer `ConnectionTestResult` (`Identity`/`Checks`), `Connection.SummaryJson`, the `Connections.razor` two-pane layout (table + conditional side panel) replacing the modal dialog, and `AwsConnectionFields.razor` as the first (only) adopter.
- The two deviations from the original spec and why (no SDK-level base class; no `MudDrawer`) — copy the reasoning from this plan's Global Constraints section, since design.md is the durable record and this plan file is not guaranteed to be read again.
- What's still out of scope, carried over verbatim from the spec's §8: SNS/Topics, persisted denied-action enforcement, IAM Policy Simulator, a host-level `Region` field, Service Bus/Kafka adopting either new hook.
- A line for §8 (Testing) noting that `ConnectionEditor`/`AwsConnectionFields`/the Connections page are covered by bUnit only (already true of every other Blazor component in this codebase — this isn't a new deferral, just confirming the pattern extends here too).

Do not use placeholder text — write the actual final paragraphs, following §6.7's existing prose style and level of detail (read it first, in Step 1, and match it).

- [ ] **Step 3: Commit**

```bash
git add docs/design.md
git commit -m "docs: reconcile design.md with the connections-page redesign"
```

---

## Final gate

- [ ] Run `dotnet build -warnaserror` from the repo root — expect 0 warnings, 0 errors.
- [ ] Run `dotnet test` from the repo root — expect every test in every project to pass.
- [ ] Run `git status --short` — expect only files this plan intentionally changed (plus any pre-existing untouched files the session started with, which must remain untouched).
