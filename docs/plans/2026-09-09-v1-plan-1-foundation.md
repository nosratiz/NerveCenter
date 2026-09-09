# SbConsole v1 — Plan 1 of 3: Platform Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scaffold the SbConsole solution and build the plugin-host chassis: SDK contracts, Core services (results, secrets, EF model, settings, audit, connections, plugin store), MudBlazor web shell with plugin registry, and auth.

**Architecture:** Per `docs/design.md`. Four src projects (`Sdk`, `Core`, `Web`, `Plugins.ServiceBus`) + test projects. Plugins depend only on Sdk; Core depends on Sdk; Web wires everything. Handlers are plain classes; commands audit via `IAuditWriter`; secrets AES-GCM encrypted before write. DbContext access is always via `IDbContextFactory<SbcDbContext>` (Blazor Server safety).

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor, EF Core + SQLite (WAL), Minimal APIs, xUnit + FluentAssertions (pin 7.x — v8 changed license) + NSubstitute + bUnit.

**Out of scope for this plan:** the Service Bus plugin's features (Plan 2), automation API endpoints beyond health, typed-confirmation dialog, integration tests (Plan 3).

## Global Constraints

- .NET 10 (`net10.0`), C# `latest`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — all via `Directory.Build.props`.
- No MediatR. No controllers. No second component library. No WebAssembly.
- No SQLite-only SQL in Core (PostgreSQL must remain possible).
- Plugins never reference the DbContext; only `SbConsole.Sdk` types.
- Never log or return secret/connection values; secrets encrypted via `ISecretProtector` before any write.
- Env vars for bootstrap only: `SBC_DB_PATH`, `SBC_DATA_KEY` (base64, 32 bytes), `SBC_ADMIN_PASSWORD`, `SBC_API_KEY`, `SBC_BIND`.
- Gate before every commit: `dotnet build -warnaserror && dotnet test` both green.
- Conventional commits, ending with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Solution scaffold

**Files:**
- Create: `Directory.Build.props`, `SbConsole.sln`, `src/SbConsole.Sdk/`, `src/SbConsole.Core/`, `src/SbConsole.Web/`, `src/SbConsole.Plugins.ServiceBus/`, `tests/SbConsole.Core.Tests/`, `tests/SbConsole.Web.Tests/`, `tests/SbConsole.Plugins.ServiceBus.Tests/`

**Interfaces:**
- Consumes: nothing.
- Produces: buildable solution with project references: Core→Sdk, Web→Core+Sdk+Plugins.ServiceBus, Plugins.ServiceBus→Sdk (ONLY), tests→their subject.

- [ ] **Step 1: Create Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create projects and solution**

```bash
dotnet new sln -n SbConsole
dotnet new classlib -o src/SbConsole.Sdk
dotnet new classlib -o src/SbConsole.Core
dotnet new blazor -o src/SbConsole.Web -int Server -e
dotnet new classlib -o src/SbConsole.Plugins.ServiceBus
dotnet new xunit -o tests/SbConsole.Core.Tests
dotnet new xunit -o tests/SbConsole.Web.Tests
dotnet new xunit -o tests/SbConsole.Plugins.ServiceBus.Tests
dotnet sln add src/SbConsole.Sdk src/SbConsole.Core src/SbConsole.Web src/SbConsole.Plugins.ServiceBus tests/SbConsole.Core.Tests tests/SbConsole.Web.Tests tests/SbConsole.Plugins.ServiceBus.Tests
rm src/SbConsole.Sdk/Class1.cs src/SbConsole.Core/Class1.cs src/SbConsole.Plugins.ServiceBus/Class1.cs
```

Delete `UnitTest1.cs` from each test project only when the task that adds real tests to it lands (an empty xunit project still builds). Leave `tests/SbConsole.Plugins.ServiceBus.Tests/UnitTest1.cs` in place until Plan 2.

- [ ] **Step 3: Add project references**

```bash
dotnet add src/SbConsole.Core reference src/SbConsole.Sdk
dotnet add src/SbConsole.Plugins.ServiceBus reference src/SbConsole.Sdk
dotnet add src/SbConsole.Web reference src/SbConsole.Core src/SbConsole.Sdk src/SbConsole.Plugins.ServiceBus
dotnet add tests/SbConsole.Core.Tests reference src/SbConsole.Core
dotnet add tests/SbConsole.Web.Tests reference src/SbConsole.Web
dotnet add tests/SbConsole.Plugins.ServiceBus.Tests reference src/SbConsole.Plugins.ServiceBus
```

- [ ] **Step 4: Add test packages**

```bash
dotnet add tests/SbConsole.Core.Tests package FluentAssertions --version 7.2.0
dotnet add tests/SbConsole.Core.Tests package NSubstitute
dotnet add tests/SbConsole.Web.Tests package FluentAssertions --version 7.2.0
dotnet add tests/SbConsole.Web.Tests package NSubstitute
dotnet add tests/SbConsole.Web.Tests package bunit
dotnet add tests/SbConsole.Plugins.ServiceBus.Tests package FluentAssertions --version 7.2.0
dotnet add tests/SbConsole.Plugins.ServiceBus.Tests package NSubstitute
```

- [ ] **Step 5: Verify build + tests green**

Run: `dotnet build -warnaserror && dotnet test`
Expected: build succeeds, template placeholder tests pass. If the blazor template's `Home.razor`/layout produce warnings-as-errors, fix them now (they usually don't).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution, projects, references, test packages"
```

---

### Task 2: SDK contracts

**Files:**
- Create: `src/SbConsole.Sdk/ActionRisk.cs`, `src/SbConsole.Sdk/PluginNavItem.cs`, `src/SbConsole.Sdk/IPlugin.cs`, `src/SbConsole.Sdk/IPluginStore.cs`, `src/SbConsole.Sdk/ConnectionInfo.cs`, `src/SbConsole.Sdk/IConnectionProvider.cs`, `src/SbConsole.Sdk/IAuditScope.cs`
- Test: `tests/SbConsole.Core.Tests/Sdk/ActionRiskTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.DependencyInjection.Abstractions` (the ONLY package Sdk may take).
- Produces (used by every later task):
  - `enum ActionRisk { Safe = 0, Mutating = 1, Destructive = 2 }`
  - `record PluginNavItem(string Title, string Href, string? Icon = null)`
  - `interface IPlugin { string Id; string DisplayName; string Version; IReadOnlyList<PluginNavItem> NavItems; Type RootComponent; void ConfigureServices(IServiceCollection services); }`
  - `interface IPluginStore { Task<string?> GetAsync(string key, CancellationToken ct = default); Task SetAsync(string key, string value, CancellationToken ct = default); Task<bool> DeleteAsync(string key, CancellationToken ct = default); }`
  - `record ConnectionInfo(Guid Id, string Name, string Kind, IReadOnlyList<string> Tags)`
  - `interface IConnectionProvider { Task<IReadOnlyList<ConnectionInfo>> ListAsync(string kind, CancellationToken ct = default); Task<string?> GetSecretAsync(Guid connectionId, CancellationToken ct = default); }`
  - `interface IAuditScope { Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default); }`

- [ ] **Step 1: Add the DI abstractions package to Sdk**

```bash
dotnet add src/SbConsole.Sdk package Microsoft.Extensions.DependencyInjection.Abstractions
```

- [ ] **Step 2: Write the failing test** (in Core.Tests — Sdk has no test project of its own; delete `tests/SbConsole.Core.Tests/UnitTest1.cs` now)

```csharp
// tests/SbConsole.Core.Tests/Sdk/ActionRiskTests.cs
using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class ActionRiskTests
{
    [Fact]
    public void Risk_levels_are_ordered_safe_to_destructive()
    {
        ((int)ActionRisk.Safe).Should().Be(0);
        ((int)ActionRisk.Mutating).Should().Be(1);
        ((int)ActionRisk.Destructive).Should().Be(2);
        (ActionRisk.Destructive > ActionRisk.Mutating).Should().BeTrue();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ActionRiskTests`
Expected: FAIL to compile — `ActionRisk` does not exist.

- [ ] **Step 4: Write the SDK types**

```csharp
// src/SbConsole.Sdk/ActionRisk.cs
namespace SbConsole.Sdk;

/// <summary>Risk level of a plugin action; the host enforces confirmation rules from it.</summary>
public enum ActionRisk
{
    Safe = 0,
    Mutating = 1,
    Destructive = 2,
}
```

```csharp
// src/SbConsole.Sdk/PluginNavItem.cs
namespace SbConsole.Sdk;

public sealed record PluginNavItem(string Title, string Href, string? Icon = null);
```

```csharp
// src/SbConsole.Sdk/IPlugin.cs
using Microsoft.Extensions.DependencyInjection;

namespace SbConsole.Sdk;

public interface IPlugin
{
    /// <summary>Stable, URL-safe identifier, e.g. "servicebus". Used in routes (/p/{Id}/...) and storage scoping.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    IReadOnlyList<PluginNavItem> NavItems { get; }

    /// <summary>Root Blazor component rendered at /p/{Id}. Must derive from ComponentBase.</summary>
    Type RootComponent { get; }

    void ConfigureServices(IServiceCollection services);
}
```

```csharp
// src/SbConsole.Sdk/IPluginStore.cs
namespace SbConsole.Sdk;

/// <summary>Per-plugin persistence. Keys are scoped to the owning plugin; plugins never see the host database.</summary>
public interface IPluginStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
}
```

```csharp
// src/SbConsole.Sdk/ConnectionInfo.cs
namespace SbConsole.Sdk;

/// <summary>Connection metadata safe to show in UI. Never carries the secret.</summary>
public sealed record ConnectionInfo(Guid Id, string Name, string Kind, IReadOnlyList<string> Tags)
{
    public bool IsProd => Tags.Contains("prod", StringComparer.OrdinalIgnoreCase);
}
```

```csharp
// src/SbConsole.Sdk/IConnectionProvider.cs
namespace SbConsole.Sdk;

public interface IConnectionProvider
{
    Task<IReadOnlyList<ConnectionInfo>> ListAsync(string kind, CancellationToken ct = default);

    /// <summary>Decrypts and returns the secret just-in-time. Null if the connection does not exist.</summary>
    Task<string?> GetSecretAsync(Guid connectionId, CancellationToken ct = default);
}
```

```csharp
// src/SbConsole.Sdk/IAuditScope.cs
namespace SbConsole.Sdk;

/// <summary>Plugins report what they did; the host writes the audit row.</summary>
public interface IAuditScope
{
    Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ActionRiskTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add SbConsole.Sdk v1.0.0 plugin contracts"
```

---

### Task 3: Result and Error types (Core)

**Files:**
- Create: `src/SbConsole.Core/Results/ErrorCategory.cs`, `src/SbConsole.Core/Results/Error.cs`, `src/SbConsole.Core/Results/Result.cs`
- Test: `tests/SbConsole.Core.Tests/Results/ResultTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum ErrorCategory { NotFound, Conflict, AuthFailure, Transient, Invalid }`
  - `record Error(ErrorCategory Category, string Message)`
  - `Result` with `bool IsSuccess`, `Error? Error`, `static Result Ok()`, `static Result Fail(ErrorCategory, string)`
  - `Result<T>` with `bool IsSuccess`, `T? Value`, `Error? Error`, `static Result<T> Ok(T)`, `static Result<T> Fail(ErrorCategory, string)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Results/ResultTests.cs
using FluentAssertions;
using SbConsole.Core.Results;

namespace SbConsole.Core.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Ok_result_has_value_and_no_error()
    {
        var result = Result<int>.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failed_result_carries_category_and_message()
    {
        var result = Result<int>.Fail(ErrorCategory.NotFound, "queue missing");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(new Error(ErrorCategory.NotFound, "queue missing"));
    }

    [Fact]
    public void Non_generic_result_works_for_commands()
    {
        Result.Ok().IsSuccess.Should().BeTrue();
        Result.Fail(ErrorCategory.Conflict, "exists").Error!.Category.Should().Be(ErrorCategory.Conflict);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ResultTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Results/ErrorCategory.cs
namespace SbConsole.Core.Results;

public enum ErrorCategory
{
    NotFound,
    Conflict,
    AuthFailure,
    Transient,
    Invalid,
}
```

```csharp
// src/SbConsole.Core/Results/Error.cs
namespace SbConsole.Core.Results;

public sealed record Error(ErrorCategory Category, string Message);
```

```csharp
// src/SbConsole.Core/Results/Result.cs
namespace SbConsole.Core.Results;

public sealed class Result
{
    private Result(bool isSuccess, Error? error) => (IsSuccess, Error) = (isSuccess, error);

    public bool IsSuccess { get; }
    public Error? Error { get; }

    public static Result Ok() => new(true, null);
    public static Result Fail(ErrorCategory category, string message) => new(false, new Error(category, message));
}

public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, Error? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);

    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(ErrorCategory category, string message) => new(false, default, new Error(category, message));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter ResultTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Result/Error types for handler outcomes"
```

---

### Task 4: AES-GCM secret protector

**Files:**
- Create: `src/SbConsole.Core/Security/ISecretProtector.cs`, `src/SbConsole.Core/Security/AesGcmSecretProtector.cs`
- Test: `tests/SbConsole.Core.Tests/Security/AesGcmSecretProtectorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface ISecretProtector { byte[] Protect(string plaintext); string Unprotect(byte[] blob); }`
  - `AesGcmSecretProtector(byte[] key)` — key must be 32 bytes; blob layout `nonce(12) || tag(16) || ciphertext`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Security/AesGcmSecretProtectorTests.cs
using System.Security.Cryptography;
using FluentAssertions;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Security;

public class AesGcmSecretProtectorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Roundtrip_returns_original_plaintext()
    {
        var protector = new AesGcmSecretProtector(Key);

        var blob = protector.Protect("Endpoint=sb://x/;SharedAccessKey=secret");

        new AesGcmSecretProtector(Key).Unprotect(blob)
            .Should().Be("Endpoint=sb://x/;SharedAccessKey=secret");
    }

    [Fact]
    public void Same_plaintext_encrypts_to_different_blobs()
    {
        var protector = new AesGcmSecretProtector(Key);

        protector.Protect("s").Should().NotEqual(protector.Protect("s")); // random nonce
    }

    [Fact]
    public void Tampered_blob_throws()
    {
        var protector = new AesGcmSecretProtector(Key);
        var blob = protector.Protect("secret");
        blob[^1] ^= 0xFF;

        var act = () => protector.Unprotect(blob);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        var act = () => new AesGcmSecretProtector(new byte[16]);

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter AesGcmSecretProtectorTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Security/ISecretProtector.cs
namespace SbConsole.Core.Security;

public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] blob);
}
```

```csharp
// src/SbConsole.Core/Security/AesGcmSecretProtector.cs
using System.Security.Cryptography;
using System.Text;

namespace SbConsole.Core.Security;

/// <summary>Blob layout: nonce(12) || tag(16) || ciphertext.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Data key must be exactly 32 bytes.", nameof(key));
        }

        _key = key;
    }

    public byte[] Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        return blob;
    }

    public string Unprotect(byte[] blob)
    {
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter AesGcmSecretProtectorTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add AES-GCM secret protector"
```

---

### Task 5: EF Core model, DbContext, migration, test helper

**Files:**
- Create: `src/SbConsole.Core/Data/Entities/AppSetting.cs`, `.../Connection.cs`, `.../AuditEntry.cs`, `.../PluginDocument.cs`, `src/SbConsole.Core/Data/SbcDbContext.cs`, `src/SbConsole.Core/Data/SbcDbContextDesignFactory.cs`, `src/SbConsole.Core/Migrations/` (generated)
- Test: `tests/SbConsole.Core.Tests/TestDb.cs`, `tests/SbConsole.Core.Tests/Data/SbcDbContextTests.cs`

**Interfaces:**
- Consumes: `ActionRisk` from Sdk (Task 2).
- Produces (used by Tasks 6–9):
  - `SbcDbContext(DbContextOptions<SbcDbContext>)` with `DbSet<AppSetting> AppSettings`, `DbSet<Connection> Connections`, `DbSet<AuditEntry> AuditEntries`, `DbSet<PluginDocument> PluginDocuments`.
  - Entities exactly as coded below.
  - Test helper `TestDb : IDbContextFactory<SbcDbContext>, IDisposable` (in-memory SQLite, schema created).

- [ ] **Step 1: Add EF packages**

```bash
dotnet add src/SbConsole.Core package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/SbConsole.Core package Microsoft.EntityFrameworkCore.Design
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/SbConsole.Core.Tests/TestDb.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;

namespace SbConsole.Core.Tests;

/// <summary>In-memory SQLite database that lives as long as the open connection.</summary>
public sealed class TestDb : IDbContextFactory<SbcDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<SbcDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SbcDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
```

```csharp
// tests/SbConsole.Core.Tests/Data/SbcDbContextTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Data;

public class SbcDbContextTests
{
    [Fact]
    public async Task Can_roundtrip_all_entities()
    {
        using var testDb = new TestDb();
        var connectionId = Guid.NewGuid();

        await using (var db = testDb.CreateDbContext())
        {
            db.AppSettings.Add(new AppSetting { Key = "theme", Value = "dark" });
            db.Connections.Add(new Connection
            {
                Id = connectionId,
                Name = "prod-bus",
                Kind = "azure-servicebus",
                TagsCsv = "prod,east",
                SecretCiphertext = [1, 2, 3],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow,
                Actor = "admin",
                Action = "connection.create",
                Target = "prod-bus",
                Risk = ActionRisk.Mutating,
                Succeeded = true,
            });
            db.PluginDocuments.Add(new PluginDocument { PluginId = "servicebus", Key = "prefs", Json = "{}" });
            await db.SaveChangesAsync();
        }

        await using (var db = testDb.CreateDbContext())
        {
            (await db.AppSettings.SingleAsync(s => s.Key == "theme")).Value.Should().Be("dark");
            var conn = await db.Connections.SingleAsync(c => c.Id == connectionId);
            conn.Tags.Should().Equal("prod", "east");
            (await db.AuditEntries.SingleAsync()).Risk.Should().Be(ActionRisk.Mutating);
            (await db.PluginDocuments.SingleAsync()).PluginId.Should().Be("servicebus");
        }
    }

    [Fact]
    public async Task Connection_names_are_unique()
    {
        using var testDb = new TestDb();
        await using var db = testDb.CreateDbContext();
        db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "k", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "k", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter SbcDbContextTests`
Expected: FAIL to compile.

- [ ] **Step 4: Write entities and DbContext**

```csharp
// src/SbConsole.Core/Data/Entities/AppSetting.cs
namespace SbConsole.Core.Data.Entities;

public sealed class AppSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
```

```csharp
// src/SbConsole.Core/Data/Entities/Connection.cs
namespace SbConsole.Core.Data.Entities;

public sealed class Connection
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string TagsCsv { get; set; } = "";
    public required byte[] SecretCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public IReadOnlyList<string> Tags => TagsCsv.Length == 0 ? [] : TagsCsv.Split(',');
}
```

```csharp
// src/SbConsole.Core/Data/Entities/AuditEntry.cs
using SbConsole.Sdk;

namespace SbConsole.Core.Data.Entities;

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public ActionRisk Risk { get; set; }
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
}
```

```csharp
// src/SbConsole.Core/Data/Entities/PluginDocument.cs
namespace SbConsole.Core.Data.Entities;

public sealed class PluginDocument
{
    public required string PluginId { get; set; }
    public required string Key { get; set; }
    public required string Json { get; set; }
}
```

```csharp
// src/SbConsole.Core/Data/SbcDbContext.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Data;

public sealed class SbcDbContext(DbContextOptions<SbcDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<PluginDocument> PluginDocuments => Set<PluginDocument>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppSetting>().HasKey(s => s.Key);
        builder.Entity<Connection>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Name).IsUnique();
            e.Ignore(c => c.Tags);
        });
        builder.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Risk).HasConversion<string>();
            e.HasIndex(a => a.At);
        });
        builder.Entity<PluginDocument>().HasKey(d => new { d.PluginId, d.Key });
    }
}
```

```csharp
// src/SbConsole.Core/Data/SbcDbContextDesignFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SbConsole.Core.Data;

/// <summary>Used only by `dotnet ef` at design time.</summary>
public sealed class SbcDbContextDesignFactory : IDesignTimeDbContextFactory<SbcDbContext>
{
    public SbcDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite("Data Source=design.db").Options);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter SbcDbContextTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Generate the initial migration**

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/SbConsole.Core
```

Expected: `src/SbConsole.Core/Migrations/` appears and `dotnet build -warnaserror` stays green (EF-generated migration files compile clean under nullable + warnaserror; they carry their own `#nullable disable` pragma).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add EF Core model, SbcDbContext, initial migration"
```

---

### Task 6: Bootstrap options and ISettings

**Files:**
- Create: `src/SbConsole.Core/Settings/BootstrapOptions.cs`, `src/SbConsole.Core/Settings/ISettings.cs`, `src/SbConsole.Core/Settings/DbSettings.cs`
- Test: `tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs`, `tests/SbConsole.Core.Tests/Settings/DbSettingsTests.cs`

**Interfaces:**
- Consumes: `SbcDbContext`, `TestDb` (Task 5).
- Produces:
  - `record BootstrapOptions(string DbPath, byte[] DataKey, string AdminPassword, string ApiKey, string Bind)` with `static BootstrapOptions FromEnvironment(Func<string, string?> getEnv)`.
  - `interface ISettings { Task<string?> GetAsync(string key, CancellationToken ct = default); Task SetAsync(string key, string value, CancellationToken ct = default); }`
  - `DbSettings(IDbContextFactory<SbcDbContext>) : ISettings`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs
using System.Security.Cryptography;
using FluentAssertions;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Settings;

public class BootstrapOptionsTests
{
    private static readonly string ValidKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static Dictionary<string, string?> ValidEnv() => new()
    {
        ["SBC_DB_PATH"] = "/data/sbc.db",
        ["SBC_DATA_KEY"] = ValidKey,
        ["SBC_ADMIN_PASSWORD"] = "hunter2",
        ["SBC_API_KEY"] = "api-key",
        ["SBC_BIND"] = "http://0.0.0.0:8080",
    };

    [Fact]
    public void Reads_all_values_from_environment()
    {
        var env = ValidEnv();

        var options = BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        options.DbPath.Should().Be("/data/sbc.db");
        options.DataKey.Should().HaveCount(32);
        options.AdminPassword.Should().Be("hunter2");
        options.ApiKey.Should().Be("api-key");
        options.Bind.Should().Be("http://0.0.0.0:8080");
    }

    [Fact]
    public void Bind_defaults_when_missing()
    {
        var env = ValidEnv();
        env["SBC_BIND"] = null;

        BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Bind
            .Should().Be("http://localhost:5080");
    }

    [Theory]
    [InlineData("SBC_DB_PATH")]
    [InlineData("SBC_DATA_KEY")]
    [InlineData("SBC_ADMIN_PASSWORD")]
    [InlineData("SBC_API_KEY")]
    public void Missing_required_variable_throws_naming_it(string missing)
    {
        var env = ValidEnv();
        env[missing] = null;

        var act = () => BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    [Fact]
    public void Data_key_must_be_32_bytes()
    {
        var env = ValidEnv();
        env["SBC_DATA_KEY"] = Convert.ToBase64String(new byte[16]);

        var act = () => BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }
}
```

```csharp
// tests/SbConsole.Core.Tests/Settings/DbSettingsTests.cs
using FluentAssertions;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Settings;

public class DbSettingsTests
{
    [Fact]
    public async Task Get_returns_null_for_unknown_key()
    {
        using var testDb = new TestDb();
        var settings = new DbSettings(testDb);

        (await settings.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_get_roundtrips_and_set_overwrites()
    {
        using var testDb = new TestDb();
        var settings = new DbSettings(testDb);

        await settings.SetAsync("theme", "dark");
        await settings.SetAsync("theme", "light");

        (await settings.GetAsync("theme")).Should().Be("light");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "BootstrapOptionsTests|DbSettingsTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Settings/BootstrapOptions.cs
namespace SbConsole.Core.Settings;

/// <summary>The ONLY configuration read from environment variables. Everything else lives in AppSettings rows.</summary>
public sealed record BootstrapOptions(string DbPath, byte[] DataKey, string AdminPassword, string ApiKey, string Bind)
{
    public static BootstrapOptions FromEnvironment(Func<string, string?> getEnv)
    {
        string Require(string name) =>
            getEnv(name) ?? throw new InvalidOperationException($"Missing required environment variable {name}.");

        byte[] dataKey;
        try
        {
            dataKey = Convert.FromBase64String(Require("SBC_DATA_KEY"));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("SBC_DATA_KEY must be base64-encoded (32 bytes).");
        }

        if (dataKey.Length != 32)
        {
            throw new InvalidOperationException("SBC_DATA_KEY must decode to exactly 32 bytes.");
        }

        return new BootstrapOptions(
            DbPath: Require("SBC_DB_PATH"),
            DataKey: dataKey,
            AdminPassword: Require("SBC_ADMIN_PASSWORD"),
            ApiKey: Require("SBC_API_KEY"),
            Bind: getEnv("SBC_BIND") ?? "http://localhost:5080");
    }
}
```

```csharp
// src/SbConsole.Core/Settings/ISettings.cs
namespace SbConsole.Core.Settings;

public interface ISettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
```

```csharp
// src/SbConsole.Core/Settings/DbSettings.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Settings;

public sealed class DbSettings(IDbContextFactory<SbcDbContext> dbFactory) : ISettings
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "BootstrapOptionsTests|DbSettingsTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add bootstrap options and DB-backed settings"
```

---

### Task 7: Audit writer

**Files:**
- Create: `src/SbConsole.Core/Audit/IAuditWriter.cs`, `src/SbConsole.Core/Audit/EfAuditWriter.cs`
- Test: `tests/SbConsole.Core.Tests/Audit/EfAuditWriterTests.cs`

**Interfaces:**
- Consumes: `SbcDbContext`, `AuditEntry`, `TestDb` (Task 5); `ActionRisk` (Task 2).
- Produces:
  - `interface IAuditWriter { Task WriteAsync(AuditEntry entry, CancellationToken ct = default); }`
  - `EfAuditWriter(IDbContextFactory<SbcDbContext>) : IAuditWriter`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Core.Tests/Audit/EfAuditWriterTests.cs
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Audit;

public class EfAuditWriterTests
{
    [Fact]
    public async Task Persists_the_entry()
    {
        using var testDb = new TestDb();
        var writer = new EfAuditWriter(testDb);

        await writer.WriteAsync(new AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            Actor = "admin",
            Action = "queue.purge",
            Target = "orders/$deadletter",
            Risk = ActionRisk.Destructive,
            Succeeded = false,
            Detail = "confirmation mismatch",
        });

        await using var db = testDb.CreateDbContext();
        var saved = await db.AuditEntries.SingleAsync();
        saved.Action.Should().Be("queue.purge");
        saved.Risk.Should().Be(ActionRisk.Destructive);
        saved.Succeeded.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfAuditWriterTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Audit/IAuditWriter.cs
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Audit;

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}
```

```csharp
// src/SbConsole.Core/Audit/EfAuditWriter.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Audit;

public sealed class EfAuditWriter(IDbContextFactory<SbcDbContext> dbFactory) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfAuditWriterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add EF-backed audit writer"
```

---

### Task 8: Connection handlers and connection provider

**Files:**
- Create: `src/SbConsole.Core/Connections/CreateConnectionCommandHandler.cs`, `.../ListConnectionsQueryHandler.cs`, `.../DeleteConnectionCommandHandler.cs`, `.../EfConnectionProvider.cs`
- Test: `tests/SbConsole.Core.Tests/Connections/CreateConnectionCommandHandlerTests.cs`, `.../ListConnectionsQueryHandlerTests.cs`, `.../DeleteConnectionCommandHandlerTests.cs`, `.../EfConnectionProviderTests.cs`

**Interfaces:**
- Consumes: Tasks 3–7 (`Result<T>`, `ISecretProtector`, `SbcDbContext`, `IAuditWriter`, `TestDb`), Sdk `ConnectionInfo`/`IConnectionProvider`.
- Produces:
  - `record CreateConnectionCommand(string Name, string Kind, string Secret, IReadOnlyList<string> Tags, string Actor)`
  - `CreateConnectionCommandHandler(IDbContextFactory<SbcDbContext>, ISecretProtector, IAuditWriter, TimeProvider)` with `Task<Result<Guid>> HandleAsync(CreateConnectionCommand cmd, CancellationToken ct = default)`
  - `ListConnectionsQueryHandler(IDbContextFactory<SbcDbContext>)` with `Task<IReadOnlyList<ConnectionInfo>> HandleAsync(string? kind = null, CancellationToken ct = default)`
  - `record DeleteConnectionCommand(Guid Id, string Actor)`
  - `DeleteConnectionCommandHandler(IDbContextFactory<SbcDbContext>, IAuditWriter, TimeProvider)` with `Task<Result> HandleAsync(DeleteConnectionCommand cmd, CancellationToken ct = default)`
  - `EfConnectionProvider(IDbContextFactory<SbcDbContext>, ISecretProtector) : IConnectionProvider`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Connections/CreateConnectionCommandHandlerTests.cs
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

public class CreateConnectionCommandHandlerTests
{
    private static readonly byte[] Key = new byte[32];

    private static CreateConnectionCommandHandler Handler(TestDb db, IAuditWriter audit) =>
        new(db, new AesGcmSecretProtector(Key), audit, new FakeTimeProvider());

    [Fact]
    public async Task Creates_connection_with_encrypted_secret_and_audits()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();

        var result = await Handler(testDb, audit).HandleAsync(
            new CreateConnectionCommand("dev-bus", "azure-servicebus", "Endpoint=sb://x", ["dev"], "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync();
        saved.Name.Should().Be("dev-bus");
        saved.SecretCiphertext.Should().NotBeEquivalentTo(System.Text.Encoding.UTF8.GetBytes("Endpoint=sb://x"));
        new AesGcmSecretProtector(Key).Unprotect(saved.SecretCiphertext).Should().Be("Endpoint=sb://x");
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.create" && a.Target == "dev-bus"
                && a.Actor == "admin" && a.Risk == ActionRisk.Mutating && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_name_returns_conflict_and_does_not_audit_success()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var handler = Handler(testDb, audit);
        await handler.HandleAsync(new CreateConnectionCommand("dup", "k", "s", [], "admin"));

        var result = await handler.HandleAsync(new CreateConnectionCommand("dup", "k", "s2", [], "admin"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Category.Should().Be(ErrorCategory.Conflict);
    }
}
```

```csharp
// tests/SbConsole.Core.Tests/Connections/ListConnectionsQueryHandlerTests.cs
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Connections;

public class ListConnectionsQueryHandlerTests
{
    [Fact]
    public async Task Lists_metadata_without_secrets_filtered_by_kind()
    {
        using var testDb = new TestDb();
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), Substitute.For<IAuditWriter>(), new FakeTimeProvider());
        await create.HandleAsync(new CreateConnectionCommand("bus-prod", "azure-servicebus", "s1", ["prod"], "admin"));
        await create.HandleAsync(new CreateConnectionCommand("other", "postgres", "s2", [], "admin"));

        var all = await new ListConnectionsQueryHandler(testDb).HandleAsync();
        var buses = await new ListConnectionsQueryHandler(testDb).HandleAsync("azure-servicebus");

        all.Should().HaveCount(2);
        buses.Should().ContainSingle(c => c.Name == "bus-prod" && c.IsProd);
    }
}
```

```csharp
// tests/SbConsole.Core.Tests/Connections/DeleteConnectionCommandHandlerTests.cs
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

public class DeleteConnectionCommandHandlerTests
{
    [Fact]
    public async Task Deletes_and_audits_as_destructive()
    {
        using var testDb = new TestDb();
        var audit = Substitute.For<IAuditWriter>();
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), audit, new FakeTimeProvider());
        var created = await create.HandleAsync(new CreateConnectionCommand("gone", "k", "s", [], "admin"));

        var result = await new DeleteConnectionCommandHandler(testDb, audit, new FakeTimeProvider())
            .HandleAsync(new DeleteConnectionCommand(created.Value, "admin"));

        result.IsSuccess.Should().BeTrue();
        await using var db = testDb.CreateDbContext();
        (await db.Connections.CountAsync()).Should().Be(0);
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(a => a.Action == "connection.delete" && a.Risk == ActionRisk.Destructive && a.Succeeded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_id_returns_not_found()
    {
        using var testDb = new TestDb();

        var result = await new DeleteConnectionCommandHandler(testDb, Substitute.For<IAuditWriter>(), new FakeTimeProvider())
            .HandleAsync(new DeleteConnectionCommand(Guid.NewGuid(), "admin"));

        result.Error!.Category.Should().Be(ErrorCategory.NotFound);
    }
}
```

```csharp
// tests/SbConsole.Core.Tests/Connections/EfConnectionProviderTests.cs
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Connections;

public class EfConnectionProviderTests
{
    [Fact]
    public async Task Get_secret_decrypts_just_in_time_and_returns_null_for_unknown()
    {
        using var testDb = new TestDb();
        var protector = new AesGcmSecretProtector(new byte[32]);
        var create = new CreateConnectionCommandHandler(testDb, protector, Substitute.For<IAuditWriter>(), new FakeTimeProvider());
        var created = await create.HandleAsync(new CreateConnectionCommand("bus", "azure-servicebus", "Endpoint=sb://real", [], "admin"));
        var provider = new EfConnectionProvider(testDb, protector);

        (await provider.GetSecretAsync(created.Value)).Should().Be("Endpoint=sb://real");
        (await provider.GetSecretAsync(Guid.NewGuid())).Should().BeNull();
        (await provider.ListAsync("azure-servicebus")).Should().ContainSingle(c => c.Name == "bus");
    }
}
```

Note: `FakeTimeProvider` needs `dotnet add tests/SbConsole.Core.Tests package Microsoft.Extensions.TimeProvider.Testing` — run that first.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~Connections"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Connections/CreateConnectionCommandHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record CreateConnectionCommand(
    string Name, string Kind, string Secret, IReadOnlyList<string> Tags, string Actor);

public sealed class CreateConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result<Guid>> HandleAsync(CreateConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Connections.AnyAsync(c => c.Name == cmd.Name, ct))
        {
            return Result<Guid>.Fail(ErrorCategory.Conflict, $"A connection named '{cmd.Name}' already exists.");
        }

        var connection = new Data.Entities.Connection
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name,
            Kind = cmd.Kind,
            TagsCsv = string.Join(',', cmd.Tags),
            SecretCiphertext = protector.Protect(cmd.Secret),
            CreatedAt = clock.GetUtcNow(),
        };
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

```csharp
// src/SbConsole.Core/Connections/ListConnectionsQueryHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed class ListConnectionsQueryHandler(IDbContextFactory<SbcDbContext> dbFactory)
{
    public async Task<IReadOnlyList<ConnectionInfo>> HandleAsync(string? kind = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Connections.AsNoTracking();
        if (kind is not null)
        {
            query = query.Where(c => c.Kind == kind);
        }

        var rows = await query.OrderBy(c => c.Name).ToListAsync(ct);
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags)).ToList();
    }
}
```

```csharp
// src/SbConsole.Core/Connections/DeleteConnectionCommandHandler.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Audit;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Results;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed record DeleteConnectionCommand(Guid Id, string Actor);

public sealed class DeleteConnectionCommandHandler(
    IDbContextFactory<SbcDbContext> dbFactory,
    IAuditWriter audit,
    TimeProvider clock)
{
    public async Task<Result> HandleAsync(DeleteConnectionCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (connection is null)
        {
            return Result.Fail(ErrorCategory.NotFound, "Connection not found.");
        }

        db.Connections.Remove(connection);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = cmd.Actor,
            Action = "connection.delete",
            Target = connection.Name,
            Risk = ActionRisk.Destructive,
            Succeeded = true,
        }, ct);

        return Result.Ok();
    }
}
```

```csharp
// src/SbConsole.Core/Connections/EfConnectionProvider.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Security;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

public sealed class EfConnectionProvider(
    IDbContextFactory<SbcDbContext> dbFactory,
    ISecretProtector protector) : IConnectionProvider
{
    public async Task<IReadOnlyList<ConnectionInfo>> ListAsync(string kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Connections.AsNoTracking()
            .Where(c => c.Kind == kind)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        return rows.Select(c => new ConnectionInfo(c.Id, c.Name, c.Kind, c.Tags)).ToList();
    }

    public async Task<string?> GetSecretAsync(Guid connectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct);
        return row is null ? null : protector.Unprotect(row.SecretCiphertext);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~Connections"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add connection handlers and Sdk connection provider"
```

---

### Task 9: Plugin store

**Files:**
- Create: `src/SbConsole.Core/Plugins/EfPluginStore.cs`, `src/SbConsole.Core/Plugins/IPluginStoreFactory.cs`
- Test: `tests/SbConsole.Core.Tests/Plugins/EfPluginStoreTests.cs`

**Interfaces:**
- Consumes: `SbcDbContext`, `PluginDocument`, `TestDb` (Task 5); Sdk `IPluginStore`.
- Produces:
  - `interface IPluginStoreFactory { IPluginStore For(string pluginId); }`
  - `EfPluginStoreFactory(IDbContextFactory<SbcDbContext>) : IPluginStoreFactory`
  - `EfPluginStore : IPluginStore` (internal to Core; constructed only by the factory).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Core.Tests/Plugins/EfPluginStoreTests.cs
using FluentAssertions;
using SbConsole.Core.Plugins;

namespace SbConsole.Core.Tests.Plugins;

public class EfPluginStoreTests
{
    [Fact]
    public async Task Set_get_delete_roundtrip()
    {
        using var testDb = new TestDb();
        var store = new EfPluginStoreFactory(testDb).For("servicebus");

        (await store.GetAsync("prefs")).Should().BeNull();
        await store.SetAsync("prefs", """{"pageSize":50}""");
        (await store.GetAsync("prefs")).Should().Be("""{"pageSize":50}""");
        await store.SetAsync("prefs", """{"pageSize":100}""");
        (await store.GetAsync("prefs")).Should().Be("""{"pageSize":100}""");
        (await store.DeleteAsync("prefs")).Should().BeTrue();
        (await store.DeleteAsync("prefs")).Should().BeFalse();
    }

    [Fact]
    public async Task Stores_are_isolated_per_plugin()
    {
        using var testDb = new TestDb();
        var factory = new EfPluginStoreFactory(testDb);
        await factory.For("plugin-a").SetAsync("k", "a-value");

        (await factory.For("plugin-b").GetAsync("k")).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfPluginStoreTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the implementation**

```csharp
// src/SbConsole.Core/Plugins/IPluginStoreFactory.cs
using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public interface IPluginStoreFactory
{
    IPluginStore For(string pluginId);
}
```

```csharp
// src/SbConsole.Core/Plugins/EfPluginStore.cs
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Sdk;

namespace SbConsole.Core.Plugins;

public sealed class EfPluginStoreFactory(IDbContextFactory<SbcDbContext> dbFactory) : IPluginStoreFactory
{
    public IPluginStore For(string pluginId) => new EfPluginStore(dbFactory, pluginId);
}

internal sealed class EfPluginStore(IDbContextFactory<SbcDbContext> dbFactory, string pluginId) : IPluginStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        return doc?.Json;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        if (doc is null)
        {
            db.PluginDocuments.Add(new PluginDocument { PluginId = pluginId, Key = key, Json = value });
        }
        else
        {
            doc.Json = value;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.PluginDocuments.SingleOrDefaultAsync(d => d.PluginId == pluginId && d.Key == key, ct);
        if (doc is null)
        {
            return false;
        }

        db.PluginDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter EfPluginStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add per-plugin store with isolation"
```

---

### Task 10: Web shell — MudBlazor, plugin registry, DI wiring

**Files:**
- Create: `src/SbConsole.Web/Plugins/PluginRegistry.cs`, `src/SbConsole.Web/Plugins/PluginServiceCollectionExtensions.cs`, `src/SbConsole.Web/Components/Layout/NavMenu.razor`
- Modify: `src/SbConsole.Web/Program.cs`, `src/SbConsole.Web/Components/App.razor`, `src/SbConsole.Web/Components/Layout/MainLayout.razor`, `src/SbConsole.Web/Components/_Imports.razor`
- Test: `tests/SbConsole.Web.Tests/NavMenuTests.cs` (delete `UnitTest1.cs`)

**Interfaces:**
- Consumes: Sdk `IPlugin`, `PluginNavItem`; Core `BootstrapOptions`, `SbcDbContext`, all Core services (Tasks 5–9).
- Produces:
  - `PluginRegistry` with `IReadOnlyList<IPlugin> Plugins` and `IPlugin? Find(string pluginId)`.
  - `IServiceCollection AddSbConsolePlugin<TPlugin>(this IServiceCollection services) where TPlugin : class, IPlugin, new()` — instantiates the plugin, registers it as `IPlugin` singleton, calls its `ConfigureServices`, and registers a scoped `IPluginStore` bound to the plugin id (single-plugin simplification; switch to keyed services when plugin #2 arrives).
  - A running app shell at `/` with nav sections Dashboard, Connections, Audit, Settings + plugin nav groups.

- [ ] **Step 1: Add MudBlazor**

```bash
dotnet add src/SbConsole.Web package MudBlazor
```

- [ ] **Step 2: Write the failing bUnit test**

```csharp
// tests/SbConsole.Web.Tests/NavMenuTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Sdk;
using SbConsole.Web.Components.Layout;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class NavMenuTests : TestContext
{
    private sealed class FakePlugin : IPlugin
    {
        public string Id => "fake";
        public string DisplayName => "Fake Plugin";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/fake/queues")];
        public Type RootComponent => typeof(object);
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public void Renders_host_sections_and_plugin_nav_items()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new PluginRegistry([new FakePlugin()]));

        var cut = RenderComponent<NavMenu>();

        cut.Markup.Should().Contain("Connections");
        cut.Markup.Should().Contain("Audit");
        cut.Markup.Should().Contain("Fake Plugin");
        cut.Markup.Should().Contain("/p/fake/queues");
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests`
Expected: FAIL to compile — `PluginRegistry` and `NavMenu` do not exist.

- [ ] **Step 4: Write registry, extensions, shell**

```csharp
// src/SbConsole.Web/Plugins/PluginRegistry.cs
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public sealed class PluginRegistry(IEnumerable<IPlugin> plugins)
{
    public IReadOnlyList<IPlugin> Plugins { get; } = [.. plugins];

    public IPlugin? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
}
```

```csharp
// src/SbConsole.Web/Plugins/PluginServiceCollectionExtensions.cs
using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers a plugin at compile time. The scoped IPluginStore registration is a
    /// single-plugin simplification; move to keyed services when a second plugin arrives.
    /// </summary>
    public static IServiceCollection AddSbConsolePlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IPlugin, new()
    {
        var plugin = new TPlugin();
        services.AddSingleton<IPlugin>(plugin);
        services.AddScoped<IPluginStore>(sp => sp.GetRequiredService<IPluginStoreFactory>().For(plugin.Id));
        plugin.ConfigureServices(services);
        return services;
    }
}
```

```csharp
// src/SbConsole.Web/Program.cs
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Plugins;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components;
using SbConsole.Web.Plugins;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.WebHost.UseUrls(bootstrap.Bind);
builder.Services.AddSingleton(bootstrap);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<SbcDbContext>(o => o.UseSqlite($"Data Source={bootstrap.DbPath}"));
builder.Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(bootstrap.DataKey));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISettings, DbSettings>();
builder.Services.AddSingleton<IAuditWriter, EfAuditWriter>();
builder.Services.AddSingleton<IPluginStoreFactory, EfPluginStoreFactory>();
builder.Services.AddSingleton<IConnectionProvider, EfConnectionProvider>();
builder.Services.AddScoped<CreateConnectionCommandHandler>();
builder.Services.AddScoped<ListConnectionsQueryHandler>();
builder.Services.AddScoped<DeleteConnectionCommandHandler>();

// Plugins (compile-time registration; see docs/design.md §2)
builder.Services.AddSingleton(sp => new PluginRegistry(sp.GetServices<IPlugin>()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SbcDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
```

Note: no plugin is registered yet — `AddSbConsolePlugin<ServiceBusPlugin>()` lands in Plan 2. `PluginRegistry` handles the empty case.

```razor
@* src/SbConsole.Web/Components/_Imports.razor — ADD these lines to the template's existing ones *@
@using MudBlazor
@using SbConsole.Sdk
@using SbConsole.Web.Plugins
```

```razor
@* src/SbConsole.Web/Components/App.razor — replace the template's <head> additions and Routes line *@
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link rel="stylesheet" href="@Assets["_content/MudBlazor/MudBlazor.min.css"]" />
    <ImportMap />
    <HeadOutlet @rendermode="InteractiveServer" />
</head>

<body>
    <Routes @rendermode="InteractiveServer" />
    <script src="_content/MudBlazor/MudBlazor.min.js"></script>
    <script src="_framework/blazor.web.js"></script>
</body>

</html>
```

```razor
@* src/SbConsole.Web/Components/Layout/MainLayout.razor — replace entirely *@
@inherits LayoutComponentBase

<MudThemeProvider />
<MudPopoverProvider />
<MudDialogProvider />
<MudSnackbarProvider />

<MudLayout>
    <MudAppBar Elevation="1">
        <MudText Typo="Typo.h6">SbConsole</MudText>
    </MudAppBar>
    <MudDrawer Open="true" Elevation="1">
        <NavMenu />
    </MudDrawer>
    <MudMainContent Class="pa-4">
        @Body
    </MudMainContent>
</MudLayout>
```

```razor
@* src/SbConsole.Web/Components/Layout/NavMenu.razor *@
@inject PluginRegistry Registry

<MudNavMenu>
    <MudNavLink Href="/" Match="NavLinkMatch.All" Icon="@Icons.Material.Filled.Dashboard">Dashboard</MudNavLink>
    <MudNavLink Href="/connections" Icon="@Icons.Material.Filled.Cable">Connections</MudNavLink>
    <MudNavLink Href="/audit" Icon="@Icons.Material.Filled.History">Audit</MudNavLink>
    <MudNavLink Href="/settings" Icon="@Icons.Material.Filled.Settings">Settings</MudNavLink>

    @foreach (var plugin in Registry.Plugins)
    {
        <MudNavGroup Title="@plugin.DisplayName" Expanded="true">
            @foreach (var item in plugin.NavItems)
            {
                <MudNavLink Href="@item.Href" Icon="@(item.Icon ?? Icons.Material.Filled.Extension)">@item.Title</MudNavLink>
            }
        </MudNavGroup>
    }
</MudNavMenu>
```

Keep the template's `Routes.razor` and `Home.razor` as-is (Home can stay minimal; it is the Dashboard placeholder).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests`
Expected: PASS.

- [ ] **Step 6: Smoke-run the app**

```bash
SBC_DB_PATH=/tmp/sbc-dev.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8 && curl -s -o /dev/null -w "%{http_code}" http://localhost:5080/ && kill %1
```

Expected: `200`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add MudBlazor shell, plugin registry, DI wiring"
```

---

### Task 11: Auth — admin cookie login and API key filter

**Files:**
- Create: `src/SbConsole.Web/Auth/ApiKeyFilter.cs`, `src/SbConsole.Web/Components/Pages/Login.razor`
- Modify: `src/SbConsole.Web/Program.cs`
- Test: `tests/SbConsole.Web.Tests/ApiKeyFilterTests.cs`

**Interfaces:**
- Consumes: `BootstrapOptions` (Task 6).
- Produces:
  - `ApiKeyFilter(string expectedKey) : IEndpointFilter` — checks `X-Api-Key` header with constant-time comparison.
  - Cookie-authenticated UI (all pages require login except `/login`); `GET /api/v1/health` anonymous; API group pattern for Plan 3.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/ApiKeyFilterTests.cs
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using SbConsole.Web.Auth;

namespace SbConsole.Web.Tests;

public class ApiKeyFilterTests
{
    private static EndpointFilterInvocationContext Context(string? headerValue)
    {
        var http = new DefaultHttpContext();
        if (headerValue is not null)
        {
            http.Request.Headers["X-Api-Key"] = headerValue;
        }

        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static readonly EndpointFilterDelegate Next = _ => ValueTask.FromResult<object?>(Results.Ok("through"));

    [Fact]
    public async Task Correct_key_passes_through()
    {
        var result = await new ApiKeyFilter("expected").InvokeAsync(Context("expected"), Next);

        result.Should().NotBeOfType<UnauthorizedHttpResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("")]
    public async Task Missing_or_wrong_key_is_unauthorized(string? provided)
    {
        var result = await new ApiKeyFilter("expected").InvokeAsync(Context(provided), Next);

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ApiKeyFilterTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the filter, login page, and wiring**

```csharp
// src/SbConsole.Web/Auth/ApiKeyFilter.cs
using System.Security.Cryptography;
using System.Text;

namespace SbConsole.Web.Auth;

public sealed class ApiKeyFilter(string expectedKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided.ToString()),
                Encoding.UTF8.GetBytes(expectedKey)))
        {
            return TypedResults.Unauthorized();
        }

        return await next(context);
    }
}
```

```razor
@* src/SbConsole.Web/Components/Pages/Login.razor *@
@page "/login"
@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]
@layout SbConsole.Web.Components.Layout.EmptyLayout

<PageTitle>Sign in — SbConsole</PageTitle>

<div style="display:flex;justify-content:center;margin-top:15vh">
    <MudPaper Class="pa-8" Style="width:360px">
        <MudText Typo="Typo.h5" Class="mb-4">SbConsole</MudText>
        <form method="post" action="/auth/login">
            <input type="password" name="password" placeholder="Admin password"
                   style="width:100%;padding:8px;margin-bottom:12px" autofocus />
            <button type="submit" style="width:100%;padding:8px">Sign in</button>
        </form>
        @if (Failed)
        {
            <MudText Color="Color.Error" Class="mt-2">Wrong password.</MudText>
        }
    </MudPaper>
</div>

@code {
    [SupplyParameterFromQuery(Name = "failed")]
    public bool Failed { get; set; }
}
```

Also create the referenced empty layout:

```razor
@* src/SbConsole.Web/Components/Layout/EmptyLayout.razor *@
@inherits LayoutComponentBase
<MudThemeProvider />
@Body
```

Modify `Program.cs` — add after `builder.Services.AddMudServices();`:

```csharp
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "sbc.auth";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddCascadingAuthenticationState();
```

And after `app.UseAntiforgery();` add `app.UseAuthentication(); app.UseAuthorization();` — order note: in .NET 10 minimal hosting, authentication/authorization middleware are auto-added when services are registered, but keep the explicit calls BEFORE `UseAntiforgery()` if the build warns about ordering; the safe explicit order is:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
```

Add the auth endpoints and health endpoint before `app.Run();`:

```csharp
app.MapPost("/auth/login", async (HttpContext http, BootstrapOptions options) =>
{
    var form = await http.Request.ReadFormAsync();
    var password = form["password"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(options.AdminPassword)))
    {
        return Results.Redirect("/login?failed=true");
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).AllowAnonymous().DisableAntiforgery(); // pre-auth form; password is the only field, no session to ride

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
```

With `using` additions at the top of `Program.cs`:

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
```

- [ ] **Step 4: Run all tests**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 5: Smoke-test auth flow**

```bash
SBC_DB_PATH=/tmp/sbc-dev.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8
curl -s -o /dev/null -w "root:%{http_code}\n" -L http://localhost:5080/            # expect 200 after redirect to /login
curl -s -o /dev/null -w "health:%{http_code}\n" http://localhost:5080/api/v1/health # expect 200
curl -s -o /dev/null -w "login:%{http_code}\n" -d "password=dev" http://localhost:5080/auth/login # expect 302
kill %1
```

Expected: `root:200` (login page), `health:200`, `login:302`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add admin cookie auth, API key filter, health endpoint"
```

---

## After this plan

- **Plan 2 (next):** `SbConsole.Plugins.ServiceBus` — plugin class, entity browse/CRUD pages, peek/send/DLQ message tooling, `AddSbConsolePlugin<ServiceBusPlugin>()` wiring, host Connections/Audit pages.
- **Plan 3:** automation API endpoints under `/api/v1` with `ApiKeyFilter` + `?confirm=` on destructive routes, typed-confirmation dialog component, `SbConsole.IntegrationTests` with Testcontainers + Service Bus emulator.
