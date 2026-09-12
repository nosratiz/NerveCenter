# Service Bus Plugin — Queues Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first real SbConsole plugin — Azure Service Bus queue management (list, create, delete, peek, send, dead-letter resubmit/purge) — plus the two SDK-completion gaps this plugin exposes (plugin page routing was never wired up; `IAuditScope` was never implemented) and connection reachability testing.

**Architecture:** `SbConsole.Plugins.ServiceBus` is a compile-time-registered `IPlugin` whose Azure SDK calls go through a substitutable `IServiceBusOperations` interface (unit-testable without a network call). Plugin handlers mirror Core's `XxxQueryHandler`/`XxxCommandHandler` pattern. Two host-side gaps found while designing this get fixed as part of it: `Routes.razor`/`Program.cs` never scanned plugin assemblies for routable `@page` components (so no plugin page could ever be reached), and `IAuditScope` was declared in the SDK but never implemented or registered.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor, `Azure.Messaging.ServiceBus`, EF Core + SQLite, xUnit + FluentAssertions 7.x + NSubstitute + bUnit.

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (already enforced via `Directory.Build.props`).
- No MediatR, no controllers, no second component library, no WebAssembly.
- Gate before every commit: `dotnet build -warnaserror && dotnet test` both green.
- `SbConsole.Sdk` version bumps to **1.2.0** — already reflected in `docs/design.md` §3.
- `Azure.Messaging.ServiceBus` is added ONLY to `SbConsole.Plugins.ServiceBus` — no other project references it.
- `IServiceBusOperations` methods take the connection string as a parameter (never pre-configured at construction) — one instance tests and operates against whatever connection the caller names.
- **Testing strategy for this plan is unit tests only** — no Testcontainers, no real AMQP traffic, no Docker. `AzureServiceBusOperations`'s own methods get light coverage by necessity (they can't be meaningfully unit-tested without a real/emulated broker); the substitute of `IServiceBusOperations` is where the real test leverage is. This is deliberate, not a shortcut — see `docs/design.md` §6/§8.
- Commands write audit rows (host commands via `IAuditWriter`; plugin commands via `IAuditScope`); queries never write.
- Destructive actions (delete queue, purge dead-letter) go through `IConfirmationService` exactly like host actions do — plugins consume it by injection, never referencing `SbConsole.Web`.
- Actor resolution in Razor components: `ActorResolver.ResolveAsync(AuthState)` (already exists, `src/SbConsole.Web/Auth/ActorResolver.cs`) — reuse it, don't reinvent it.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

---

### Task 1: SDK v1.2 — remove `RootComponent`, add `TestConnectionAsync`/`ConnectionTestResult`

**Files:**
- Modify: `src/SbConsole.Sdk/IPlugin.cs`
- Create: `src/SbConsole.Sdk/ConnectionTestResult.cs`
- Modify: `tests/SbConsole.Web.Tests/NavMenuTests.cs`, `tests/SbConsole.Core.Tests/Plugins/ListPluginsQueryHandlerTests.cs`, `tests/SbConsole.Web.Tests/AddEditConnectionDialogTests.cs`, `tests/SbConsole.Web.Tests/PluginsPageTests.cs` (each has a `FakePlugin : IPlugin` test double that must drop `RootComponent` and add `TestConnectionAsync` or the build breaks)
- Test: `tests/SbConsole.Core.Tests/Sdk/ConnectionTestResultTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by every later task): `IPlugin` without `RootComponent`, with `Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)`. `sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Core.Tests/Sdk/ConnectionTestResultTests.cs
using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ConnectionTestResultTests
{
    [Fact]
    public void Success_result_has_no_error_message_by_default()
    {
        var result = new ConnectionTestResult(Success: true);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failure_result_carries_an_error_message()
    {
        var result = new ConnectionTestResult(Success: false, ErrorMessage: "Unauthorized (401)");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Unauthorized (401)");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ConnectionTestResultTests`
Expected: FAIL to compile — `ConnectionTestResult` does not exist.

- [ ] **Step 3: Write the SDK type and update IPlugin**

```csharp
// src/SbConsole.Sdk/ConnectionTestResult.cs
namespace SbConsole.Sdk;

/// <summary>Outcome of IPlugin.TestConnectionAsync — shown in the Connections page's Status column.</summary>
public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null);
```

Modify `src/SbConsole.Sdk/IPlugin.cs` — remove the `RootComponent` property (it was never wired to anything in the host), and add `TestConnectionAsync`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace SbConsole.Sdk;

public interface IPlugin
{
    /// <summary>Stable, URL-safe identifier, e.g. "servicebus". Used in routes (/p/{Id}/...) and storage scoping.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    IReadOnlyList<PluginNavItem> NavItems { get; }

    /// <summary>Connection kind this plugin's connections use, e.g. "azure-servicebus". Matches Connection.Kind.</summary>
    string ConnectionKind { get; }

    /// <summary>Shown in the host's Add/Edit Connection "Kind" dropdown.</summary>
    string ConnectionKindDisplayName { get; }

    /// <summary>Static summary shown on the host's Plugins page.</summary>
    PluginContribution Contribution { get; }

    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Verifies a saved connection of this plugin's ConnectionKind actually works, using
    /// whatever protocol that connection kind speaks. Called with the connection's decrypted
    /// secret — never the connection ID, so this has no dependency on the host's DbContext.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
}
```

- [ ] **Step 4: Fix the four broken FakePlugin test doubles**

Each of these files has a `FakePlugin : IPlugin` (or similarly named) test double. Read each file first, then remove its `RootComponent` property and add a `TestConnectionAsync` implementation returning a trivial success result:

```csharp
// add to each FakePlugin, replacing its "public Type RootComponent => typeof(object);" line:
public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
    Task.FromResult(new ConnectionTestResult(Success: true));
```

Add `using SbConsole.Sdk;` to any of the four files that doesn't already have it (all four already reference `IPlugin` from that namespace, so this should already be present — just confirm).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build -warnaserror && dotnet test`
Expected: full solution builds; `ConnectionTestResultTests` passes (2 tests); all four previously-broken test files compile and pass again.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: bump SbConsole.Sdk to 1.2.0 — remove unused RootComponent, add TestConnectionAsync"
```

---

### Task 2: EfAuditScope — host implementation of IAuditScope

**Files:**
- Create: `src/SbConsole.Core/Audit/EfAuditScope.cs`
- Modify: `src/SbConsole.Web/Program.cs` (register `IAuditScope`)
- Test: `tests/SbConsole.Core.Tests/Audit/EfAuditScopeTests.cs`

**Interfaces:**
- Consumes: `IAuditWriter` (Plan 1 Task 7, unchanged), `AuditEntry` entity, `ActionRisk` (Sdk), `TestDb`.
- Produces (used by every plugin handler that writes audit rows, starting Task 8 onward): `EfAuditScope(IAuditWriter, TimeProvider, Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider) : IAuditScope` — `RecordAsync(action, target, risk, succeeded, detail, ct)` resolves the actor via `authStateProvider.GetAuthenticationStateAsync()` (falling back to `"admin"` if the identity name is null, matching `ActorResolver`'s convention) and writes one `AuditEntry`.

**Why this exists:** `IAuditScope` was declared in the SDK back in Plan 1 but never implemented or registered — a contract with no consumer to prove it out until this plugin needed one. This task closes that gap.

- [ ] **Step 1: Add the required package**

`Microsoft.AspNetCore.Components.Authorization` (for `AuthenticationStateProvider`) — check if `SbConsole.Core` already references it before adding; if not:

```bash
dotnet add src/SbConsole.Core package Microsoft.AspNetCore.Components.Authorization
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Audit/EfAuditScopeTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class EfAuditScopeTests
{
    private static AuthenticationStateProvider AuthProviderFor(string? userName)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        var identity = userName is null
            ? new System.Security.Claims.ClaimsIdentity()
            : new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName)],
                "test");
        var state = new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identity));
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    [Fact]
    public async Task Records_an_audit_entry_with_the_current_users_name()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor("alice"));

        await scope.RecordAsync("queue.create", "orders-inbound", ActionRisk.Mutating, succeeded: true);

        await using var db = testDb.CreateDbContext();
        var entry = await db.AuditEntries.SingleAsync();
        entry.Actor.Should().Be("alice");
        entry.Action.Should().Be("queue.create");
        entry.Target.Should().Be("orders-inbound");
        entry.Risk.Should().Be(ActionRisk.Mutating);
        entry.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Falls_back_to_admin_when_the_identity_has_no_name()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor(null));

        await scope.RecordAsync("queue.peek", "orders-inbound", ActionRisk.Safe, succeeded: true);

        await using var db = testDb.CreateDbContext();
        (await db.AuditEntries.SingleAsync()).Actor.Should().Be("admin");
    }

    [Fact]
    public async Task Records_the_detail_and_failure_outcome()
    {
        using var testDb = new TestDb();
        var scope = new EfAuditScope(new EfAuditWriter(testDb), new FakeTimeProvider(), AuthProviderFor("alice"));

        await scope.RecordAsync("queue.purge", "orders-inbound/$deadletter", ActionRisk.Destructive, succeeded: false, detail: "confirmation mismatch");

        await using var db = testDb.CreateDbContext();
        var entry = await db.AuditEntries.SingleAsync();
        entry.Succeeded.Should().BeFalse();
        entry.Detail.Should().Be("confirmation mismatch");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfAuditScopeTests`
Expected: FAIL to compile — `EfAuditScope` does not exist.

- [ ] **Step 4: Write the implementation**

```csharp
// src/SbConsole.Core/Audit/EfAuditScope.cs
using Microsoft.AspNetCore.Components.Authorization;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Audit;

/// <summary>
/// Host implementation of the Sdk's IAuditScope. Injected into plugin handler classes (plain
/// classes, not components), so — unlike the Razor-component-facing ActorResolver, which reads
/// the AuthenticationState cascading parameter it's handed — this resolves the actor itself via
/// AuthenticationStateProvider.
/// </summary>
public sealed class EfAuditScope(
    IAuditWriter writer,
    TimeProvider clock,
    AuthenticationStateProvider authStateProvider) : IAuditScope
{
    public async Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default)
    {
        var state = await authStateProvider.GetAuthenticationStateAsync();
        var actor = state.User.Identity?.Name ?? "admin";

        await writer.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = actor,
            Action = action,
            Target = target,
            Risk = risk,
            Succeeded = succeeded,
            Detail = detail,
        }, ct);
    }
}
```

- [ ] **Step 5: Register it in Program.cs**

Modify `src/SbConsole.Web/Program.cs` — add alongside the other Core service registrations (near `builder.Services.AddSingleton<IAuditWriter, EfAuditWriter>();`):

```csharp
builder.Services.AddScoped<IAuditScope, EfAuditScope>();
```

(`AddScoped` because `AuthenticationStateProvider` is itself scoped per Blazor circuit.) Add `using SbConsole.Core.Audit;` if not already present (it already is, per the existing `IAuditWriter` line).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfAuditScopeTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: implement IAuditScope (EfAuditScope), register in DI"
```

---

### Task 3: Connection reachability schema + `ConnectionInfo` extension + `TestConnectionCommandHandler`

**Files:**
- Modify: `src/SbConsole.Core/Data/Entities/Connection.cs`, `src/SbConsole.Core/Data/SbcDbContext.cs`, `src/SbConsole.Sdk/ConnectionInfo.cs`, `src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs`, `src/SbConsole.Core/Connections/EfConnectionProvider.cs`
- Create: `src/SbConsole.Core/Migrations/` (generated), `src/SbConsole.Core/Connections/TestConnectionCommandHandler.cs`
- Test: `tests/SbConsole.Core.Tests/Connections/TestConnectionCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IPlugin.TestConnectionAsync`/`ConnectionTestResult` (Task 1), `ISecretProtector`, `IAuditWriter`, `TestDb`.
- Produces (used by Task 4's Connections page): `ConnectionInfo` gains three OPTIONAL trailing parameters (existing positional-constructor call sites are unaffected): `bool? LastTestSucceeded = null, DateTimeOffset? LastTestedAt = null, string? LastTestError = null`. `record TestConnectionCommand(Guid ConnectionId, string Actor)`. `TestConnectionCommandHandler(IDbContextFactory<SbcDbContext>, ISecretProtector, IEnumerable<IPlugin>, IAuditWriter, TimeProvider)` with `Task<Result<ConnectionTestResult>> HandleAsync(TestConnectionCommand cmd, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Connections/TestConnectionCommandHandlerTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Connections;

public class TestConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private sealed class FakePlugin(string kind, ConnectionTestResult result, string? expectedSecret = null) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => kind;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }

        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
        {
            if (expectedSecret is not null)
            {
                secret.Should().Be(expectedSecret);
            }

            return Task.FromResult(result);
        }
    }

    private static async Task<Guid> SeedAsync(TestDb db, IAuditWriter audit, string kind = "azure-servicebus", string secret = "Endpoint=sb://x")
    {
        var create = new CreateConnectionCommandHandler(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider());
        var result = await create.HandleAsync(new CreateConnectionCommand("bus", kind, secret, [], "admin"));
        return result.Value;
    }

    [Fact]
    public async Task Successful_test_persists_success_and_audits_as_safe()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit, secret: "Endpoint=sb://real");
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(true), expectedSecret: "Endpoint=sb://real");
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider());

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.LastTestSucceeded.Should().BeTrue();
        saved.LastTestedAt.Should().NotBeNull();
        saved.LastTestError.Should().BeNull();
        await audit.Received(1).WriteAsync(
            Arg.Is<Data.Entities.AuditEntry>(a => a.Action == "connection.test" && a.Risk == ActionRisk.Safe && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_test_persists_the_error_and_audits_as_failed()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit);
        var plugin = new FakePlugin("azure-servicebus", new ConnectionTestResult(false, "Unauthorized (401)"));
        var handler = new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [plugin], audit, new FakeTimeProvider());

        var result = await handler.HandleAsync(new TestConnectionCommand(id, "admin"));

        result.IsSuccess.Should().BeTrue(); // the COMMAND succeeded (it ran); the TEST itself failed
        result.Value!.Success.Should().BeFalse();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == id);
        saved.LastTestSucceeded.Should().BeFalse();
        saved.LastTestError.Should().Be("Unauthorized (401)");
        await audit.Received(1).WriteAsync(
            Arg.Is<Data.Entities.AuditEntry>(a => a.Action == "connection.test" && a.Risk == ActionRisk.Safe && !a.Succeeded && a.Detail == "Unauthorized (401)"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_not_found()
    {
        using var testDb = new TestDb();

        var result = await new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [], Substitute.For<IAuditWriter>(), new FakeTimeProvider())
            .HandleAsync(new TestConnectionCommand(Guid.NewGuid(), "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
    }

    [Fact]
    public async Task No_plugin_registered_for_the_connections_kind_returns_not_found()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var id = await SeedAsync(testDb, audit, kind: "kafka");

        var result = await new TestConnectionCommandHandler(testDb, new AesGcmSecretProtector(Key), [], audit, new FakeTimeProvider())
            .HandleAsync(new TestConnectionCommand(id, "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter TestConnectionCommandHandlerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Add the entity columns and value converter**

Modify `src/SbConsole.Core/Data/Entities/Connection.cs`:

```csharp
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

    public IReadOnlyList<string> Tags => TagsCsv.Length == 0 ? [] : TagsCsv.Split(',');
}
```

Modify `src/SbConsole.Core/Data/SbcDbContext.cs`'s `Connection` configuration — add the same UTC-ticks value converter `AuditEntry.At` already uses (learned the hard way in an earlier plan: EF Core 10 + SQLite cannot translate `WHERE`/`ORDER BY` over a raw `DateTimeOffset` column at all; applying it now, on a brand-new nullable column with no existing data to convert, avoids hitting that trap later for free):

```csharp
builder.Entity<Connection>(e =>
{
    e.HasKey(c => c.Id);
    e.HasIndex(c => c.Name).IsUnique();
    e.Ignore(c => c.Tags);
    // Same UTC-ticks conversion as AuditEntry.At (see its comment) — pre-empting the identical
    // EF Core 10 + SQLite DateTimeOffset translation trap on a brand-new column, before any data exists.
    e.Property(c => c.LastTestedAt).HasConversion(
        v => v.HasValue ? v.Value.UtcTicks : (long?)null,
        v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
});
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddConnectionTestResultColumns --project src/SbConsole.Core
```

Expected: a new migration file appears under `src/SbConsole.Core/Migrations/` adding three nullable columns (`LastTestSucceeded` BOOLEAN, `LastTestedAt` INTEGER, `LastTestError` TEXT) to `Connections`. No backfill SQL is needed — unlike the earlier `AuditEntry.At` migration, this adds brand-new columns with no pre-existing data to convert; every existing row simply gets `NULL` in all three, which is exactly the "Never tested" state.

- [ ] **Step 5: Extend ConnectionInfo**

Modify `src/SbConsole.Sdk/ConnectionInfo.cs` — add the three fields as trailing optional parameters (existing callers like `new ConnectionInfo(id, name, kind, tags)` keep compiling unchanged):

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
    string? LastTestError = null)
{
    public bool IsProd => Tags.Contains("prod", StringComparer.OrdinalIgnoreCase);
}
```

Modify `src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs`'s projection line to pass the new fields through:

```csharp
return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags, c.LastTestSucceeded, c.LastTestedAt, c.LastTestError)).ToList();
```

Modify `src/SbConsole.Core/Connections/EfConnectionProvider.cs`'s equivalent projection line the same way, for consistency (plugins consuming `IConnectionProvider` don't need these fields today, but there's no reason the two `ConnectionInfo` construction sites should diverge in what they populate).

- [ ] **Step 6: Write TestConnectionCommandHandler**

```csharp
// src/SbConsole.Core/Connections/TestConnectionCommandHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record TestConnectionCommand(Guid ConnectionId, string Actor);

public sealed class TestConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IEnumerable<IPlugin> plugins,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<ConnectionTestResult>> HandleAsync(TestConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.ConnectionId, ct);
        if (connection is null)
        {
            return Result<ConnectionTestResult>.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        var plugin = plugins.FirstOrDefault(p => p.ConnectionKind == connection.Kind);
        if (plugin is null)
        {
            return Result<ConnectionTestResult>.Fail(ErrorCategory.NotFound, $"No plugin registered for connection kind '{connection.Kind}'.");
        }

        var secret = protector.Unprotect(connection.SecretCiphertext);
        var testResult = await plugin.TestConnectionAsync(secret, ct);

        connection.LastTestSucceeded = testResult.Success;
        connection.LastTestedAt = clock.GetUtcNow();
        connection.LastTestError = testResult.ErrorMessage;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.test",
            Target = connection.Name,
            Risk = ActionRisk.Safe,
            Succeeded = testResult.Success,
            Detail = testResult.ErrorMessage,
        }, ct);

        return Result<ConnectionTestResult>.Ok(testResult);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter TestConnectionCommandHandlerTests`
Expected: PASS (4 tests).

- [ ] **Step 8: Register the handler in Program.cs — do this now, not in a later task**

A prior plan's final review found a Critical bug: handlers created in one task but not registered in `src/SbConsole.Web/Program.cs` until much later caused real HTTP 500s in the running app despite every unit test passing (each test's own DI container registered the handler itself, masking the gap). To not repeat that, register every new handler in `Program.cs` in the SAME task that creates it — never defer this to the task that builds the UI consuming it.

Modify `src/SbConsole.Web/Program.cs` — add alongside the other connection handler registrations:

```csharp
builder.Services.AddScoped<TestConnectionCommandHandler>();
```

- [ ] **Step 9: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green (confirms `ConnectionInfo`'s trailing-optional-parameter change didn't break any existing call site).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: add connection reachability schema and TestConnectionCommandHandler"
```

---

### Task 4: Connections page — real status display + "Test" action

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Connections.razor`
- Test: `tests/SbConsole.Web.Tests/ConnectionsPageTests.cs` (extend the existing file — read it first)

**Interfaces:**
- Consumes: `TestConnectionCommandHandler`/`TestConnectionCommand` (Task 3), `ConnectionInfo`'s new `LastTestSucceeded`/`LastTestedAt`/`LastTestError` fields (Task 3).
- Produces: the Connections page's Status column reflects real, persisted test results instead of the static "Untested" string.

- [ ] **Step 1: Write the failing test**

Read the current `tests/SbConsole.Web.Tests/ConnectionsPageTests.cs` first (it already has a working DI setup with `TestDb`, `IAuditWriter`, `AesGcmSecretProtector`, `FakeTimeProvider`, `CreateConnectionCommandHandler`, etc. — reuse that exact setup). Add `IEnumerable<IPlugin>` and `TestConnectionCommandHandler` to the constructor's DI registrations, and add these tests:

```csharp
// add to the existing ConnectionsPageTests class

private sealed class FakeServiceBusPlugin(ConnectionTestResult result) : IPlugin
{
    public string Id => "azure-servicebus";
    public string DisplayName => "Azure Service Bus";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems => [];
    public string ConnectionKind => "azure-servicebus";
    public string ConnectionKindDisplayName => "Azure Service Bus";
    public PluginContribution Contribution => new(0, 0);
    public void ConfigureServices(IServiceCollection services) { }
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) => Task.FromResult(result);
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
public async Task Test_button_runs_the_test_and_updates_the_status_to_ok()
{
    Services.AddSingleton<IEnumerable<IPlugin>>([new FakeServiceBusPlugin(new ConnectionTestResult(true))]);
    Services.AddSingleton<TestConnectionCommandHandler>();
    await Services.GetRequiredService<CreateConnectionCommandHandler>()
        .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

    var cut = Render<Connections>();
    cut.Find("button.test-connection").Click();
    await Task.Delay(50);
    cut.Render();

    cut.Markup.Should().Contain("OK");
    cut.Markup.Should().NotContain("Never tested");
}

[Fact]
public async Task Test_button_shows_the_error_message_on_failure()
{
    Services.AddSingleton<IEnumerable<IPlugin>>([new FakeServiceBusPlugin(new ConnectionTestResult(false, "Unauthorized (401)"))]);
    Services.AddSingleton<TestConnectionCommandHandler>();
    await Services.GetRequiredService<CreateConnectionCommandHandler>()
        .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

    var cut = Render<Connections>();
    cut.Find("button.test-connection").Click();
    await Task.Delay(50);
    cut.Render();

    cut.Markup.Should().Contain("Unauthorized (401)");
}
```

Add `using SbConsole.Sdk;` and `using Microsoft.Extensions.DependencyInjection;` to the test file if not already present (they already are, per the existing `FakePlugin`-style doubles this file's siblings use).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ConnectionsPageTests`
Expected: FAIL — `button.test-connection` not found / `TestConnectionCommandHandler` not injected.

- [ ] **Step 3: Update Connections.razor**

Read the current file first (shown in full in this task's context below), then apply these changes: add `@inject TestConnectionCommandHandler TestHandler`, replace the static `<MudTd>Untested</MudTd>` with a real status expression, and add a "Test" button next to Edit/Delete:

```razor
@* src/SbConsole.Web/Components/Pages/Connections.razor *@
@page "/connections"
@using SbConsole.Core.Connections
@using SbConsole.Web.Auth
@using SbConsole.Web.Components.Connections
@inject ListConnectionsQueryHandler ListHandler
@inject DeleteConnectionCommandHandler DeleteHandler
@inject TestConnectionCommandHandler TestHandler
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
                <MudChip T="string" Color="@(string.Equals(tag, "prod", StringComparison.OrdinalIgnoreCase) ? Color.Warning : Color.Default)">@tag</MudChip>
            }
        </MudTd>
        <MudTd>@StatusText(context)</MudTd>
        <MudTd>
            <MudButton Class="test-connection" OnClick="@(() => TestAsync(context))">Test</MudButton>
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

    private static string StatusText(ConnectionInfo connection) => connection switch
    {
        { LastTestedAt: null } => "Never tested",
        { LastTestSucceeded: true } => "OK",
        { LastTestError: { } error } => error,
        _ => "Failed",
    };

    private async Task TestAsync(ConnectionInfo connection)
    {
        var actor = await ActorResolver.ResolveAsync(AuthState);
        await TestHandler.HandleAsync(new TestConnectionCommand(connection.Id, actor));
        await RefreshAsync();
    }

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

        var actor = await ActorResolver.ResolveAsync(AuthState);
        await DeleteHandler.HandleAsync(new DeleteConnectionCommand(connection.Id, actor));
        await RefreshAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ConnectionsPageTests`
Expected: PASS (all tests in the file, including the 3 new ones).

- [ ] **Step 5: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: wire real connection reachability status and Test action into Connections page"
```

---

### Task 5: `IServiceBusOperations` + DTOs + `AzureServiceBusOperations`

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`, `src/SbConsole.Plugins.ServiceBus/Client/QueueSummary.cs`, `src/SbConsole.Plugins.ServiceBus/Client/CreateQueueRequest.cs`, `src/SbConsole.Plugins.ServiceBus/Client/SendMessageRequest.cs`, `src/SbConsole.Plugins.ServiceBus/Client/PeekedMessage.cs`, `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Client/AzureServiceBusOperationsTests.cs`

**Interfaces:**
- Consumes: `ConnectionTestResult` (Sdk, Task 1), `Azure.Messaging.ServiceBus`.
- Produces (used by every later task in this plan): `IServiceBusOperations` with the methods below; `AzureServiceBusOperations : IServiceBusOperations`, the only real implementation.

**Read this before writing any code:** this task's real Azure SDK calls cannot be run against a live or emulated broker in this plan (§ Global Constraints — unit tests only). The exact method/property names below are written from careful knowledge of the `Azure.Messaging.ServiceBus` SDK but were not compiled against the actual installed package version. **Before treating any signature below as final, verify it against the installed package** (check `~/.nuget/packages/azure.messaging.servicebus/*/lib/*/Azure.Messaging.ServiceBus.dll` via decompilation or IntelliSense/`dotnet build` errors) the same way this project has repeatedly verified MudBlazor's actual API rather than trusting assumptions. A compiler error here is expected next-step information, not a sign the task is wrong — resolve it against the real API and keep going.

- [ ] **Step 1: Add the package**

```bash
dotnet add src/SbConsole.Plugins.ServiceBus package Azure.Messaging.ServiceBus
```

- [ ] **Step 2: Write the DTOs and interface**

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/QueueSummary.cs
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record QueueSummary(
    string Name,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long ScheduledMessageCount,
    long SizeInBytes);
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/CreateQueueRequest.cs
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateQueueRequest(
    string Name,
    int MaxDeliveryCount = 10,
    TimeSpan? LockDuration = null,
    TimeSpan? DefaultMessageTimeToLive = null);
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/SendMessageRequest.cs
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record SendMessageRequest(
    string Body,
    string ContentType = "application/json",
    IReadOnlyDictionary<string, string>? Properties = null,
    DateTimeOffset? ScheduledEnqueueTime = null);
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/PeekedMessage.cs
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record PeekedMessage(
    long SequenceNumber,
    string Body,
    string? ContentType,
    DateTimeOffset EnqueuedTime,
    int DeliveryCount,
    IReadOnlyDictionary<string, string> Properties,
    string? DeadLetterReason = null,
    string? DeadLetterErrorDescription = null);
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>
/// The seam between the plugin's handlers and the real Azure SDK. Every method takes the
/// connection string as a parameter — no instance is pre-configured for one connection — since
/// a single registered instance tests and operates against whatever connection the caller names.
/// </summary>
public interface IServiceBusOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default);

    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string connectionString, CancellationToken ct = default);

    Task CreateQueueAsync(string connectionString, CreateQueueRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteQueueAsync(string connectionString, string queueName, CancellationToken ct = default);

    /// <summary>Non-destructive. Set fromDeadLetter to browse the queue's dead-letter sub-queue instead.</summary>
    Task<IReadOnlyList<PeekedMessage>> PeekMessagesAsync(
        string connectionString, string queueName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default);

    Task SendMessageAsync(string connectionString, string queueName, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>Moves the named dead-lettered messages (by sequence number) back onto the main queue. Returns how many were actually found and resubmitted.</summary>
    Task<int> ResubmitDeadLetterMessagesAsync(string connectionString, string queueName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default);

    /// <summary>Destructive. Drains and discards every message currently in the queue's dead-letter sub-queue. Returns how many were purged.</summary>
    Task<int> PurgeDeadLetterMessagesAsync(string connectionString, string queueName, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the failing test**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Client/AzureServiceBusOperationsTests.cs
using FluentAssertions;
using SbConsole.Plugins.ServiceBus.Client;

namespace SbConsole.Plugins.ServiceBus.Tests.Client;

public class AzureServiceBusOperationsTests
{
    [Fact]
    public async Task TestConnectionAsync_returns_a_readable_failure_for_a_malformed_connection_string()
    {
        var operations = new AzureServiceBusOperations();

        var result = await operations.TestConnectionAsync("this-is-not-a-real-service-bus-connection-string");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TestConnectionAsync_never_throws_regardless_of_input()
    {
        var operations = new AzureServiceBusOperations();

        var act = async () => await operations.TestConnectionAsync("");

        await act.Should().NotThrowAsync();
    }
}
```

Delete `tests/SbConsole.Plugins.ServiceBus.Tests/UnitTest1.cs` now (the Plan 1 scaffold placeholder) if it's still present.

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter AzureServiceBusOperationsTests`
Expected: FAIL to compile — `AzureServiceBusOperations` does not exist.

- [ ] **Step 5: Write AzureServiceBusOperations**

```csharp
// src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>
/// The only real implementation of IServiceBusOperations. Its own methods get light test
/// coverage by necessity — they can't be meaningfully unit-tested without a real or emulated
/// broker (docs/design.md §6/§8) — the substitutable interface is where the test leverage is.
/// </summary>
public sealed class AzureServiceBusOperations : IServiceBusOperations
{
    public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(connectionString);
            await foreach (var _ in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
            {
                break; // one item (or a confirmed-empty-but-authenticated page) is enough
            }

            return new ConnectionTestResult(true);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            return new ConnectionTestResult(false, $"Unauthorized ({ex.Status})");
        }
        catch (Exception ex)
        {
            // Covers malformed connection strings (thrown synchronously at client construction,
            // before any network call) and every other reachability/auth failure. The SDK's exact
            // exception type for a bad connection string isn't load-bearing here — every failure
            // path becomes a readable ConnectionTestResult, never an unhandled throw.
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string connectionString, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        var queues = new List<QueueSummary>();
        await foreach (var props in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
        {
            queues.Add(new QueueSummary(props.Name, props.ActiveMessageCount, props.DeadLetterMessageCount, props.ScheduledMessageCount, props.SizeInBytes));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string connectionString, CreateQueueRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        var options = new CreateQueueOptions(request.Name) { MaxDeliveryCount = request.MaxDeliveryCount };
        if (request.LockDuration is { } lockDuration)
        {
            options.LockDuration = lockDuration;
        }

        if (request.DefaultMessageTimeToLive is { } ttl)
        {
            options.DefaultMessageTimeToLive = ttl;
        }

        await adminClient.CreateQueueAsync(options, ct);
    }

    public async Task DeleteQueueAsync(string connectionString, string queueName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        await adminClient.DeleteQueueAsync(queueName, ct);
    }

    public async Task<IReadOnlyList<PeekedMessage>> PeekMessagesAsync(
        string connectionString, string queueName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = fromDeadLetter ? SubQueue.DeadLetter : SubQueue.None };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);

        var received = fromSequenceNumber is { } seq
            ? await receiver.PeekMessagesAsync(maxMessages, seq, ct)
            : await receiver.PeekMessagesAsync(maxMessages, cancellationToken: ct);

        return received.Select(m => new PeekedMessage(
            m.SequenceNumber,
            m.Body.ToString(),
            m.ContentType,
            m.EnqueuedTime,
            m.DeliveryCount,
            m.ApplicationProperties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
            m.DeadLetterReason,
            m.DeadLetterErrorDescription)).ToList();
    }

    public async Task SendMessageAsync(string connectionString, string queueName, SendMessageRequest request, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        await using var sender = client.CreateSender(queueName);

        var message = new ServiceBusMessage(request.Body) { ContentType = request.ContentType };
        if (request.Properties is not null)
        {
            foreach (var (key, value) in request.Properties)
            {
                message.ApplicationProperties[key] = value;
            }
        }

        if (request.ScheduledEnqueueTime is { } scheduled)
        {
            await sender.ScheduleMessageAsync(message, scheduled, ct);
        }
        else
        {
            await sender.SendMessageAsync(message, ct);
        }
    }

    public async Task<int> ResubmitDeadLetterMessagesAsync(string connectionString, string queueName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default)
    {
        if (sequenceNumbers.Count == 0)
        {
            return 0;
        }

        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);
        await using var sender = client.CreateSender(queueName);

        var remaining = new HashSet<long>(sequenceNumbers);
        var resubmitted = 0;
        // Bounded scan: keep receiving batches until every requested sequence number has been
        // found or the dead-letter queue is exhausted, so resubmitting a handful of messages out
        // of a much larger dead-letter queue can't loop forever.
        var maxAttempts = sequenceNumbers.Count * 4 + 10;
        for (var attempt = 0; attempt < maxAttempts && remaining.Count > 0; attempt++)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(5), ct);
            if (batch.Count == 0)
            {
                break; // dead-letter queue exhausted before every requested message was found
            }

            foreach (var message in batch)
            {
                if (remaining.Remove(message.SequenceNumber))
                {
                    await sender.SendMessageAsync(new ServiceBusMessage(message), ct);
                    await receiver.CompleteMessageAsync(message, ct);
                    resubmitted++;
                }
                else
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken: ct);
                }
            }
        }

        return resubmitted;
    }

    public async Task<int> PurgeDeadLetterMessagesAsync(string connectionString, string queueName, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);

        var purged = 0;
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(3), ct);
            if (batch.Count == 0)
            {
                break;
            }

            purged += batch.Count;
        }

        return purged;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter AzureServiceBusOperationsTests`
Expected: PASS (2 tests). If the build fails on an SDK API mismatch, resolve it against the installed package's real API per the note above, then re-run.

- [ ] **Step 7: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add IServiceBusOperations and the real Azure SDK implementation"
```

---

### Task 6: `ServiceBusPlugin` + routing fix + registration + Queues stub (end-to-end smoke slice)

This is the highest-risk task in the plan — it proves the routing fix, the plugin registration, and per-page auth enforcement all work together, before any later task builds real functionality on top. Do not skip or shortcut the smoke-test step.

**Files:**
- Modify: `src/SbConsole.Web/Components/Routes.razor`, `src/SbConsole.Web/Program.cs`, `src/SbConsole.Web/Components/Layout/NavMenu.razor`
- Create: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, `src/SbConsole.Plugins.ServiceBus/Pages/_Imports.razor`, `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`

**Interfaces:**
- Consumes: `IPlugin`/`PluginContribution`/`ConnectionTestResult` (Task 1), `IServiceBusOperations`/`AzureServiceBusOperations` (Task 5), `PluginRegistry` (existing, `src/SbConsole.Web/Plugins/PluginRegistry.cs`).
- Produces: a routable, authenticated, real page at `/p/azure-servicebus/queues`; every later task in this plan builds inside `Queues.razor` and `SbConsole.Plugins.ServiceBus`'s DI registrations on top of what this task establishes.

**A one-line pre-existing bug fixed here too:** `NavMenu.razor` renders `Dashboard`/`Connections`/`Audit`/`Settings` links but is missing `Plugins` (the read-only Plugins page from an earlier plan has no nav link to it at all — reachable only by typing the URL). Fixed alongside this task since verifying `NavMenu` renders correctly is already part of this task's smoke test.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
using FluentAssertions;
using SbConsole.Plugins.ServiceBus;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests;

public class ServiceBusPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new ServiceBusPlugin();

        plugin.Id.Should().Be("azure-servicebus");
        plugin.ConnectionKind.Should().Be("azure-servicebus");
        plugin.DisplayName.Should().Be("Azure Service Bus");
        plugin.ConnectionKindDisplayName.Should().Be("Azure Service Bus");
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Queues" && n.Href == "/p/azure-servicebus/queues");
    }

    [Fact]
    public async Task TestConnectionAsync_delegates_to_the_real_Azure_SDK_and_never_throws()
    {
        var plugin = new ServiceBusPlugin();

        var result = await plugin.TestConnectionAsync("not-a-real-connection-string");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter ServiceBusPluginTests`
Expected: FAIL to compile — `ServiceBusPlugin` does not exist.

- [ ] **Step 3: Write ServiceBusPlugin**

```csharp
// src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

public sealed class ServiceBusPlugin : IPlugin
{
    public string Id => "azure-servicebus";
    public string DisplayName => "Azure Service Bus";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/azure-servicebus/queues")];
    public string ConnectionKind => "azure-servicebus";
    public string ConnectionKindDisplayName => "Azure Service Bus";

    // Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter.
    public PluginContribution Contribution => new(PageCount: 1, ActionCount: 6);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServiceBusOperations, AzureServiceBusOperations>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IServiceBusOperations from at
    // this layer — construct the real implementation directly, same as any other plugin would.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new AzureServiceBusOperations().TestConnectionAsync(secret, ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter ServiceBusPluginTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Add the plugin's Pages `_Imports.razor` with `[Authorize]`**

`src/SbConsole.Web/Components/Pages/_Imports.razor` carries `@attribute [Authorize]`, but that only applies within `SbConsole.Web`'s own `Components/Pages/` folder — directory-scoped Razor directives don't cross project boundaries. Without an equivalent here, `Queues.razor` (and every future page this plugin adds) would NOT be protected by `AuthorizeRouteView`, even though the app-wide fallback authorization policy exists — this is exactly the same class of gap Plan 1 had to fix for the host's own pages (see `git log` for `81bda6b`).

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/_Imports.razor *@
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize]
```

- [ ] **Step 6: Add the Queues stub page**

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor *@
@page "/p/azure-servicebus/queues"

<PageTitle>Queues</PageTitle>
<h1>Queues</h1>
```

(Plain `<h1>` rather than `MudText` for now — this stub only needs to prove routing works; Task 7 replaces this with the real page. Using `<h1>` also finally gives `Routes.razor`'s `<FocusOnNavigate Selector="h1">` something to focus on this route, which no host page currently provides either — not fixed here, out of scope, but worth knowing.)

- [ ] **Step 7: Wire the routing fix into Routes.razor**

Modify `src/SbConsole.Web/Components/Routes.razor` (currently a router with no `@code` block or injected services):

```razor
@inject SbConsole.Web.Plugins.PluginRegistry Registry

<Router AppAssembly="typeof(Program).Assembly" AdditionalAssemblies="_pluginAssemblies" NotFoundPage="typeof(Pages.NotFound)">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <NotAuthorized>
                <RedirectToLogin />
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>

@code {
    private IEnumerable<System.Reflection.Assembly> _pluginAssemblies = [];

    protected override void OnInitialized()
    {
        _pluginAssemblies = Registry.Plugins.Select(p => p.GetType().Assembly).Distinct();
    }
}
```

- [ ] **Step 8: Wire the routing fix into Program.cs, register the plugin, add the missing Plugins nav link**

Read the current `src/SbConsole.Web/Program.cs` first. Add the plugin registration alongside the other service registrations (near the `IConfirmationService` line):

```csharp
builder.Services.AddSbConsolePlugin<SbConsole.Plugins.ServiceBus.ServiceBusPlugin>();
```

Find the existing `var app = builder.Build();` line and the `app.MapRazorComponents<App>().AddInteractiveServerRenderMode();` line near the end of the file. Between them (after the migration-running scope block, before `app.UseAntiforgery();`), resolve the plugin assemblies and pass them to `MapRazorComponents`:

```csharp
var pluginAssemblies = app.Services.GetRequiredService<SbConsole.Web.Plugins.PluginRegistry>().Plugins
    .Select(p => p.GetType().Assembly)
    .Distinct()
    .ToArray();
```

Change the existing:
```csharp
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```
to:
```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(pluginAssemblies);
```

Add `using Microsoft.Extensions.DependencyInjection;` and `using SbConsole.Web.Plugins;` at the top if not already present (check first — `PluginRegistry` is already constructed in this file, so `SbConsole.Web.Plugins` is very likely already imported; if so, drop the `SbConsole.Web.Plugins.` prefix above and just write `PluginRegistry`).

Modify `src/SbConsole.Web/Components/Layout/NavMenu.razor` — add the missing Plugins link between Audit and Settings:

```razor
<MudNavLink Href="/audit" Icon="@Icons.Material.Filled.History">Audit</MudNavLink>
<MudNavLink Href="/plugins" Icon="@Icons.Material.Filled.Extension">Plugins</MudNavLink>
<MudNavLink Href="/settings" Icon="@Icons.Material.Filled.Settings">Settings</MudNavLink>
```

- [ ] **Step 9: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 10: Smoke-test the whole slice — do not skip this**

```bash
SBC_DB_PATH=/tmp/sbc-sb-smoke.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8

echo "--- anonymous requests must still redirect ---"
curl -s -o /dev/null -w "queues-anon:%{http_code}\n" http://localhost:5080/p/azure-servicebus/queues

echo "--- log in and capture the session cookie ---"
curl -s -c /tmp/sbc-smoke-cookies.txt -o /dev/null -w "login:%{http_code}\n" -d "password=dev" http://localhost:5080/auth/login

echo "--- authenticated requests ---"
curl -s -b /tmp/sbc-smoke-cookies.txt -o /dev/null -w "queues:%{http_code}\n" http://localhost:5080/p/azure-servicebus/queues
curl -s -b /tmp/sbc-smoke-cookies.txt http://localhost:5080/p/azure-servicebus/queues | grep -o "Queues" | head -1
curl -s -b /tmp/sbc-smoke-cookies.txt http://localhost:5080/plugins | grep -o "Azure Service Bus" | head -1
curl -s -b /tmp/sbc-smoke-cookies.txt http://localhost:5080/ | grep -o "Azure Service Bus" | head -1

kill %1
rm -f /tmp/sbc-smoke-cookies.txt
```

Expected: `queues-anon:302` (proves `[Authorize]` from Step 5 is actually enforced — this is the check that matters most, since an unauthenticated plugin page reachable without login would be a real security gap, not a cosmetic one); `login:302`; `queues:200`; the page body contains `Queues`; `/plugins` contains `Azure Service Bus` (proves plugin registration + the existing Plugins page work end to end); the Dashboard page (`/`) also contains `Azure Service Bus` (proves `NavMenu` renders the plugin's nav group, since `NavMenu` is part of every authenticated page's `MainLayout`).

If `queues-anon` is NOT `302` (e.g. it's `200`), STOP — Step 5's `_Imports.razor` isn't taking effect. Do not proceed to later tasks with an unauthenticated plugin page.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: register ServiceBusPlugin, wire plugin-assembly routing, add missing Plugins nav link"
```

---

### Task 7: Queues page — list, create, delete

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/PluginResult.cs`, `src/SbConsole.Plugins.ServiceBus/Queues/ListQueuesQueryHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Queues/CreateQueueCommandHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Queues/DeleteQueueCommandHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Pages/CreateQueueDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor` (replace the Task 6 stub), `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (register the new handlers), `src/SbConsole.Web/Program.cs` (register `IConnectionProvider` consumer wiring — already registered; confirm no change needed there)
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Queues/ListQueuesQueryHandlerTests.cs`, `.../CreateQueueCommandHandlerTests.cs`, `.../DeleteQueueCommandHandlerTests.cs`, `tests/SbConsole.Web.Tests/Pages/QueuesPageTests.cs` — actually create this last one under `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs` since the component lives in the plugin project, not Web

**Interfaces:**
- Consumes: `IServiceBusOperations`/`QueueSummary`/`CreateQueueRequest` (Task 5), `IConnectionProvider`/`ConnectionInfo` (Sdk, existing), `IAuditScope` (Sdk, implemented Task 2), `IConfirmationService` (Sdk, existing).
- Produces (used by Tasks 8-9): `PluginResult`/`PluginResult<T>` — this plugin's own lightweight result type (plugins reference only `SbConsole.Sdk`, not `SbConsole.Core`, so Core's `Result<T>` isn't available here; this mirrors its shape without the cross-project dependency). `ListQueuesQueryHandler(IServiceBusOperations, IConnectionProvider)` with `Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)`. `record CreateQueueCommand(Guid ConnectionId, string ConnectionName, string QueueName, int MaxDeliveryCount)` — no `IsProd`; creation isn't a confirmation-gated action (only delete/purge are), so there's nothing for it to drive. `CreateQueueCommandHandler(IServiceBusOperations, IConnectionProvider, IAuditScope)` with `Task<PluginResult> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)`. `record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueName)`; `DeleteQueueCommandHandler(IServiceBusOperations, IConnectionProvider, IAuditScope)` with `Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)`.

- [ ] **Step 1: Write PluginResult**

```csharp
// src/SbConsole.Plugins.ServiceBus/PluginResult.cs
namespace SbConsole.Plugins.ServiceBus;

/// <summary>
/// This plugin's own lightweight result type, mirroring the shape of SbConsole.Core.Results.Result
/// without depending on it — plugins reference only SbConsole.Sdk (docs/design.md §2).
/// </summary>
public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error) => (IsSuccess, Error) = (isSuccess, error);
    public bool IsSuccess { get; }
    public string? Error { get; }
    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, T? value, string? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public static PluginResult<T> Ok(T value) => new(true, value, null);
    public static PluginResult<T> Fail(string error) => new(false, default, error);
}
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Queues/ListQueuesQueryHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class ListQueuesQueryHandlerTests
{
    [Fact]
    public async Task Returns_queues_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var queues = new[] { new QueueSummary("orders-inbound", 12, 0, 0, 1024) };
        operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>()).Returns(queues);

        var result = await new ListQueuesQueryHandler(operations, connections).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(queues);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListQueuesQueryHandler(Substitute.For<IServiceBusOperations>(), connections).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }
}
```

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Queues/CreateQueueCommandHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class CreateQueueCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_queue_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "sb-dev", "orders-inbound", 10));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateQueueAsync("Endpoint=sb://real", Arg.Is<CreateQueueRequest>(r => r.Name == "orders-inbound" && r.MaxDeliveryCount == 10), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("queue.create", "sb-dev/orders-inbound", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateQueueAsync(Arg.Any<string>(), Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "sb-dev", "orders-inbound", 10));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("queue.create", "sb-dev/orders-inbound", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Queues/DeleteQueueCommandHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class DeleteQueueCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_queue_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteQueueCommand(connectionId, "sb-dev", false, "orders-inbound"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteQueueAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("queue.delete", "sb-dev/orders-inbound", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter "FullyQualifiedName~Queues"`
Expected: FAIL to compile.

- [ ] **Step 4: Write the handlers**

```csharp
// src/SbConsole.Plugins.ServiceBus/Queues/ListQueuesQueryHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed class ListQueuesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections)
{
    public async Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<QueueSummary>>.Fail("Connection not found.");
        }

        var queues = await operations.ListQueuesAsync(secret, ct);
        return PluginResult<IReadOnlyList<QueueSummary>>.Ok(queues);
    }
}
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Queues/CreateQueueCommandHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, string QueueName, int MaxDeliveryCount);

public sealed class CreateQueueCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        try
        {
            await operations.CreateQueueAsync(secret, new CreateQueueRequest(cmd.QueueName, cmd.MaxDeliveryCount), ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult.Fail(ex.Message);
        }

        await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Queues/DeleteQueueCommandHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueName);

public sealed class DeleteQueueCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        try
        {
            await operations.DeleteQueueAsync(secret, cmd.QueueName, ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("queue.delete", target, ActionRisk.Destructive, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult.Fail(ex.Message);
        }

        await audit.RecordAsync("queue.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [ ] **Step 5: Register the handlers in ServiceBusPlugin.ConfigureServices**

Modify `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`'s `ConfigureServices`:

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<IServiceBusOperations, AzureServiceBusOperations>();
    services.AddScoped<Queues.ListQueuesQueryHandler>();
    services.AddScoped<Queues.CreateQueueCommandHandler>();
    services.AddScoped<Queues.DeleteQueueCommandHandler>();
}
```

(These are registered by the PLUGIN's own `ConfigureServices` — called by `AddSbConsolePlugin<TPlugin>()`, Task 6 — not by `Program.cs` directly. Unlike Core handlers, plugin handlers don't need a separate `Program.cs` registration line; the plugin owns its own DI wiring. This is a structural difference worth remembering for later tasks in this plan.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter "FullyQualifiedName~Queues"`
Expected: PASS (5 tests).

- [ ] **Step 7: Write the failing component tests**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class QueuesPageTests : BunitContext
{
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public QueuesPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "sb-dev", "azure-servicebus", ["dev"]) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddSingleton<ListQueuesQueryHandler>();
        Services.AddSingleton<CreateQueueCommandHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
    }

    [Fact]
    public async Task Lists_queues_for_the_first_available_connection()
    {
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 12, 3, 0, 2048) });

        var cut = Render<Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders-inbound");
        cut.Markup.Should().Contain("12");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { new("orders-inbound", 0, 0, 0, 0) });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "orders-inbound", false, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteQueueAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>());
    }
}
```

If any of this file's bUnit/MudBlazor interaction patterns (MudSelect for the connection picker, `IAsyncLifetime`/`TestDb` disposal, `WaitForState` vs `Task.Delay`) don't work as literally written, adapt using the established working patterns from `SbConsole.Web.Tests` (e.g. `ConnectionsPageTests.cs`, `AuditPageTests.cs`) — this project has already solved these exact MudBlazor/bUnit friction points once; don't re-derive them from scratch. Note this test file has NO `TestDb` (everything here is substituted, not backed by a real database, since queue data comes entirely from `IServiceBusOperations`/`IConnectionProvider` substitutes) — so no `IAsyncLifetime`/disposal concern applies here.

- [ ] **Step 8: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter QueuesPageTests`
Expected: FAIL to compile — the real `Queues.razor` (Task 6's stub) doesn't have this behavior yet.

- [ ] **Step 9: Replace the Queues.razor stub with the real page**

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor *@
@page "/p/azure-servicebus/queues"
@using Microsoft.AspNetCore.Components.Authorization
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Queues
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListQueuesQueryHandler ListHandler
@inject DeleteQueueCommandHandler DeleteHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService

<PageTitle>Queues</PageTitle>
<h1>Queues</h1>

@if (_connections.Count == 0)
{
    <MudAlert Severity="Severity.Info">No connections yet. Add an Azure Service Bus connection to get started.</MudAlert>
}
else
{
    <MudSelect T="Guid" Label="Namespace" Value="_selectedConnectionId" ValueChanged="OnConnectionChanged">
        @foreach (var connection in _connections)
        {
            <MudSelectItem Value="@connection.Id">@connection.Name</MudSelectItem>
        }
    </MudSelect>

    <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenCreate" Class="my-4">+ Create queue</MudButton>

    <MudTable Items="_queues">
        <HeaderContent>
            <MudTh>Queue</MudTh>
            <MudTh>Active</MudTh>
            <MudTh>Dead-letter</MudTh>
            <MudTh>Scheduled</MudTh>
            <MudTh>Size</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Name</MudTd>
            <MudTd>@context.ActiveMessageCount</MudTd>
            <MudTd>@context.DeadLetterMessageCount</MudTd>
            <MudTd>@context.ScheduledMessageCount</MudTd>
            <MudTd>@FormatSize(context.SizeInBytes)</MudTd>
            <MudTd>
                <MudButton Href="@PeekUrl(context.Name)">Peek</MudButton>
                <MudButton Class="delete-queue" Color="Color.Error" OnClick="@(() => DeleteAsync(context.Name))">Delete</MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<QueueSummary> _queues = [];
    private Guid _selectedConnectionId;

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("azure-servicebus");
        if (_connections.Count > 0)
        {
            _selectedConnectionId = _connections[0].Id;
            await LoadQueuesAsync();
        }
    }

    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        await LoadQueuesAsync();
    }

    private async Task LoadQueuesAsync()
    {
        var result = await ListHandler.HandleAsync(_selectedConnectionId);
        _queues = result.IsSuccess ? result.Value! : [];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    // The Peek page's purge action needs the connection's name (for a readable audit target) and
    // its prod status (for typed-confirmation gating) — carried across navigation as query
    // parameters since Peek.razor is reached via a full link, not a component parameter.
    private string PeekUrl(string queueName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/azure-servicebus/queues/{queueName}/peek" +
               $"?connectionId={connection.Id}" +
               $"&connectionName={Uri.EscapeDataString(connection.Name)}" +
               $"&isProd={connection.IsProd}";
    }

    private async Task OpenCreate()
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateQueueDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
        };
        var dialog = await DialogService.ShowAsync<CreateQueueDialog>("Create queue", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadQueuesAsync();
        }
    }

    private async Task DeleteAsync(string queueName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", queueName, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        await DeleteHandler.HandleAsync(new DeleteQueueCommand(connection.Id, connection.Name, connection.IsProd, queueName));
        await LoadQueuesAsync();
    }
}
```

Note: the "Peek" button's `Href` links to `/p/azure-servicebus/queues/{name}/peek?connectionId=...` — that route doesn't exist yet; Task 8 adds it. Leaving the link pointing there now is intentional (it's the natural next step) but will 404 until Task 8 lands; this is fine within a single plan's task sequence.

- [ ] **Step 10: Write CreateQueueDialog**

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/CreateQueueDialog.razor *@
@using SbConsole.Plugins.ServiceBus.Queues

<MudDialog>
    <DialogContent>
        <MudTextField id="queue-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudNumericField @bind-Value="_maxDeliveryCount" Label="Max delivery count" Min="1" Max="2000" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Class="save-queue" Color="Color.Primary" Variant="Variant.Filled" Disabled="@string.IsNullOrWhiteSpace(_name)" OnClick="Save">Create</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";

    [Inject] private CreateQueueCommandHandler CreateHandler { get; set; } = default!;

    private string _name = "";
    private int _maxDeliveryCount = 10;

    private async Task Save()
    {
        await CreateHandler.HandleAsync(new CreateQueueCommand(ConnectionId, ConnectionName, _name, _maxDeliveryCount));
        MudDialog.Close(DialogResult.Ok(true));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 11: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter QueuesPageTests`
Expected: PASS (3 tests).

- [ ] **Step 12: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 13: Commit**

```bash
git add -A
git commit -m "feat: add Queues page — list, create, delete"
```

---

### Task 8: Message peek — queue and dead-letter, view-only

One page serves both "peek the queue" and "peek the dead-letter sub-queue" — they're structurally identical (a two-pane message browser), distinguished by a `deadLetter` query flag. Destructive/mutating actions (resubmit, purge, send) are Task 9 — this task is read-only.

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/PeekMessagesQueryHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (register the handler)
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PeekMessagesQueryHandlerTests.cs`, `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations`/`PeekedMessage` (Task 5), `IConnectionProvider` (existing), `PluginResult<T>` (Task 7).
- Produces (used by Task 9): route `/p/azure-servicebus/queues/{QueueName}/peek` with query parameters `connectionId` (Guid) and `deadLetter` (bool, default false). `PeekMessagesQueryHandler(IServiceBusOperations, IConnectionProvider)` with `Task<PluginResult<IReadOnlyList<PeekedMessage>>> HandleAsync(Guid connectionId, string queueName, bool fromDeadLetter, long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing handler test**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PeekMessagesQueryHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PeekMessagesQueryHandlerTests
{
    [Fact]
    public async Task Peeks_the_dead_letter_subqueue_when_asked()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var messages = new[] { new PeekedMessage(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>(), "MaxDeliveryCountExceeded", "boom") };
        operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>()).Returns(messages);

        var result = await new PeekMessagesQueryHandler(operations, connections)
            .HandleAsync(connectionId, "orders-inbound", fromDeadLetter: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task Unknown_connection_fails_rather_than_returning_an_empty_page()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new PeekMessagesQueryHandler(Substitute.For<IServiceBusOperations>(), connections)
            .HandleAsync(Guid.NewGuid(), "orders-inbound", fromDeadLetter: false);

        result.IsSuccess.Should().BeFalse();
    }
}
```

Check the exact parameter order/names your `IServiceBusOperations.PeekMessagesAsync` ended up with in Task 5 (it's `(connectionString, queueName, fromDeadLetter, maxMessages, fromSequenceNumber, ct)`) — match this test's `Arg.Is`/positional call to whatever actually compiles.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekMessagesQueryHandlerTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the handler**

```csharp
// src/SbConsole.Plugins.ServiceBus/Messages/PeekMessagesQueryHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed class PeekMessagesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections)
{
    public async Task<PluginResult<IReadOnlyList<PeekedMessage>>> HandleAsync(
        Guid connectionId, string queueName, bool fromDeadLetter,
        long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<PeekedMessage>>.Fail("Connection not found.");
        }

        var messages = await operations.PeekMessagesAsync(secret, queueName, fromDeadLetter, maxMessages, fromSequenceNumber, ct);
        return PluginResult<IReadOnlyList<PeekedMessage>>.Ok(messages);
    }
}
```

- [ ] **Step 4: Register it and run tests**

Add to `ServiceBusPlugin.ConfigureServices`:

```csharp
services.AddScoped<Messages.PeekMessagesQueryHandler>();
```

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekMessagesQueryHandlerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Write the failing component test**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class PeekPageTests : BunitContext
{
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public PeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton<PeekMessagesQueryHandler>();
    }

    [Fact]
    public async Task Shows_message_list_and_selecting_one_shows_its_body()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, """{"orderId":"UK-123"}""", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string> { ["correlationId"] = "c-1" }),
            });

        var cut = Render<Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound")
            .Add(p => p.ConnectionId, _connectionId)
            .Add(p => p.DeadLetter, false));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("UK-123");
        cut.Markup.Should().Contain("correlationId");
    }

    [Fact]
    public async Task Dead_letter_mode_shows_the_dead_letter_reason()
    {
        _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>(), "MaxDeliveryCountExceeded", "Handler threw"),
            });

        var cut = Render<Peek>(parameters => parameters
            .Add(p => p.QueueName, "orders-inbound")
            .Add(p => p.ConnectionId, _connectionId)
            .Add(p => p.DeadLetter, true));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("MaxDeliveryCountExceeded");
    }
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekPageTests`
Expected: FAIL to compile — `Peek` component doesn't exist.

- [ ] **Step 7: Write Peek.razor**

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor *@
@page "/p/azure-servicebus/queues/{QueueName}/peek"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Messages
@inject PeekMessagesQueryHandler PeekHandler

<PageTitle>@(DeadLetter ? "Dead-letter" : "Peek") — @QueueName</PageTitle>
<h1>@(DeadLetter ? "Dead-letter" : "Peek"): @QueueName</h1>

<MudGrid>
    <MudItem xs="5">
        <MudList T="PeekedMessage" SelectedValue="_selected" SelectedValueChanged="OnSelect">
            @foreach (var message in _messages)
            {
                <MudListItem T="PeekedMessage" Value="message">
                    #@message.SequenceNumber · @message.EnqueuedTime.ToLocalTime() · deliveries @message.DeliveryCount
                </MudListItem>
            }
        </MudList>
    </MudItem>
    <MudItem xs="7">
        @if (_selected is not null)
        {
            @if (_selected.DeadLetterReason is { } reason)
            {
                <MudAlert Severity="Severity.Warning">@reason — @_selected.DeadLetterErrorDescription</MudAlert>
            }
            <MudText Typo="Typo.subtitle2">@_selected.ContentType</MudText>
            <pre>@_selected.Body</pre>
            <MudText Typo="Typo.subtitle2" Class="mt-4">Application properties</MudText>
            <MudTable Items="_selected.Properties">
                <HeaderContent>
                    <MudTh>Key</MudTh>
                    <MudTh>Value</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>@context.Key</MudTd>
                    <MudTd>@context.Value</MudTd>
                </RowTemplate>
            </MudTable>
        }
    </MudItem>
</MudGrid>

@code {
    [Parameter] public string QueueName { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }
    [SupplyParameterFromQuery] public bool DeadLetter { get; set; }

    private IReadOnlyList<PeekedMessage> _messages = [];
    private PeekedMessage? _selected;

    protected override async Task OnInitializedAsync()
    {
        var result = await PeekHandler.HandleAsync(ConnectionId, QueueName, DeadLetter);
        _messages = result.IsSuccess ? result.Value! : [];
    }

    private void OnSelect(PeekedMessage message) => _selected = message;
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekPageTests`
Expected: PASS (2 tests). If `MudList`/`MudListItem`'s selection API (`SelectedValue`/`SelectedValueChanged`) doesn't match the installed MudBlazor version, check the real API the same way earlier tasks in this project verified MudBlazor specifics, and adapt.

- [ ] **Step 9: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: add message peek (queue and dead-letter), view-only"
```

---

### Task 9: Send message dialog + dead-letter resubmit (multi-select) + purge

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/SendMessageCommandHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Messages/ResubmitDeadLetterMessagesCommandHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Messages/PurgeDeadLetterMessagesCommandHandler.cs`, `src/SbConsole.Plugins.ServiceBus/Pages/SendMessageDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (register the three handlers), `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor` (add a "Send" row action), `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor` (add multi-select + resubmit + purge, dead-letter mode only)
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/SendMessageCommandHandlerTests.cs`, `.../ResubmitDeadLetterMessagesCommandHandlerTests.cs`, `.../PurgeDeadLetterMessagesCommandHandlerTests.cs`, extend `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations`/`SendMessageRequest` (Task 5), `PluginResult`/`PluginResult<T>` (Task 7), `IConfirmationService` (Sdk, existing).
- Produces: the plugin's full v1 Queues feature set is complete after this task.

- [ ] **Step 1: Write the failing handler tests**

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Messages/SendMessageCommandHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Sends_the_message_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new SendMessageCommand(connectionId, "sb-dev", "orders-inbound", """{"a":1}""", "application/json", null, null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).SendMessageAsync("Endpoint=sb://real", "orders-inbound", Arg.Is<SendMessageRequest>(r => r.Body == """{"a":1}"""), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("message.send", "sb-dev/orders-inbound", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Messages/ResubmitDeadLetterMessagesCommandHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class ResubmitDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Resubmits_the_named_messages_and_audits_the_count()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ResubmitDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1, 2, 3 })), Arg.Any<CancellationToken>())
            .Returns(3);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResubmitDeadLetterMessagesCommandHandler(operations, connections, audit)
            .HandleAsync(new ResubmitDeadLetterMessagesCommand(connectionId, "sb-dev", "orders-inbound", [1, 2, 3]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
        await audit.Received(1).RecordAsync("message.resubmit", "sb-dev/orders-inbound", ActionRisk.Mutating, true, "3 of 3 resubmitted", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PurgeDeadLetterMessagesCommandHandlerTests.cs
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PurgeDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Purges_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PurgeDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>()).Returns(214);
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeDeadLetterMessagesCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeDeadLetterMessagesCommand(connectionId, "sb-dev", "orders-inbound"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(214);
        await audit.Received(1).RecordAsync("queue.purge", "sb-dev/orders-inbound", ActionRisk.Destructive, true, "214 messages purged", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter "SendMessageCommandHandlerTests|ResubmitDeadLetterMessagesCommandHandlerTests|PurgeDeadLetterMessagesCommandHandlerTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the three handlers**

```csharp
// src/SbConsole.Plugins.ServiceBus/Messages/SendMessageCommandHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record SendMessageCommand(
    Guid ConnectionId, string ConnectionName, string QueueName, string Body, string ContentType,
    IReadOnlyDictionary<string, string>? Properties, DateTimeOffset? ScheduledEnqueueTime);

public sealed class SendMessageCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(SendMessageCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var request = new SendMessageRequest(cmd.Body, cmd.ContentType, cmd.Properties, cmd.ScheduledEnqueueTime);
        try
        {
            await operations.SendMessageAsync(secret, cmd.QueueName, request, ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult.Fail(ex.Message);
        }

        await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Messages/ResubmitDeadLetterMessagesCommandHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record ResubmitDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName, IReadOnlyList<long> SequenceNumbers);

public sealed class ResubmitDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<int>> HandleAsync(ResubmitDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        int resubmitted;
        try
        {
            resubmitted = await operations.ResubmitDeadLetterMessagesAsync(secret, cmd.QueueName, cmd.SequenceNumbers, ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult<int>.Fail(ex.Message);
        }

        await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: true, detail: $"{resubmitted} of {cmd.SequenceNumbers.Count} resubmitted", ct: ct);
        return PluginResult<int>.Ok(resubmitted);
    }
}
```

```csharp
// src/SbConsole.Plugins.ServiceBus/Messages/PurgeDeadLetterMessagesCommandHandler.cs
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record PurgeDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName);

public sealed class PurgeDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<int>> HandleAsync(PurgeDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        int purged;
        try
        {
            purged = await operations.PurgeDeadLetterMessagesAsync(secret, cmd.QueueName, ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult<int>.Fail(ex.Message);
        }

        await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: true, detail: $"{purged} messages purged", ct: ct);
        return PluginResult<int>.Ok(purged);
    }
}
```

- [ ] **Step 4: Register the three handlers and run tests**

Add to `ServiceBusPlugin.ConfigureServices`:

```csharp
services.AddScoped<Messages.SendMessageCommandHandler>();
services.AddScoped<Messages.ResubmitDeadLetterMessagesCommandHandler>();
services.AddScoped<Messages.PurgeDeadLetterMessagesCommandHandler>();
```

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter "SendMessageCommandHandlerTests|ResubmitDeadLetterMessagesCommandHandlerTests|PurgeDeadLetterMessagesCommandHandlerTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Write SendMessageDialog**

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/SendMessageDialog.razor *@
@using SbConsole.Plugins.ServiceBus.Messages

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">@ConnectionName / @QueueName</MudText>
        <MudTextField id="message-body" @bind-Value="_body" Label="Body" Lines="8" Immediate="true" />
        <MudTextField @bind-Value="_contentType" Label="Content type" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Class="send-message" Color="Color.Primary" Variant="Variant.Filled" Disabled="@string.IsNullOrWhiteSpace(_body)" OnClick="Send">Send</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string QueueName { get; set; } = "";

    [Inject] private SendMessageCommandHandler SendHandler { get; set; } = default!;

    private string _body = "";
    private string _contentType = "application/json";

    private async Task Send()
    {
        await SendHandler.HandleAsync(new SendMessageCommand(ConnectionId, ConnectionName, QueueName, _body, _contentType, null, null));
        MudDialog.Close(DialogResult.Ok(true));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 6: Add the Send row action to Queues.razor**

Modify `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor` — add a "Send" button between Peek and Delete in the row template:

```razor
<MudButton Href="@($"/p/azure-servicebus/queues/{context.Name}/peek?connectionId={_selectedConnectionId}")">Peek</MudButton>
<MudButton OnClick="@(() => OpenSend(context.Name))">Send</MudButton>
<MudButton Class="delete-queue" Color="Color.Error" OnClick="@(() => DeleteAsync(context.Name))">Delete</MudButton>
```

Add the corresponding method to the `@code` block:

```csharp
private async Task OpenSend(string queueName)
{
    var connection = _connections.Single(c => c.Id == _selectedConnectionId);
    var parameters = new DialogParameters<SendMessageDialog>
    {
        { x => x.ConnectionId, connection.Id },
        { x => x.ConnectionName, connection.Name },
        { x => x.QueueName, queueName },
    };
    await DialogService.ShowAsync<SendMessageDialog>("Send message", parameters);
}
```

- [ ] **Step 7: Write the failing tests for Peek.razor's dead-letter actions**

Add to `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs`. Register REAL `ResubmitDeadLetterMessagesCommandHandler`/`PurgeDeadLetterMessagesCommandHandler` instances built from the already-substituted `_operations`/`_connections` (the same pattern `QueuesPageTests` already uses successfully) rather than trying to substitute the handler classes themselves — they're plain classes with no virtual members, so NSubstitute can't proxy them the way it proxies `IServiceBusOperations`; only true external-boundary interfaces get substituted in this codebase's tests, never the project's own classes:

```csharp
// add to the constructor:
Services.AddSingleton(Substitute.For<IConfirmationService>());
Services.AddSingleton(new Messages.ResubmitDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>()));
Services.AddSingleton(new Messages.PurgeDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>()));

[Fact]
public async Task Dead_letter_mode_shows_resubmit_and_purge_actions_but_queue_mode_does_not()
{
    _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
        .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

    var cut = Render<Peek>(parameters => parameters
        .Add(p => p.QueueName, "orders-inbound")
        .Add(p => p.ConnectionId, _connectionId)
        .Add(p => p.DeadLetter, true));
    await Task.Delay(30);
    cut.Render();

    cut.FindAll("button.purge-queue").Should().HaveCount(1);
    cut.FindAll("input.select-message").Should().HaveCount(1);
}

[Fact]
public async Task Resubmit_selected_calls_the_operations_seam_with_the_checked_sequence_numbers()
{
    _operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>())
        .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
    _operations.ResubmitDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
        .Returns(1);

    var cut = Render<Peek>(parameters => parameters
        .Add(p => p.QueueName, "orders-inbound")
        .Add(p => p.ConnectionId, _connectionId)
        .Add(p => p.DeadLetter, true));
    await Task.Delay(30);
    cut.Render();
    cut.Find("input.select-message").Change(true);
    cut.Find("button.resubmit-selected").Click();
    await Task.Delay(30);

    await _operations.Received(1).ResubmitDeadLetterMessagesAsync(
        "Endpoint=sb://real", "orders-inbound",
        Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1 })),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 8: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekPageTests`
Expected: FAIL — `button.purge-queue`/`input.select-message`/`button.resubmit-selected` don't exist yet.

- [ ] **Step 9: Add multi-select, resubmit, and purge to Peek.razor**

Replace the `MudList`-based message list with plain markup carrying a checkbox (dead-letter mode only) and a clickable row, plus a resubmit/purge action bar:

```razor
@* src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor — full replacement *@
@page "/p/azure-servicebus/queues/{QueueName}/peek"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Messages
@using SbConsole.Sdk
@inject PeekMessagesQueryHandler PeekHandler
@inject ResubmitDeadLetterMessagesCommandHandler ResubmitHandler
@inject PurgeDeadLetterMessagesCommandHandler PurgeHandler
@inject IConfirmationService Confirmation

<PageTitle>@(DeadLetter ? "Dead-letter" : "Peek") — @QueueName</PageTitle>
<h1>@(DeadLetter ? "Dead-letter" : "Peek"): @QueueName</h1>

@if (DeadLetter)
{
    <div class="d-flex gap-2 mb-2">
        <MudButton Class="resubmit-selected" Disabled="@(_selectedSequenceNumbers.Count == 0)" OnClick="ResubmitSelectedAsync">
            Resubmit selected (@_selectedSequenceNumbers.Count)
        </MudButton>
        <MudButton Class="purge-queue" Color="Color.Error" OnClick="PurgeAsync">Purge queue</MudButton>
    </div>
}

<MudGrid>
    <MudItem xs="5">
        @foreach (var message in _messages)
        {
            <div class="d-flex align-center gap-2">
                @if (DeadLetter)
                {
                    <input type="checkbox" class="select-message"
                           checked="@_selectedSequenceNumbers.Contains(message.SequenceNumber)"
                           @onchange="@(e => ToggleSelection(message.SequenceNumber, (bool)e.Value!))" />
                }
                <MudButton OnClick="@(() => _selected = message)">
                    #@message.SequenceNumber · @message.EnqueuedTime.ToLocalTime() · deliveries @message.DeliveryCount
                </MudButton>
            </div>
        }
    </MudItem>
    <MudItem xs="7">
        @if (_selected is not null)
        {
            @if (_selected.DeadLetterReason is { } reason)
            {
                <MudAlert Severity="Severity.Warning">@reason — @_selected.DeadLetterErrorDescription</MudAlert>
            }
            <MudText Typo="Typo.subtitle2">@_selected.ContentType</MudText>
            <pre>@_selected.Body</pre>
            <MudText Typo="Typo.subtitle2" Class="mt-4">Application properties</MudText>
            <MudTable Items="_selected.Properties">
                <HeaderContent>
                    <MudTh>Key</MudTh>
                    <MudTh>Value</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>@context.Key</MudTd>
                    <MudTd>@context.Value</MudTd>
                </RowTemplate>
            </MudTable>
        }
    </MudItem>
</MudGrid>

@code {
    [Parameter] public string QueueName { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }
    [SupplyParameterFromQuery] public string ConnectionName { get; set; } = "";
    [SupplyParameterFromQuery] public bool IsProd { get; set; }
    [SupplyParameterFromQuery] public bool DeadLetter { get; set; }

    private IReadOnlyList<PeekedMessage> _messages = [];
    private PeekedMessage? _selected;
    private readonly HashSet<long> _selectedSequenceNumbers = [];

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var result = await PeekHandler.HandleAsync(ConnectionId, QueueName, DeadLetter);
        _messages = result.IsSuccess ? result.Value! : [];
        _selectedSequenceNumbers.Clear();
        _selected = null;
    }

    private void ToggleSelection(long sequenceNumber, bool selected)
    {
        if (selected)
        {
            _selectedSequenceNumbers.Add(sequenceNumber);
        }
        else
        {
            _selectedSequenceNumbers.Remove(sequenceNumber);
        }
    }

    private async Task ResubmitSelectedAsync()
    {
        await ResubmitHandler.HandleAsync(new ResubmitDeadLetterMessagesCommand(ConnectionId, ConnectionName, QueueName, [.. _selectedSequenceNumbers]));
        await LoadAsync();
    }

    private async Task PurgeAsync()
    {
        var confirmed = await Confirmation.ConfirmAsync("Purge", QueueName, IsProd, count: _messages.Count);
        if (!confirmed)
        {
            return;
        }

        await PurgeHandler.HandleAsync(new PurgeDeadLetterMessagesCommand(ConnectionId, ConnectionName, QueueName));
        await LoadAsync();
    }
}
```

`ConnectionName` and `IsProd` arrive as query parameters set by `Queues.razor`'s `PeekUrl` helper (Task 7) — the same values `Confirmation.ConfirmAsync` and the delete flow already use on that page, carried across navigation since `Peek.razor` is reached via a full link, not a component parameter. This is what makes `PurgeAsync`'s typed-confirmation gating correct for prod-tagged connections, not hardcoded to `false`.

- [ ] **Step 10: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter PeekPageTests`
Expected: PASS (all tests in the file).

- [ ] **Step 11: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 12: Manual smoke test**

```bash
SBC_DB_PATH=/tmp/sbc-sb-final.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8
curl -s -c /tmp/sbc-final-cookies.txt -o /dev/null -w "login:%{http_code}\n" -d "password=dev" http://localhost:5080/auth/login
curl -s -b /tmp/sbc-final-cookies.txt -o /dev/null -w "queues:%{http_code}\n" http://localhost:5080/p/azure-servicebus/queues
kill %1
rm -f /tmp/sbc-final-cookies.txt
```

Expected: `login:302`, `queues:200`. (A real queue list requires a real or emulated Azure Service Bus namespace — out of scope per this plan's testing strategy — so an empty "No connections yet" state is the expected, correct result of this smoke test on a fresh database, not a failure.)

- [ ] **Step 13: Commit**

```bash
git add -A
git commit -m "feat: add send message dialog, dead-letter resubmit (multi-select), and purge"
```

---

## After this plan

- **Next plan:** Topics & Subscriptions — reuses `IServiceBusOperations` (extended with topic/subscription/rule methods), the message peek/send/DLQ components built here, and the confirmation-flow pattern. Nested subscription rows under each topic, per the wireframe.
- **Deferred, per docs/design.md §6/§8:** Testcontainers + the official Service Bus emulator (`SbConsole.IntegrationTests`) — picked up once this plugin's shape has proven out in real use; deferred-message tooling; sessions tooling; metrics/history dashboards; ARM/namespace creation; Entra ID auth.
