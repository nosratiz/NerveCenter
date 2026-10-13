# SQLite Plugin (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship SbConsole's sixth plugin — a read-only SQLite file inspector with an Overview page (file, journal, page cache, integrity check, size by table) and a Tables browser (schema, columns with sampled notes, indexes, FKs, DDL, data preview, exact count, CSV export) — guarded by a fail-closed host path allow-list.

**Architecture:** A new `SbConsole.Plugins.Sqlite` project (references `SbConsole.Sdk` only) talks to SQLite files through `Microsoft.Data.Sqlite`, behind an `ISqliteOperations` wrapper that pages/handlers use and unit tests substitute. Every open goes through a new SDK seam, `IHostPathPolicy`, implemented in Core (`HostPathPolicy`) from `SBC_SQLITE_ROOTS`, always refusing SbConsole's own DB. Because the host constructs plugins with `new()`, a new optional `IPlugin.Initialize(IServiceProvider)` hook (called once after `app.Build()`) lets `SqlitePlugin` resolve its operations for `TestConnectionAsync`/dashboard hooks.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor 9.9.0, Microsoft.Data.Sqlite 10.0.12 (bundled `e_sqlite3`, SQLite 3.53.3), xUnit 2.9.3 + FluentAssertions 7.2.0 + NSubstitute 6.2.0 + bUnit 2.10.3.

**Spec:** `docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md` (read §2–§9 before starting).

## Global Constraints

- Gate before every commit: `dotnet build -warnaserror` and `dotnet test` green. `TreatWarningsAsErrors=true` — a warning fails the build.
- TDD: write the failing test first, run it, see it fail, then implement.
- Plugin project references `SbConsole.Sdk` only (plus NuGet packages). No plugin type is referenced from Core.
- Phase 1 is strictly read-only: every connection opens with `Mode=ReadOnly` + `Pooling=False`, whatever the stored `Mode` says.
- Every open checks `IHostPathPolicy` and opens the **resolved** path. With no roots configured, nothing opens (fail closed). SbConsole's own DB and its `-wal`/`-shm`/`-journal` are always refused.
- Extensions are never loaded (`load_extension` stays disabled — never call `EnableExtensions`).
- User-supplied identifiers are never interpolated into SQL except through `SqliteSql.QuoteIdentifier`, and only after the name was confirmed to exist in `sqlite_schema` (`RequireObject`).
- Statement timeout = linked CTS with `CancelAfter(StatementTimeoutS)` whose registration calls `SQLitePCL.raw.sqlite3_interrupt(connection.Handle)`. Passing a token to `Microsoft.Data.Sqlite` does **not** stop a running statement (verified 2026-10-13).
- `dbstat` is **not** compiled into the bundled SQLite (verified 2026-10-13); size panels must work without it.
- Raw exception text never reaches the UI: route every error through `FriendlySqliteError.From` (which falls back to the SDK's `FriendlyError`).
- Theming: colors only via `--mud-palette-*` tokens; every plugin CSS class is prefixed `sql-`; test selectors are prefixed `sqlite-`.
- DI by marker: handlers `IScopedService`, `SqliteOperations` `ISingletonService`. Keyed plugin store in a handler constructor: `[FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store`.
- Audit actions: `sqlite.integrity.check` (target = connection name), `sqlite.table.export` (target = `{connectionName}/{table}`), both `ActionRisk.Safe`.
- `PluginDashboardProblem.Severity` is only `"Error"` or `"Warning"`.
- Package versions: `Microsoft.Data.Sqlite` `10.0.12`, `MudBlazor` `9.9.0`; test packages exactly as `tests/SbConsole.Plugins.Redis.Tests/SbConsole.Plugins.Redis.Tests.csproj`.

## File Structure

```
src/SbConsole.Sdk/
  IHostPathPolicy.cs                         NEW  path allow-list seam + PathCheckResult
  IPlugin.cs                                 MOD  + default no-op Initialize(IServiceProvider)
src/SbConsole.Core/
  Settings/BootstrapOptions.cs               MOD  + SqliteRoots (from SBC_SQLITE_ROOTS)
  Security/HostPathPolicy.cs                 NEW  IHostPathPolicy implementation
src/SbConsole.Web/
  Plugins/PluginRegistry.cs                  MOD  + InitializeAll
  Program.cs                                 MOD  register SqlitePlugin, call InitializeAll
  SbConsole.Web.csproj                       MOD  project reference
  Properties/launchSettings.json.example     MOD  SBC_SQLITE_ROOTS
src/Plugins/SbConsole.Plugins.Sqlite/
  SbConsole.Plugins.Sqlite.csproj
  SqlitePlugin.cs                            identity, summary, Initialize, test, dashboard hooks
  PluginResult.cs, HandlerRunner.cs          result/try-audit plumbing (Redis pattern)
  Client/
    SqliteConnectionSettings.cs              secret parse/format + SqliteAccessMode
    SqliteFormat.cs                          bytes/count/approx/duration copy
    SqliteExceptions.cs                      path refused / timeout / object not found
    FriendlySqliteError.cs                   error → friendly text
    SqliteSql.cs                             command helpers + identifier quoting
    ISqliteOperations.cs, SqliteOperations.cs
    SqliteOverview.cs, SchemaModels.cs, RowModels.cs, IntegrityCheckOutcome.cs, CsvExport.cs, CsvFormat.cs
    SqliteConnectionFields.razor             connection form
  Overview/  GetOverviewQueryHandler, GetTableSizesQueryHandler, IntegrityResult, IntegrityResultStore,
             RunIntegrityCheckCommandHandler, GetLastIntegrityResultQueryHandler
  Tables/    ListSchemaQueryHandler, GetTableDetailQueryHandler, GetColumnSamplesQueryHandler,
             PreviewRowsQueryHandler, CountRowsQueryHandler, ExportTableCsvCommandHandler, ColumnNoteBuilder
  Components/ _Imports.razor, SqlitePageHeader.razor, SqliteConnectionPicker.razor, SqliteStatTile.razor
  Pages/     _Imports.razor, Overview.razor, Tables.razor
  wwwroot/   sqlite.css, sqlite.js
tests/SbConsole.Plugins.Sqlite.Tests/        mirrors the above (Client/, Overview/, Tables/, Pages/)
tests/SbConsole.Core.Tests/Security/HostPathPolicyTests.cs
tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs   MOD
tests/SbConsole.Web.Tests/PluginRegistryInitializeTests.cs
tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs        MOD
docker/sqlite/seed.py, docker-compose.yml, docker/README.md, .env.example, docs/design.md, CLAUDE.md
```

---

### Task 1: Host path policy (`IHostPathPolicy` + `HostPathPolicy` + `SBC_SQLITE_ROOTS`)

**Files:**
- Create: `src/SbConsole.Sdk/IHostPathPolicy.cs`
- Modify: `src/SbConsole.Core/Settings/BootstrapOptions.cs`
- Create: `src/SbConsole.Core/Security/HostPathPolicy.cs`
- Modify: `src/SbConsole.Web/Properties/launchSettings.json.example`
- Test: `tests/SbConsole.Core.Tests/Security/HostPathPolicyTests.cs`, `tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs`

**Interfaces:**
- Produces: `IHostPathPolicy.Check(string path) : PathCheckResult`; `PathCheckResult(bool Allowed, string? ResolvedPath, string? Reason)` with `PathCheckResult.Allow(string)` / `PathCheckResult.Refuse(string)`; `BootstrapOptions.SqliteRoots : IReadOnlyList<string>`; `HostPathPolicy(BootstrapOptions)` (`ISingletonService`, so registered as `IHostPathPolicy`); `HostPathPolicy.ResolveReal(string) : string` (internal static, used by tests).

- [ ] **Step 1: Write the failing BootstrapOptions tests**

Append to `tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs` (inside the class, which already has `ValidEnv()`):

```csharp
    [Fact]
    public void Sqlite_roots_default_to_empty_so_no_sqlite_file_can_be_opened()
    {
        var env = ValidEnv();

        BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k)).SqliteRoots.Should().BeEmpty();
    }

    [Fact]
    public void Sqlite_roots_are_split_on_semicolons_trimmed_and_blanks_dropped()
    {
        var env = ValidEnv();
        env["SBC_SQLITE_ROOTS"] = " /sqlite ; ;/backups/reports ";

        BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k)).SqliteRoots
            .Should().Equal("/sqlite", "/backups/reports");
    }
```

- [ ] **Step 2: Write the failing HostPathPolicy tests**

Create `tests/SbConsole.Core.Tests/Security/HostPathPolicyTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Security;

public sealed class HostPathPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbc-paths-").FullName;

    public HostPathPolicyTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(_dir, "data2"));
        Directory.CreateDirectory(Path.Combine(_dir, "secret"));
    }

    private string Root => Path.Combine(_dir, "data");

    private HostPathPolicy Policy(string? dbPath = null, params string[] roots) =>
        new(new BootstrapOptions(dbPath ?? Path.Combine(_dir, "host", "sbconsole.db"), new byte[32], "pw", "key", "http://localhost")
        {
            SqliteRoots = roots,
        });

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Refuses_everything_when_no_roots_are_configured()
    {
        var file = Touch("data/reports.db");

        var result = Policy().Check(file);

        result.Allowed.Should().BeFalse();
        result.ResolvedPath.Should().BeNull();
        result.Reason.Should().Contain("SBC_SQLITE_ROOTS");
    }

    [Fact]
    public void Allows_a_file_under_a_root_and_returns_its_resolved_path()
    {
        var file = Touch("data/reports.db");

        var result = Policy(null, Root).Check(file);

        result.Allowed.Should().BeTrue();
        result.ResolvedPath.Should().Be(HostPathPolicy.ResolveReal(file));
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void Allows_a_file_under_any_of_several_roots()
    {
        var file = Touch("data2/other.db");

        Policy(null, Root, Path.Combine(_dir, "data2")).Check(file).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_sibling_directory_that_shares_the_roots_prefix()
    {
        var file = Touch("data2/reports.db");

        var result = Policy(null, Root).Check(file);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("outside the allowed SQLite roots");
    }

    [Fact]
    public void Refuses_dot_dot_traversal_out_of_a_root()
    {
        Touch("secret/keys.db");

        Policy(null, Root).Check(Path.Combine(Root, "..", "secret", "keys.db")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_symlinked_file_that_points_outside_the_root()
    {
        var outside = Touch("secret/keys.db");
        var link = Path.Combine(Root, "innocent.db");
        File.CreateSymbolicLink(link, outside);

        Policy(null, Root).Check(link).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_file_under_a_symlinked_directory_that_points_outside_the_root()
    {
        Touch("secret/keys.db");
        Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), Path.Combine(_dir, "secret"));

        Policy(null, Root).Check(Path.Combine(Root, "sub", "keys.db")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Allows_a_symlink_that_stays_inside_the_root()
    {
        var target = Touch("data/real.db");
        var link = Path.Combine(Root, "alias.db");
        File.CreateSymbolicLink(link, target);

        var result = Policy(null, Root).Check(link);

        result.Allowed.Should().BeTrue();
        result.ResolvedPath.Should().Be(HostPathPolicy.ResolveReal(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void Refuses_the_hosts_own_database_and_its_sidecar_files_even_under_a_root(string suffix)
    {
        var dbPath = Path.Combine(Root, "sbconsole.db");
        var file = Touch("data/sbconsole.db" + suffix);

        var result = Policy(dbPath, Root).Check(file);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("SbConsole itself");
    }

    [Fact]
    public void Refuses_an_empty_path()
    {
        Policy(null, Root).Check("  ").Allowed.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp dir cleanup is best effort.
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~HostPathPolicyTests|FullyQualifiedName~BootstrapOptionsTests"`
Expected: build FAILS — `HostPathPolicy` / `SqliteRoots` do not exist.

- [ ] **Step 4: Add the SDK seam**

Create `src/SbConsole.Sdk/IHostPathPolicy.cs`:

```csharp
namespace SbConsole.Sdk;

/// <summary>
/// Decides whether a plugin may open a file on the SbConsole host (design spec
/// docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §3). Implemented by the host, which
/// alone knows its allowed roots and where its own database lives. Callers must open
/// <see cref="PathCheckResult.ResolvedPath"/>, never the path they passed in, so a symlink swapped
/// in after the check can't redirect the open.
/// </summary>
public interface IHostPathPolicy
{
    PathCheckResult Check(string path);
}

public sealed record PathCheckResult(bool Allowed, string? ResolvedPath, string? Reason)
{
    public static PathCheckResult Allow(string resolvedPath) => new(true, resolvedPath, null);

    public static PathCheckResult Refuse(string reason) => new(false, null, reason);
}
```

- [ ] **Step 5: Add `SqliteRoots` to BootstrapOptions**

In `src/SbConsole.Core/Settings/BootstrapOptions.cs`, add inside the record body (before `FromEnvironment`):

```csharp
    /// <summary>
    /// Directories SQLite connections may open files under (SBC_SQLITE_ROOTS, ';'-separated).
    /// Empty means no SQLite file can be opened at all -- HostPathPolicy fails closed.
    /// </summary>
    public IReadOnlyList<string> SqliteRoots { get; init; } = [];
```

Change the `return new BootstrapOptions(...)` statement in `FromEnvironment` to end with an initializer:

```csharp
        return new BootstrapOptions(
            DbPath: Require("SBC_DB_PATH"),
            DataKey: dataKey,
            AdminPassword: Require("SBC_ADMIN_PASSWORD"),
            ApiKey: Require("SBC_API_KEY"),
            Bind: getEnv("SBC_BIND") ?? "http://localhost:5080")
        {
            SqliteRoots = ParseRoots(getEnv("SBC_SQLITE_ROOTS")),
        };
```

Add below `FromEnvironment`:

```csharp
    private static IReadOnlyList<string> ParseRoots(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
```

Replace `ToString()` with:

```csharp
    public override string ToString() =>
        $"BootstrapOptions {{ DbPath = {DbPath}, DataKey = [REDACTED], AdminPassword = [REDACTED], ApiKey = [REDACTED], Bind = {Bind}, SqliteRoots = [{string.Join(";", SqliteRoots)}] }}";
```

- [ ] **Step 6: Implement HostPathPolicy**

Create `src/SbConsole.Core/Security/HostPathPolicy.cs`:

```csharp
using SbConsole.Core.Settings;
using SbConsole.Sdk;

namespace SbConsole.Core.Security;

/// <summary>
/// The host's file allow-list for plugins that open server-side files (today: SQLite). A file is
/// allowed only if, after collapsing ".." and following every symlink on the file and on each
/// parent directory, it sits under one of BootstrapOptions.SqliteRoots (separator-aware, so
/// "/data2/x" is not under "/data"). SbConsole's own database and its -wal/-shm/-journal siblings
/// are always refused, even under a root. No roots configured = everything refused. See
/// docs/design.md §15 and the SQLite plugin design spec §3.
/// </summary>
public sealed class HostPathPolicy(BootstrapOptions options) : IHostPathPolicy, ISingletonService
{
    private const int MaxSymlinkHops = 40;

    public PathCheckResult Check(string path)
    {
        if (options.SqliteRoots.Count == 0)
        {
            return PathCheckResult.Refuse("No SQLite roots are configured — set SBC_SQLITE_ROOTS.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return PathCheckResult.Refuse("A file path is required.");
        }

        string resolved;
        try
        {
            resolved = ResolveReal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PathCheckResult.Refuse("The path could not be resolved.");
        }

        if (IsHostDatabase(resolved))
        {
            return PathCheckResult.Refuse("That file belongs to SbConsole itself and can't be opened as a SQLite connection.");
        }

        foreach (var root in options.SqliteRoots)
        {
            string resolvedRoot;
            try
            {
                resolvedRoot = ResolveReal(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (IsUnder(resolved, resolvedRoot))
            {
                return PathCheckResult.Allow(resolved);
            }
        }

        return PathCheckResult.Refuse($"The path is outside the allowed SQLite roots ({string.Join(", ", options.SqliteRoots)}).");
    }

    /// <summary>
    /// Absolute path with ".." collapsed and every symlink component replaced by its target,
    /// re-resolving the target in turn (it may itself sit under a symlinked directory).
    /// Components that don't exist are kept as-is.
    /// </summary>
    internal static string ResolveReal(string path) => ResolveReal(path, 0);

    private static string ResolveReal(string path, int hops)
    {
        if (hops > MaxSymlinkHops)
        {
            throw new IOException("Too many levels of symbolic links.");
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? Path.DirectorySeparatorChar.ToString();
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(current, parts[i]);
            var linkTarget = new FileInfo(candidate).LinkTarget;
            if (linkTarget is null)
            {
                current = candidate;
                continue;
            }

            // A relative link target is relative to the directory holding the link.
            var targetFull = Path.GetFullPath(linkTarget, current);
            var rest = parts.Skip(i + 1).Prepend(targetFull).ToArray();
            return ResolveReal(Path.Combine(rest), hops + 1);
        }

        return current;
    }

    private static bool IsUnder(string path, string root)
    {
        var withSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(withSeparator, StringComparison.Ordinal);
    }

    // Case-insensitive on purpose: on a case-insensitive file system (macOS dev boxes) "/Data/x.db"
    // and "/data/x.db" are the same file, and refusing too much here is the safe direction.
    private bool IsHostDatabase(string resolved)
    {
        string db;
        try
        {
            db = ResolveReal(options.DbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            db = Path.GetFullPath(options.DbPath);
        }

        string[] own = [db, db + "-wal", db + "-shm", db + "-journal"];
        return own.Any(o => string.Equals(o, resolved, StringComparison.OrdinalIgnoreCase));
    }
}
```

`SbConsole.Core.Tests` must see `internal` members: check `src/SbConsole.Core/SbConsole.Core.csproj` for `<InternalsVisibleTo Include="SbConsole.Core.Tests" />`; if absent, add it in an `<ItemGroup>`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Core.Tests --filter "FullyQualifiedName~HostPathPolicyTests|FullyQualifiedName~BootstrapOptionsTests"`
Expected: PASS (all).

- [ ] **Step 8: Document the env var for `dotnet run`**

In `src/SbConsole.Web/Properties/launchSettings.json.example`, add to **both** profiles' `environmentVariables` (after `SBC_BIND`, adding the comma):

```json
        "SBC_SQLITE_ROOTS": "<absolute dir(s) holding SQLite files, ';'-separated -- e.g. the .sqlite-demo dir from docker/README.md>"
```

- [ ] **Step 9: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: build succeeds, all tests pass.

```bash
git add src/SbConsole.Sdk/IHostPathPolicy.cs src/SbConsole.Core/Settings/BootstrapOptions.cs src/SbConsole.Core/Security/HostPathPolicy.cs src/SbConsole.Core/SbConsole.Core.csproj src/SbConsole.Web/Properties/launchSettings.json.example tests/SbConsole.Core.Tests/Security/HostPathPolicyTests.cs tests/SbConsole.Core.Tests/Settings/BootstrapOptionsTests.cs
git commit -m "feat(core): fail-closed host path policy for plugin file access (SBC_SQLITE_ROOTS)"
```

---

### Task 2: `IPlugin.Initialize` hook

**Files:**
- Modify: `src/SbConsole.Sdk/IPlugin.cs`
- Modify: `src/SbConsole.Web/Plugins/PluginRegistry.cs`
- Modify: `src/SbConsole.Web/Program.cs`
- Test: `tests/SbConsole.Web.Tests/PluginRegistryInitializeTests.cs`

**Interfaces:**
- Produces: `IPlugin.Initialize(IServiceProvider services)` (default no-op); `PluginRegistry.InitializeAll(IServiceProvider services)`.

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Web.Tests/PluginRegistryInitializeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class PluginRegistryInitializeTests
{
    private sealed class RecordingPlugin(string id) : IPlugin
    {
        public IServiceProvider? InitializedWith { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public string Version => "0.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => id;
        public string ConnectionKindDisplayName => id;
        public PluginContribution Contribution => new(PageCount: 0, ActionCount: 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
        public void Initialize(IServiceProvider services) => InitializedWith = services;
    }

    [Fact]
    public void InitializeAll_hands_every_plugin_the_service_provider()
    {
        var first = new RecordingPlugin("a");
        var second = new RecordingPlugin("b");
        using var services = new ServiceCollection().BuildServiceProvider();

        new PluginRegistry([first, second]).InitializeAll(services);

        first.InitializedWith.Should().BeSameAs(services);
        second.InitializedWith.Should().BeSameAs(services);
    }

    [Fact]
    public void A_plugin_that_does_not_override_Initialize_is_unaffected()
    {
        IPlugin plugin = new SbConsole.Plugins.Redis.RedisPlugin();
        using var services = new ServiceCollection().BuildServiceProvider();

        var act = () => new PluginRegistry([plugin]).InitializeAll(services);

        act.Should().NotThrow();
    }
}
```

(`PluginContribution`'s positional parameter names are `PageCount`/`ActionCount` — confirm in `src/SbConsole.Sdk/PluginContribution.cs`; `PluginNavItem` lives in `SbConsole.Sdk`.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~PluginRegistryInitializeTests"`
Expected: build FAILS — `PluginRegistry.InitializeAll` does not exist.

- [ ] **Step 3: Add the SDK hook**

In `src/SbConsole.Sdk/IPlugin.cs`, add after `ConfigureServices`:

```csharp
    /// <summary>
    /// Optional: called once, after the host's service provider is built and before any request is
    /// served (Program.cs, via PluginRegistry.InitializeAll). The host constructs plugins with new()
    /// (AddSbConsolePlugin), so instance hooks like TestConnectionAsync and GetDashboardProblemsAsync
    /// have no DI access of their own; a plugin that needs a host service there (e.g. the SQLite
    /// plugin's IHostPathPolicy-guarded operations) resolves it here. Only singletons should be
    /// resolved -- this is the root provider. The default does nothing, so a plugin written before
    /// this member existed needs no change at all.
    /// </summary>
    void Initialize(IServiceProvider services) { }
```

- [ ] **Step 4: Add `InitializeAll`**

Replace `src/SbConsole.Web/Plugins/PluginRegistry.cs`'s class body so it reads:

```csharp
public sealed class PluginRegistry(IEnumerable<IPlugin> plugins)
{
    public IReadOnlyList<IPlugin> Plugins { get; } = [.. plugins];

    public IPlugin? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Calls IPlugin.Initialize on every registered plugin. Program.cs calls this once, right after Build().</summary>
    public void InitializeAll(IServiceProvider services)
    {
        foreach (var plugin in Plugins)
        {
            plugin.Initialize(services);
        }
    }
}
```

(Keep the file's existing `using`/namespace lines.)

- [ ] **Step 5: Call it from Program.cs**

In `src/SbConsole.Web/Program.cs`, directly after `var app = builder.Build();` add:

```csharp
// Plugins are constructed with new() (AddSbConsolePlugin), so any that need a host service in their
// instance hooks resolve it here -- see IPlugin.Initialize.
app.Services.GetRequiredService<PluginRegistry>().InitializeAll(app.Services);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~PluginRegistryInitializeTests"`
Expected: PASS.

- [ ] **Step 7: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

```bash
git add src/SbConsole.Sdk/IPlugin.cs src/SbConsole.Web/Plugins/PluginRegistry.cs src/SbConsole.Web/Program.cs tests/SbConsole.Web.Tests/PluginRegistryInitializeTests.cs
git commit -m "feat(sdk): optional IPlugin.Initialize hook called once after the host builds"
```

---

### Task 3: Plugin scaffold, connection settings, formatting, plugin identity

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/SbConsole.Plugins.Sqlite.csproj`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteConnectionSettings.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteFormat.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`
- Create: `tests/SbConsole.Plugins.Sqlite.Tests/SbConsole.Plugins.Sqlite.Tests.csproj`
- Modify: `SbConsole.slnx`, `src/SbConsole.Web/SbConsole.Web.csproj`, `src/SbConsole.Web/Program.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteConnectionSettingsTests.cs`, `.../Client/SqliteFormatTests.cs`, `.../SqlitePluginTests.cs`

**Interfaces:**
- Produces:
  - `enum SqliteAccessMode { ReadOnly, ReadWrite }`
  - `record SqliteConnectionSettings(string Path, SqliteAccessMode Mode = ReadOnly, int BusyTimeoutMs = 5000, int StatementTimeoutS = 30)` with `static Parse(string) /* throws FormatException */`, `ToSecretString() /* throws ArgumentException */`, consts `DefaultBusyTimeoutMs`, `DefaultStatementTimeoutS`.
  - `static class SqliteFormat { Bytes(long), Count(long), Approx(long), Duration(TimeSpan) }`
  - `SqlitePlugin` with `const string PluginId = "sqlite"`, `const string OverviewHref = "/p/sqlite/overview"`, `const string TablesHref = "/p/sqlite/tables"`, `static ConnectionTestResult NotInitialized`.

- [ ] **Step 1: Create the projects and wire them in**

Create `src/Plugins/SbConsole.Plugins.Sqlite/SbConsole.Plugins.Sqlite.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <ProjectReference Include="..\..\SbConsole.Sdk\SbConsole.Sdk.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- SqliteOperations' helpers and parsers are exercised directly by unit tests against
         temp-file databases (design spec §9). -->
    <InternalsVisibleTo Include="SbConsole.Plugins.Sqlite.Tests" />
  </ItemGroup>

  <ItemGroup>
    <!-- Same version Core gets transitively through Microsoft.EntityFrameworkCore.Sqlite, so the
         process loads one e_sqlite3 native build. -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />
    <PackageReference Include="MudBlazor" Version="9.9.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Pages/*.razor need Razor component + [Authorize] support from the shared framework. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

Create `tests/SbConsole.Plugins.Sqlite.Tests/SbConsole.Plugins.Sqlite.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="bunit" Version="2.10.3" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="FluentAssertions" Version="7.2.0" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NSubstitute" Version="6.2.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Plugins\SbConsole.Plugins.Sqlite\SbConsole.Plugins.Sqlite.csproj" />
  </ItemGroup>

</Project>
```

In `SbConsole.slnx` add (alphabetical within each folder):

```xml
    <Project Path="src/Plugins/SbConsole.Plugins.Sqlite/SbConsole.Plugins.Sqlite.csproj" />
```
```xml
    <Project Path="tests/SbConsole.Plugins.Sqlite.Tests/SbConsole.Plugins.Sqlite.Tests.csproj" />
```

In `src/SbConsole.Web/SbConsole.Web.csproj` add beside the other plugin references:

```xml
    <ProjectReference Include="..\Plugins\SbConsole.Plugins.Sqlite\SbConsole.Plugins.Sqlite.csproj" />
```

- [ ] **Step 2: Write the failing settings tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteConnectionSettingsTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteConnectionSettingsTests
{
    [Fact]
    public void Round_trips_through_the_secret_string()
    {
        var settings = new SqliteConnectionSettings("/data/reports.db", SqliteAccessMode.ReadWrite, 2500, 12);

        var secret = settings.ToSecretString();

        secret.Should().Be("Path=/data/reports.db;Mode=ReadWrite;BusyTimeoutMs=2500;StatementTimeoutS=12");
        SqliteConnectionSettings.Parse(secret).Should().Be(settings);
    }

    [Fact]
    public void Defaults_mode_and_timeouts_when_only_the_path_is_given()
    {
        var settings = SqliteConnectionSettings.Parse("Path=/data/reports.db");

        settings.Should().Be(new SqliteConnectionSettings("/data/reports.db", SqliteAccessMode.ReadOnly, 5000, 30));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_unknown_keys_are_ignored()
    {
        var settings = SqliteConnectionSettings.Parse("path=/x.db;MODE=readonly;Extra=1");

        settings.Path.Should().Be("/x.db");
        settings.Mode.Should().Be(SqliteAccessMode.ReadOnly);
    }

    [Theory]
    [InlineData("Mode=ReadOnly", "Path is required.")]
    [InlineData("Path=", "Path is required.")]
    [InlineData("Path=/x.db;Mode=Sideways", "Unknown Mode 'Sideways'.")]
    [InlineData("Path=/x.db;Mode=1", "Unknown Mode '1'.")]
    [InlineData("Path=/x.db;BusyTimeoutMs=soon", "BusyTimeoutMs must be a positive whole number.")]
    [InlineData("Path=/x.db;StatementTimeoutS=0", "StatementTimeoutS must be a positive whole number.")]
    [InlineData("Path=/x.db;garbage", "Expected key=value, got 'garbage'.")]
    public void Rejects_malformed_secrets(string secret, string message)
    {
        var act = () => SqliteConnectionSettings.Parse(secret);

        act.Should().Throw<FormatException>().WithMessage(message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/data/a;b.db")]
    public void Refuses_to_format_an_unrepresentable_path(string path)
    {
        var act = () => new SqliteConnectionSettings(path).ToSecretString();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_to_format_a_non_positive_timeout()
    {
        var act = () => new SqliteConnectionSettings("/x.db", BusyTimeoutMs: 0).ToSecretString();

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 3: Write the failing format tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteFormatTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteFormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(4096L, "4 KB")]
    [InlineData(65_536L, "64 KB")]
    [InlineData(50_331_648L, "48 MB")]
    [InlineData(1_395_864_371L, "1.3 GB")]
    [InlineData(2_254_857_830L, "2.1 GB")]
    public void Bytes_uses_binary_units_with_one_decimal_below_ten(long bytes, string expected) =>
        SqliteFormat.Bytes(bytes).Should().Be(expected);

    [Theory]
    [InlineData(412L, "412")]
    [InlineData(2_418_902L, "2.4M")]
    [InlineData(41_200_000L, "41.2M")]
    [InlineData(999_999L, "999,999")]
    public void Approx_compacts_millions(long rows, string expected) =>
        SqliteFormat.Approx(rows).Should().Be(expected);

    [Fact]
    public void Count_groups_thousands() => SqliteFormat.Count(2_418_902).Should().Be("2,418,902");

    [Theory]
    [InlineData(412, "412 ms")]
    [InlineData(41_000, "41 s")]
    [InlineData(2_500, "2.5 s")]
    [InlineData(134_000, "2m 14s")]
    public void Duration_reads_like_the_mockup(int milliseconds, string expected) =>
        SqliteFormat.Duration(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
}
```

- [ ] **Step 4: Write the failing plugin identity tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests;

public class SqlitePluginTests
{
    [Fact]
    public void Identity_is_sqlite()
    {
        var plugin = new SqlitePlugin();

        plugin.Id.Should().Be("sqlite");
        plugin.ConnectionKind.Should().Be("sqlite");
        plugin.DisplayName.Should().Be("SQLite");
        plugin.Monogram.Should().Be("SQ");
    }

    [Fact]
    public void Nav_items_are_overview_then_tables()
    {
        new SqlitePlugin().NavItems.Select(n => n.Href).Should().Equal("/p/sqlite/overview", "/p/sqlite/tables");
    }

    [Fact]
    public void Contribution_counts_two_pages_and_the_two_audited_actions()
    {
        var contribution = new SqlitePlugin().Contribution;

        contribution.PageCount.Should().Be(2);
        contribution.ActionCount.Should().Be(2);
    }

    [Fact]
    public void Connection_summary_shows_the_file_name_and_mode_but_not_the_directory()
    {
        var secret = new SqliteConnectionSettings("/data/private/reports.db").ToSecretString();

        var summary = new SqlitePlugin().GetConnectionSummary(secret);

        summary.Should().Equal(new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" });
    }

    [Fact]
    public void Connection_summary_is_empty_for_an_unparsable_secret()
    {
        new SqlitePlugin().GetConnectionSummary("not a secret").Should().BeEmpty();
    }

    [Fact]
    public async Task Test_connection_fails_closed_before_the_plugin_is_initialized()
    {
        var result = await new SqlitePlugin().TestConnectionAsync("Path=/data/reports.db");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SQLite plugin is not initialized.");
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS — types don't exist.

- [ ] **Step 6: Implement the settings**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteConnectionSettings.cs`:

```csharp
using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Named "access" mode so it doesn't collide with Microsoft.Data.Sqlite.SqliteOpenMode.</summary>
public enum SqliteAccessMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// The saved secret, house key=value; convention (design spec §4.1):
/// "Path=/data/reports.db;Mode=ReadOnly;BusyTimeoutMs=5000;StatementTimeoutS=30". Nothing in it is
/// credential-shaped, but it is stored through the normal encrypted secret path anyway. Mode is
/// stored so phase 2 (write mode) needs no migration; phase 1 always opens read-only.
/// </summary>
public sealed record SqliteConnectionSettings(
    string Path,
    SqliteAccessMode Mode = SqliteAccessMode.ReadOnly,
    int BusyTimeoutMs = SqliteConnectionSettings.DefaultBusyTimeoutMs,
    int StatementTimeoutS = SqliteConnectionSettings.DefaultStatementTimeoutS)
{
    public const int DefaultBusyTimeoutMs = 5000;
    public const int DefaultStatementTimeoutS = 30;

    public static SqliteConnectionSettings Parse(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in secret.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                throw new FormatException($"Expected key=value, got '{part.Trim()}'.");
            }

            values[part[..equals].Trim()] = part[(equals + 1)..].Trim();
        }

        if (!values.TryGetValue("Path", out var path) || path.Length == 0)
        {
            throw new FormatException("Path is required.");
        }

        var mode = SqliteAccessMode.ReadOnly;
        if (values.TryGetValue("Mode", out var modeText)
            && (!Enum.TryParse(modeText, ignoreCase: true, out mode) || !Enum.IsDefined(mode) || int.TryParse(modeText, out _)))
        {
            throw new FormatException($"Unknown Mode '{modeText}'.");
        }

        return new SqliteConnectionSettings(
            path,
            mode,
            PositiveInt(values, "BusyTimeoutMs", DefaultBusyTimeoutMs),
            PositiveInt(values, "StatementTimeoutS", DefaultStatementTimeoutS));
    }

    public string ToSecretString()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path is required.", nameof(Path));
        }

        if (Path.Contains(';'))
        {
            throw new ArgumentException("Path can't contain ';'.", nameof(Path));
        }

        if (BusyTimeoutMs <= 0 || StatementTimeoutS <= 0)
        {
            throw new ArgumentException("Timeouts must be positive.");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Path={Path};Mode={Mode};BusyTimeoutMs={BusyTimeoutMs};StatementTimeoutS={StatementTimeoutS}");
    }

    private static int PositiveInt(Dictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException($"{key} must be a positive whole number.");
        }

        return value;
    }
}
```

- [ ] **Step 7: Implement the formatter**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteFormat.cs`:

```csharp
using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Number/size/duration copy shared by every SQLite page, matching the mockup ("2.1 GB", "48 MB", "41.2M", "2m 14s").</summary>
public static class SqliteFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binary (1024) units; one decimal below 10 (KB and up), whole numbers otherwise.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit > 0 && value < 10 ? "0.#" : "0";
        return $"{value.ToString(format, Inv)} {units[unit]}";
    }

    public static string Count(long value) => value.ToString("N0", Inv);

    /// <summary>For ≈ row estimates: "41.2M" from a million up, grouped digits below.</summary>
    public static string Approx(long rows) =>
        rows >= 1_000_000 ? $"{(rows / 1_000_000d).ToString("0.0", Inv)}M" : Count(rows);

    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return $"{(int)duration.TotalMilliseconds} ms";
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{duration.TotalSeconds.ToString("0.#", Inv)} s";
        }

        return $"{(int)duration.TotalMinutes}m {duration.Seconds.ToString("00", Inv)}s";
    }
}
```

- [ ] **Step 8: Implement the plugin shell**

Create `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

public sealed class SqlitePlugin : IPlugin
{
    public const string PluginId = "sqlite";
    public const string OverviewHref = "/p/sqlite/overview";
    public const string TablesHref = "/p/sqlite/tables";

    public string Id => PluginId;
    public string DisplayName => "SQLite";
    public string? Monogram => "SQ";
    public string Version => "0.1.0";
    public string ConnectionKind => "sqlite";
    public string ConnectionKindDisplayName => "SQLite";

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Tables", TablesHref),
    ];

    // Pages: Overview, Tables. Audited actions: integrity check, CSV export (design spec §8).
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 2);

    internal static ConnectionTestResult NotInitialized => new(false, "SQLite plugin is not initialized.");

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var settings = SqliteConnectionSettings.Parse(secret);
            return new Dictionary<string, string>
            {
                // Basename only: the directory layout of the host is not something to print on every card.
                ["File"] = System.IO.Path.GetFileName(settings.Path),
                ["Mode"] = settings.Mode == SqliteAccessMode.ReadOnly ? "read-only" : "read-write",
            };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void ConfigureServices(IServiceCollection services) =>
        services.AddMarkedServices(typeof(SqlitePlugin).Assembly);

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        Task.FromResult(NotInitialized);
}
```

- [ ] **Step 9: Register the plugin**

In `src/SbConsole.Web/Program.cs`, after the Redis registration line add:

```csharp
builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Sqlite.SqlitePlugin>();
```

- [ ] **Step 10: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS (all).

- [ ] **Step 11: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

```bash
git add SbConsole.slnx src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests src/SbConsole.Web/SbConsole.Web.csproj src/SbConsole.Web/Program.cs
git commit -m "feat(sqlite): plugin scaffold, connection settings and identity"
```

---
### Task 4: Operations core — guarded open, timeout, friendly errors, Test connection

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteExceptions.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/FriendlySqliteError.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteSql.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/PluginResult.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/HandlerRunner.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`
- Modify: `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/TempDatabase.cs`, `.../Client/SqliteOperationsConnectionTests.cs`, `.../Client/FriendlySqliteErrorTests.cs`, `.../SqlitePluginTests.cs`

**Interfaces:**
- Consumes: `IHostPathPolicy`, `PathCheckResult` (Task 1); `IPlugin.Initialize` (Task 2); `SqliteConnectionSettings`, `SqliteFormat`, `SqlitePlugin` (Task 3).
- Produces:
  - `SqlitePathRefusedException(string reason)`, `SqliteStatementTimeoutException(int seconds, Exception inner)`, `SqliteObjectNotFoundException(string name)`
  - `FriendlySqliteError.From(Exception) : string`
  - `static class SqliteSql { Execute, Scalar, Long, Text, Rows<T>, QuoteIdentifier }` — every helper takes `(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)`
  - `interface ISqliteOperations { Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default); }` (grows in Tasks 5–9)
  - `SqliteOperations(IHostPathPolicy pathPolicy)` with `internal T Run<T>(string secret, Func<SqliteConnection, T> work, CancellationToken ct)` and `internal Task<T> RunAsync<T>(...)`
  - `PluginResult` / `PluginResult<T>` (`IsSuccess`, `Error`, `Value`, `Ok`, `Fail(string)`, `Fail(Exception)`)
  - `HandlerRunner.QueryAsync<T>(IConnectionProvider, ILogger, Guid, string description, Func<string, Task<T>>, CancellationToken)`, `HandlerRunner.AuditQuietlyAsync(...)`, `HandlerRunner.ConnectionNotFound`
  - `SqlitePlugin.Initialize(IServiceProvider)`, `SqlitePlugin.InitializeForTesting(ISqliteOperations)` (internal)
  - Test helper `TempDatabase` (`Path`, `Directory`, `Execute(sql)`, `Secret(...)`, `Operations()`, static `AllowingPolicy()`)

- [ ] **Step 1: Create the temp-database test helper**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/TempDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>
/// A real SQLite file in its own temp directory, deleted on dispose. The one deliberate exception
/// to "plugin tests never touch a real engine" (design spec §9): a local file needs no Docker or
/// network, and it's the only honest way to test PRAGMA parsing, read-only mode and interrupts.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase(string ddl = "", string fileName = "test.db", bool wal = false)
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("sbc-sqlite-").FullName;
        Path = System.IO.Path.Combine(Directory, fileName);
        using var connection = OpenWriter();
        Run(connection, wal ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode=DELETE;");
        if (ddl.Length > 0)
        {
            Run(connection, ddl);
        }
    }

    public string Directory { get; }

    public string Path { get; }

    /// <summary>A read-write connection for arranging test data (the code under test never writes).</summary>
    public SqliteConnection OpenWriter()
    {
        var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Execute(string sql)
    {
        using var connection = OpenWriter();
        Run(connection, sql);
    }

    public string Secret(int statementTimeoutS = 30, int busyTimeoutMs = 5000, SqliteAccessMode mode = SqliteAccessMode.ReadOnly) =>
        new SqliteConnectionSettings(Path, mode, busyTimeoutMs, statementTimeoutS).ToSecretString();

    public SqliteOperations Operations() => new(AllowingPolicy());

    public static IHostPathPolicy AllowingPolicy()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(call => PathCheckResult.Allow(call.Arg<string>()));
        return policy;
    }

    public static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
```

- [ ] **Step 2: Write the failing operations tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsConnectionTests.cs`:

```csharp
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsConnectionTests : IDisposable
{
    private const string Forever = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x";
    private readonly TempDatabase _db = new("CREATE TABLE a(x); CREATE TABLE b(y);");

    [Fact]
    public async Task Test_reports_version_size_journal_mode_and_table_count()
    {
        var result = await _db.Operations().TestConnectionAsync(_db.Secret());

        result.Success.Should().BeTrue();
        result.Identity.Should().StartWith("SQLite 3.").And.EndWith("· DELETE");
        result.Checks!.Select(c => (c.Label, c.Status)).Should().Equal(
            ("Path allowed", ConnectionCheckStatus.Passed),
            ("File opened", ConnectionCheckStatus.Passed),
            ("Schema readable", ConnectionCheckStatus.Passed));
        result.Checks![2].Detail.Should().Be("2 tables");
    }

    [Fact]
    public async Task Test_fails_with_the_policy_reason_and_never_opens_a_refused_path()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(PathCheckResult.Refuse("outside the roots"));
        var missing = System.IO.Path.Combine(_db.Directory, "never.db");

        var result = await new SqliteOperations(policy).TestConnectionAsync(new SqliteConnectionSettings(missing).ToSecretString());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("outside the roots");
        result.Checks!.Should().ContainSingle().Which.Status.Should().Be(ConnectionCheckStatus.Failed);
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Test_fails_friendly_for_a_missing_file_and_does_not_create_it()
    {
        var missing = System.IO.Path.Combine(_db.Directory, "missing.db");

        var result = await _db.Operations().TestConnectionAsync(new SqliteConnectionSettings(missing).ToSecretString());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("File not found, or not readable by the SbConsole container user.");
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Test_fails_friendly_for_a_file_that_is_not_a_database()
    {
        var text = System.IO.Path.Combine(_db.Directory, "notes.db");
        await File.WriteAllTextAsync(text, new string('x', 4096));

        var result = await _db.Operations().TestConnectionAsync(new SqliteConnectionSettings(text).ToSecretString());

        result.ErrorMessage.Should().Be("The file is not a valid or healthy SQLite database.");
    }

    [Fact]
    public async Task Test_fails_for_unparsable_settings()
    {
        var result = await _db.Operations().TestConnectionAsync("Mode=ReadOnly");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid connection settings: Path is required.");
    }

    [Fact]
    public void Opens_read_only_even_when_the_saved_mode_is_read_write()
    {
        var operations = _db.Operations();

        var act = () => operations.Run(_db.Secret(mode: SqliteAccessMode.ReadWrite), c =>
        {
            SqliteSql.Execute(c, "CREATE TABLE z(x)");
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(8);
    }

    [Fact]
    public async Task A_statement_over_the_timeout_is_interrupted_and_reported_as_a_timeout()
    {
        var operations = _db.Operations();
        var stopwatch = Stopwatch.StartNew();

        var act = () => operations.RunAsync(_db.Secret(statementTimeoutS: 1), c => SqliteSql.Long(c, Forever), CancellationToken.None);

        (await act.Should().ThrowAsync<SqliteStatementTimeoutException>())
            .Which.Message.Should().Be("Statement exceeded the 1 s statement timeout.");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_the_statement_and_surfaces_as_cancellation()
    {
        var operations = _db.Operations();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => operations.RunAsync(_db.Secret(), c => SqliteSql.Long(c, Forever), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void A_refused_path_throws_before_anything_is_opened()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(PathCheckResult.Refuse("nope"));

        var act = () => new SqliteOperations(policy).Run(_db.Secret(), _ => 0, CancellationToken.None);

        act.Should().Throw<SqlitePathRefusedException>().WithMessage("nope");
    }

    [Fact]
    public void Quotes_identifiers_by_doubling_embedded_quotes()
    {
        SqliteSql.QuoteIdentifier("we\"ird").Should().Be("\"we\"\"ird\"");
    }

    public void Dispose() => _db.Dispose();
}
```

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/FriendlySqliteErrorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class FriendlySqliteErrorTests
{
    [Theory]
    [InlineData(5, "Database is locked — still busy after the busy timeout. Another writer holds the lock.")]
    [InlineData(6, "Database is locked — still busy after the busy timeout. Another writer holds the lock.")]
    [InlineData(8, "This connection is read-only.")]
    [InlineData(11, "The file is not a valid or healthy SQLite database.")]
    [InlineData(26, "The file is not a valid or healthy SQLite database.")]
    [InlineData(14, "File not found, or not readable by the SbConsole container user.")]
    public void Maps_sqlite_error_codes(int code, string expected)
    {
        FriendlySqliteError.From(new SqliteException("raw engine noise", code)).Should().Be(expected);
    }

    [Fact]
    public void Passes_through_the_plugins_own_exceptions()
    {
        FriendlySqliteError.From(new SqlitePathRefusedException("outside the roots")).Should().Be("outside the roots");
        FriendlySqliteError.From(new SqliteObjectNotFoundException("events"))
            .Should().Be("No table or view named 'events' — it may have been dropped since the page loaded.");
    }

    [Fact]
    public void Prefixes_settings_errors()
    {
        FriendlySqliteError.From(new FormatException("Path is required.")).Should().Be("Invalid connection settings: Path is required.");
    }

    [Fact]
    public void Falls_back_to_the_sdk_helper_for_anything_else()
    {
        FriendlySqliteError.From(new InvalidOperationException("boom")).Should().Be("boom");
    }
}
```

Append to `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginTests.cs` (add `using NSubstitute;` and `using SbConsole.Sdk;`):

```csharp
    [Fact]
    public async Task Test_connection_delegates_to_the_operations_once_initialized()
    {
        var operations = Substitute.For<ISqliteOperations>();
        var expected = new ConnectionTestResult(true, Identity: "SQLite 3.53.3 · 4 KB · DELETE");
        operations.TestConnectionAsync("Path=/x.db", Arg.Any<CancellationToken>()).Returns(expected);
        var plugin = new SqlitePlugin();
        plugin.InitializeForTesting(operations);

        (await plugin.TestConnectionAsync("Path=/x.db")).Should().BeSameAs(expected);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS — `SqliteOperations`, `SqliteSql`, exceptions, `FriendlySqliteError`, `InitializeForTesting` don't exist.

- [ ] **Step 4: Implement exceptions, error mapping, SQL helpers**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteExceptions.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>IHostPathPolicy refused the connection's path; the message is the policy's Reason.</summary>
public sealed class SqlitePathRefusedException(string reason) : Exception(reason);

/// <summary>The statement ran past the connection's StatementTimeoutS and was interrupted.</summary>
public sealed class SqliteStatementTimeoutException(int seconds, Exception inner)
    : Exception($"Statement exceeded the {seconds} s statement timeout.", inner);

/// <summary>A table/view name that isn't (or is no longer) in sqlite_schema.</summary>
public sealed class SqliteObjectNotFoundException(string name)
    : Exception($"No table or view named '{name}' — it may have been dropped since the page loaded.");
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/FriendlySqliteError.cs`:

```csharp
using Microsoft.Data.Sqlite;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Maps SQLite failures to the mockup's 1g wording (design spec §7). Raw engine text never reaches the UI.</summary>
public static class FriendlySqliteError
{
    private const string Locked = "Database is locked — still busy after the busy timeout. Another writer holds the lock.";

    public static string From(Exception ex) => ex switch
    {
        SqlitePathRefusedException or SqliteStatementTimeoutException or SqliteObjectNotFoundException => ex.Message,
        SqliteException { SqliteErrorCode: 5 or 6 } => Locked,                          // SQLITE_BUSY / SQLITE_LOCKED
        SqliteException { SqliteErrorCode: 8 } => "This connection is read-only.",      // SQLITE_READONLY
        SqliteException { SqliteErrorCode: 11 or 26 } =>                               // SQLITE_CORRUPT / SQLITE_NOTADB
            "The file is not a valid or healthy SQLite database.",
        SqliteException { SqliteErrorCode: 14 } =>                                     // SQLITE_CANTOPEN
            "File not found, or not readable by the SbConsole container user.",
        FormatException format => $"Invalid connection settings: {FriendlyError.Truncate(format.Message)}",
        _ => FriendlyError.From(ex),
    };
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteSql.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Small synchronous command helpers. Values always travel as parameters; identifiers only through QuoteIdentifier.</summary>
internal static class SqliteSql
{
    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    public static long Long(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Scalar(connection, sql, parameters) is { } value and not DBNull ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;

    public static string Text(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToString(Scalar(connection, sql, parameters), CultureInfo.InvariantCulture) ?? "";

    public static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>SQLite identifier quoting: wrap in double quotes, double any embedded double quote.</summary>
    public static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static SqliteCommand Create(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
```

- [ ] **Step 5: Implement the operations core**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`:

```csharp
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// The thin wrapper every SQLite page and handler goes through (CLAUDE.md "every plugin operation
/// goes through a thin wrapper interface"), so handler/page tests substitute it directly.
/// </summary>
public interface ISqliteOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;
using SbConsole.Sdk;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Microsoft.Data.Sqlite implementation. Every call: parse the secret, ask IHostPathPolicy, open the
/// *resolved* path read-only with pooling off, set busy_timeout, run the work under the statement
/// timeout, close. Design spec §4.2.
/// </summary>
public sealed partial class SqliteOperations(IHostPathPolicy pathPolicy) : ISqliteOperations, ISingletonService
{
    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        SqliteConnectionSettings settings;
        try
        {
            settings = SqliteConnectionSettings.Parse(secret);
        }
        catch (FormatException ex)
        {
            var message = FriendlySqliteError.From(ex);
            return new ConnectionTestResult(false, message, Checks: [new ConnectionCheck("Settings valid", ConnectionCheckStatus.Failed, message)]);
        }

        var path = pathPolicy.Check(settings.Path);
        if (!path.Allowed)
        {
            var reason = path.Reason ?? "Path refused.";
            return new ConnectionTestResult(false, reason, Checks: [new ConnectionCheck("Path allowed", ConnectionCheckStatus.Failed, reason)]);
        }

        List<ConnectionCheck> checks = [new ConnectionCheck("Path allowed", ConnectionCheckStatus.Passed, path.ResolvedPath)];
        try
        {
            var (version, journal, tables, bytes) = await RunAsync(secret, c => (
                SqliteSql.Text(c, "SELECT sqlite_version()"),
                SqliteSql.Text(c, "PRAGMA journal_mode").ToUpperInvariant(),
                SqliteSql.Long(c, @"SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'"),
                new FileInfo(c.DataSource).Length), ct);

            var identity = $"SQLite {version} · {SqliteFormat.Bytes(bytes)} · {journal}";
            checks.Add(new ConnectionCheck("File opened", ConnectionCheckStatus.Passed, identity));
            checks.Add(new ConnectionCheck("Schema readable", ConnectionCheckStatus.Passed, $"{tables.ToString(CultureInfo.InvariantCulture)} tables"));
            return new ConnectionTestResult(true, null, identity, checks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = FriendlySqliteError.From(ex);
            checks.Add(new ConnectionCheck("File opened", ConnectionCheckStatus.Failed, message));
            return new ConnectionTestResult(false, message, null, checks);
        }
    }

    /// <summary>Runs <paramref name="work"/> off the Blazor circuit thread (Microsoft.Data.Sqlite's async API is synchronous underneath).</summary>
    internal Task<T> RunAsync<T>(string secret, Func<SqliteConnection, T> work, CancellationToken ct) =>
        Task.Run(() => Run(secret, work, ct), ct);

    internal T Run<T>(string secret, Func<SqliteConnection, T> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = SqliteConnectionSettings.Parse(secret);
        var path = pathPolicy.Check(settings.Path);
        if (!path.Allowed)
        {
            throw new SqlitePathRefusedException(path.Reason ?? "Path refused.");
        }

        using var connection = Open(path.ResolvedPath!, settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.StatementTimeoutS));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        // Verified 2026-10-13 (Microsoft.Data.Sqlite 10.0.12): a token passed to the provider, or
        // SqliteCommand.Cancel(), does NOT stop a running statement. sqlite3_interrupt does, with
        // SQLITE_INTERRUPT. Disposing the registration waits for an in-flight callback, so the
        // handle is never interrupted after the connection is gone.
        using (linked.Token.Register(() => raw.sqlite3_interrupt(connection.Handle)))
        {
            try
            {
                return work(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                ct.ThrowIfCancellationRequested();
                throw new SqliteStatementTimeoutException(settings.StatementTimeoutS, ex);
            }
        }
    }

    private static SqliteConnection Open(string resolvedPath, SqliteConnectionSettings settings)
    {
        // The resolved path is always absolute ("/..."), so SQLite can never read it as a "file:"
        // URI carrying its own ?mode= override.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = resolvedPath,
            Mode = SqliteOpenMode.ReadOnly, // phase 1: always read-only, whatever settings.Mode says (spec §2)
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(settings.BusyTimeoutMs / 1000.0)),
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            SqliteSql.Execute(connection, $"PRAGMA busy_timeout = {settings.BusyTimeoutMs.ToString(CultureInfo.InvariantCulture)}");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
```

(`partial` lets later tasks add `SqliteOperations.Overview.cs`, `.Schema.cs`, etc., keeping each file focused.)

- [ ] **Step 6: Implement result plumbing**

Create `src/Plugins/SbConsole.Plugins.Sqlite/PluginResult.cs`:

```csharp
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite;

public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public string? Error { get; }

    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlySqliteError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, string? error, T? value)
    {
        IsSuccess = isSuccess;
        Error = error;
        Value = value;
    }

    public bool IsSuccess { get; }
    public string? Error { get; }
    public T? Value { get; }

    public static PluginResult<T> Ok(T value) => new(true, null, value);
    public static PluginResult<T> Fail(string error) => new(false, error, default);
    public static PluginResult<T> Fail(Exception ex) => new(false, FriendlySqliteError.From(ex), default);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/HandlerRunner.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

internal static class HandlerRunner
{
    public const string ConnectionNotFound = "Connection not found.";

    /// <summary>
    /// Shared body of every read-only query: resolve the secret, run, reduce any failure through
    /// FriendlySqliteError (Redis/RabbitMQ HandlerRunner pattern). A cancellation the caller asked
    /// for is rethrown so pages can tell "you cancelled" from "it failed".
    /// </summary>
    public static async Task<PluginResult<T>> QueryAsync<T>(
        IConnectionProvider connections, ILogger logger, Guid connectionId, string description,
        Func<string, Task<T>> run, CancellationToken ct)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<T>.Fail(ConnectionNotFound);
            }

            return PluginResult<T>.Ok(await run(secret));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "SQLite query failed: {Description}", description);
            return PluginResult<T>.Fail(ex);
        }
    }

    /// <summary>Writes one audit row with CancellationToken.None and only logs if the write itself fails.</summary>
    public static async Task AuditQuietlyAsync(
        IAuditScope audit, ILogger logger, string action, string target, ActionRisk risk, bool succeeded, string? detail)
    {
        try
        {
            await audit.RecordAsync(action, target, risk, succeeded, detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing the {Action} audit row for {Target} (succeeded: {Succeeded}) failed.", action, target, succeeded);
        }
    }
}
```

- [ ] **Step 7: Wire the plugin to its operations**

In `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`, add a field below the constants:

```csharp
    // Resolved in Initialize (IPlugin.Initialize): the host constructs plugins with new(), so the
    // instance hooks below have no DI of their own. Until then they fail closed.
    private ISqliteOperations? _operations;
```

Replace `TestConnectionAsync` with:

```csharp
    public void Initialize(IServiceProvider services) =>
        _operations = services.GetRequiredService<ISqliteOperations>();

    internal void InitializeForTesting(ISqliteOperations operations) => _operations = operations;

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        _operations?.TestConnectionAsync(secret, ct) ?? Task.FromResult(NotInitialized);
```

- [ ] **Step 8: Prove the real host initializes it**

In `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`, add this test to the class (it already sets the other `SBC_*` vars in its constructor):

```csharp
    [Fact]
    public async Task Sqlite_plugin_is_initialized_by_the_real_program_and_fails_closed_without_roots()
    {
        Environment.SetEnvironmentVariable("SBC_SQLITE_ROOTS", null);
        using var factory = new WebApplicationFactory<Program>();

        var plugin = factory.Services.GetRequiredService<PluginRegistry>().Find("sqlite");
        var result = await plugin!.TestConnectionAsync("Path=/nowhere/reports.db");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("SBC_SQLITE_ROOTS");
    }
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests && dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ProgramDiRegistrationTests"`
Expected: PASS. The timeout test takes ~1 s; the cancellation test ~200 ms.

- [ ] **Step 10: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
git commit -m "feat(sqlite): guarded read-only open, statement timeout via sqlite3_interrupt, connection test"
```

---

### Task 5: Overview data — PRAGMA overview and table sizes

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOverview.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SchemaModels.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Overview.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetOverviewQueryHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetTableSizesQueryHandler.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsOverviewTests.cs`, `tests/SbConsole.Plugins.Sqlite.Tests/Overview/OverviewQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `SqliteOperations.RunAsync`, `SqliteSql`, `HandlerRunner.QueryAsync`, `PluginResult<T>` (Task 4).
- Produces:
  - `record SqliteOverview(string Path, string SqliteVersion, long FileBytes, long? WalBytes, DateTimeOffset LastModified, long PageSize, long PageCount, long FreelistCount, string JournalMode, string Synchronous, long WalAutoCheckpoint, long CacheSize, int BusyTimeoutMs)` with computed `FreeBytes`, `FreePercent`, `CacheBytes`, `CachePages`
  - `enum SchemaObjectType { Table, View, Index, Trigger }`
  - `record TableSizeEntry(string Name, SchemaObjectType Type, long? Bytes, long? EstimatedRows)`
  - `record TableSizes(bool SizesAvailable, int TableCount, int IndexCount, IReadOnlyList<TableSizeEntry> Entries)`
  - `internal sealed record SchemaRow(SchemaObjectType Type, string Name, string TableName, string? Sql)`
  - `ISqliteOperations.GetOverviewAsync(string secret, CancellationToken ct = default) : Task<SqliteOverview>`
  - `ISqliteOperations.GetTableSizesAsync(string secret, CancellationToken ct = default) : Task<TableSizes>`
  - `SqliteOperations` internal statics: `ReadSchemaObjects(c)`, `ReadStat1Rows(c)`, `TryReadDbstatSizes(c)`, `Lookup(dict, key)`
  - `GetOverviewQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) : Task<PluginResult<SqliteOverview>>`
  - `GetTableSizesQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) : Task<PluginResult<TableSizes>>`

- [ ] **Step 1: Write the failing operations tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsOverviewTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteOperationsOverviewTests
{
    [Fact]
    public async Task Overview_reads_file_and_pragma_values()
    {
        using var db = new TempDatabase("""
            CREATE TABLE t(id INTEGER PRIMARY KEY, payload BLOB);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
              INSERT INTO t SELECT i, randomblob(500) FROM n;
            DELETE FROM t;
            """);

        var overview = await db.Operations().GetOverviewAsync(db.Secret(busyTimeoutMs: 1234));

        overview.Path.Should().EndWith("test.db");
        overview.SqliteVersion.Should().StartWith("3.");
        overview.FileBytes.Should().Be(new FileInfo(db.Path).Length);
        overview.WalBytes.Should().BeNull();
        overview.PageSize.Should().Be(4096);
        overview.PageCount.Should().BeGreaterThan(0);
        overview.FreelistCount.Should().BeGreaterThan(0, "the DELETE leaves free pages behind");
        overview.FreePercent.Should().BeInRange(1, 100);
        overview.FreeBytes.Should().Be(overview.FreelistCount * overview.PageSize);
        overview.JournalMode.Should().Be("DELETE");
        overview.Synchronous.Should().BeOneOf("OFF", "NORMAL", "FULL", "EXTRA");
        overview.CacheBytes.Should().BeGreaterThan(0);
        overview.BusyTimeoutMs.Should().Be(1234);
    }

    [Fact]
    public async Task Overview_reports_wal_mode_and_the_wal_file_size()
    {
        using var db = new TempDatabase("CREATE TABLE t(x);", wal: true);
        using var writer = db.OpenWriter();
        TempDatabase.Run(writer, "PRAGMA wal_autocheckpoint = 0; INSERT INTO t VALUES (randomblob(10000));");

        var overview = await db.Operations().GetOverviewAsync(db.Secret());

        overview.JournalMode.Should().Be("WAL");
        overview.WalBytes.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(-2000L, 4096L, 2_048_000L, 500L)]
    [InlineData(100L, 4096L, 409_600L, 100L)]
    public void Cache_size_is_pages_when_positive_and_kib_when_negative(long cacheSize, long pageSize, long bytes, long pages)
    {
        var overview = new SqliteOverview("/x.db", "3", 0, null, DateTimeOffset.UnixEpoch, pageSize, 0, 0, "DELETE", "FULL", 1000, cacheSize, 5000);

        overview.CacheBytes.Should().Be(bytes);
        overview.CachePages.Should().Be(pages);
        overview.FreePercent.Should().Be(0);
    }

    [Theory]
    [InlineData(0L, "OFF")]
    [InlineData(1L, "NORMAL")]
    [InlineData(2L, "FULL")]
    [InlineData(3L, "EXTRA")]
    public void Synchronous_levels_get_their_pragma_names(long level, string name) =>
        SqliteOperations.SynchronousName(level).Should().Be(name);

    [Fact]
    public async Task The_bundled_sqlite_has_no_dbstat_so_tables_are_ranked_by_estimated_rows()
    {
        // Verified 2026-10-13: e_sqlite3 (SQLite 3.53.3) is built without SQLITE_ENABLE_DBSTAT_VTAB.
        // If this starts failing because sizes ARE available, the Overview's "Size by table" panel
        // lights up on its own -- update this test to assert byte ordering instead.
        using var db = new TempDatabase("""
            CREATE TABLE small(x);
            CREATE TABLE big(x);
            CREATE TABLE never_analyzed_until_now(x);
            CREATE INDEX ix_big ON big(x);
            INSERT INTO small VALUES (1), (2), (3);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 10) INSERT INTO big SELECT i FROM n;
            ANALYZE;
            """);

        var sizes = await db.Operations().GetTableSizesAsync(db.Secret());

        sizes.SizesAvailable.Should().BeFalse();
        sizes.TableCount.Should().Be(3);
        sizes.IndexCount.Should().Be(1);
        sizes.Entries.Select(e => (e.Name, e.EstimatedRows)).Should().Equal(
            ("big", 10L), ("small", 3L), ("never_analyzed_until_now", (long?)null));
        sizes.Entries.Should().OnlyContain(e => e.Type == SchemaObjectType.Table && e.Bytes == null);
    }

    [Fact]
    public async Task Table_sizes_never_list_sqlite_internal_objects()
    {
        using var db = new TempDatabase("CREATE TABLE u(x UNIQUE); INSERT INTO u VALUES (1); ANALYZE;");

        var sizes = await db.Operations().GetTableSizesAsync(db.Secret());

        sizes.Entries.Select(e => e.Name).Should().Equal("u");
        sizes.IndexCount.Should().Be(0, "sqlite_autoindex_* is internal");
    }
}
```

Note: when the table has no rows ANALYZE writes no `sqlite_stat1` row, so `never_analyzed_until_now` has a `null` estimate.

- [ ] **Step 2: Write the failing handler tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Overview/OverviewQueryHandlerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Overview;

public class OverviewQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private static readonly SqliteOverview Sample = new(
        "/data/reports.db", "3.53.3", 2_254_857_830, 50_331_648, DateTimeOffset.UnixEpoch,
        4096, 550_502, 46_387, "WAL", "NORMAL", 1000, -2000, 5000);

    [Fact]
    public async Task Overview_returns_the_operations_result_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample);

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Sample);
    }

    [Fact]
    public async Task Overview_fails_without_touching_the_file_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceive().GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overview_reduces_failures_to_friendly_text()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SqliteOverview>>(_ => throw new Microsoft.Data.Sqlite.SqliteException("raw", 26));

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Error.Should().Be("The file is not a valid or healthy SQLite database.");
    }

    [Fact]
    public async Task Table_sizes_return_the_operations_result()
    {
        var sizes = new TableSizes(false, 1, 0, [new TableSizeEntry("events", SchemaObjectType.Table, null, 41_200_000)]);
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(sizes);

        var result = await new GetTableSizesQueryHandler(_operations, _connections, NullLogger<GetTableSizesQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Value.Should().Be(sizes);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS — new types/methods don't exist.

- [ ] **Step 4: Add the models**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOverview.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Mockup 1a's tiles. Synchronous, WalAutoCheckpoint, CacheSize and BusyTimeoutMs are
/// per-connection settings -- they describe a fresh connection with this console's settings, not
/// something stored in the file -- and the page labels them that way.
/// </summary>
public sealed record SqliteOverview(
    string Path,
    string SqliteVersion,
    long FileBytes,
    long? WalBytes,
    DateTimeOffset LastModified,
    long PageSize,
    long PageCount,
    long FreelistCount,
    string JournalMode,
    string Synchronous,
    long WalAutoCheckpoint,
    long CacheSize,
    int BusyTimeoutMs)
{
    public long FreeBytes => FreelistCount * PageSize;

    public double FreePercent => PageCount == 0 ? 0 : 100.0 * FreelistCount / PageCount;

    /// <summary>PRAGMA cache_size: positive = pages, negative = KiB.</summary>
    public long CacheBytes => CacheSize >= 0 ? CacheSize * PageSize : -CacheSize * 1024;

    public long CachePages => CacheSize >= 0 ? CacheSize : PageSize == 0 ? 0 : CacheBytes / PageSize;
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SchemaModels.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

public enum SchemaObjectType
{
    Table,
    View,
    Index,
    Trigger,
}

/// <summary>One sqlite_schema row (internal sqlite_* objects excluded).</summary>
internal sealed record SchemaRow(SchemaObjectType Type, string Name, string TableName, string? Sql);

/// <summary>Bytes is null when dbstat isn't available; EstimatedRows is null without a sqlite_stat1 row.</summary>
public sealed record TableSizeEntry(string Name, SchemaObjectType Type, long? Bytes, long? EstimatedRows);

/// <summary>
/// Overview's "Size by table" panel. With dbstat: tables and indexes by bytes. Without it (the
/// bundled build, spec §5): tables only, by sqlite_stat1 rows ≈, unanalyzed tables last.
/// </summary>
public sealed record TableSizes(bool SizesAvailable, int TableCount, int IndexCount, IReadOnlyList<TableSizeEntry> Entries);
```

- [ ] **Step 5: Extend the operations interface**

Add to `ISqliteOperations`:

```csharp
    Task<SqliteOverview> GetOverviewAsync(string secret, CancellationToken ct = default);

    Task<TableSizes> GetTableSizesAsync(string secret, CancellationToken ct = default);
```

- [ ] **Step 6: Implement the overview operations**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Overview.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<SqliteOverview> GetOverviewAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var path = c.DataSource;
            var file = new FileInfo(path);
            var wal = new FileInfo(path + "-wal");
            return new SqliteOverview(
                path,
                SqliteSql.Text(c, "SELECT sqlite_version()"),
                file.Length,
                wal.Exists ? wal.Length : null,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                SqliteSql.Long(c, "PRAGMA page_size"),
                SqliteSql.Long(c, "PRAGMA page_count"),
                SqliteSql.Long(c, "PRAGMA freelist_count"),
                SqliteSql.Text(c, "PRAGMA journal_mode").ToUpperInvariant(),
                SynchronousName(SqliteSql.Long(c, "PRAGMA synchronous")),
                SqliteSql.Long(c, "PRAGMA wal_autocheckpoint"),
                SqliteSql.Long(c, "PRAGMA cache_size"),
                (int)SqliteSql.Long(c, "PRAGMA busy_timeout"));
        }, ct);

    public Task<TableSizes> GetTableSizesAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var objects = ReadSchemaObjects(c);
            var rows = ReadStat1Rows(c);
            var sizes = TryReadDbstatSizes(c);

            var entries = objects
                .Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.Index)
                .Select(o => new TableSizeEntry(
                    o.Name,
                    o.Type,
                    sizes is null ? null : Lookup(sizes, o.Name),
                    o.Type == SchemaObjectType.Table ? Lookup(rows, o.Name) : null))
                .ToList();

            IReadOnlyList<TableSizeEntry> ordered = sizes is not null
                ? entries.OrderByDescending(e => e.Bytes ?? 0).ThenBy(e => e.Name, StringComparer.Ordinal).ToList()
                : entries.Where(e => e.Type == SchemaObjectType.Table)
                    .OrderByDescending(e => e.EstimatedRows.HasValue)
                    .ThenByDescending(e => e.EstimatedRows ?? 0)
                    .ThenBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();

            return new TableSizes(
                sizes is not null,
                objects.Count(o => o.Type == SchemaObjectType.Table),
                objects.Count(o => o.Type == SchemaObjectType.Index),
                ordered);
        }, ct);

    internal static string SynchronousName(long level) => level switch
    {
        0 => "OFF",
        1 => "NORMAL",
        2 => "FULL",
        3 => "EXTRA",
        _ => level.ToString(CultureInfo.InvariantCulture),
    };

    internal static List<SchemaRow> ReadSchemaObjects(SqliteConnection c) =>
        SqliteSql.Rows(c,
            @"SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY name",
            ReadSchemaRow);

    internal static SchemaRow ReadSchemaRow(SqliteDataReader r) =>
        new(ParseType(r.GetString(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));

    private static SchemaObjectType ParseType(string type) => type switch
    {
        "table" => SchemaObjectType.Table,
        "view" => SchemaObjectType.View,
        "index" => SchemaObjectType.Index,
        "trigger" => SchemaObjectType.Trigger,
        _ => throw new InvalidOperationException($"Unknown sqlite_schema type '{type}'."),
    };

    /// <summary>
    /// Row estimates per table from sqlite_stat1: the first integer of `stat` is the table's row
    /// count (on every index row and on the idx-NULL row of an index-less table). Missing table =
    /// never analyzed = no estimate.
    /// </summary>
    internal static Dictionary<string, long> ReadStat1Rows(SqliteConnection c)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (SqliteSql.Long(c, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'sqlite_stat1'") == 0)
        {
            return result;
        }

        foreach (var (table, stat) in SqliteSql.Rows(c, "SELECT tbl, stat FROM sqlite_stat1",
                     r => (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1))))
        {
            var first = stat.Split(' ', 2)[0];
            if (long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var rows))
            {
                result[table] = Math.Max(result.GetValueOrDefault(table), rows);
            }
        }

        return result;
    }

    /// <summary>Bytes per table/index from the dbstat virtual table, or null when this SQLite build lacks it.</summary>
    internal static Dictionary<string, long>? TryReadDbstatSizes(SqliteConnection c)
    {
        try
        {
            return SqliteSql.Rows(c, "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name", r => (Name: r.GetString(0), Bytes: r.GetInt64(1)))
                .ToDictionary(x => x.Name, x => x.Bytes, StringComparer.Ordinal);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table: dbstat", StringComparison.Ordinal))
        {
            return null;
        }
    }

    internal static long? Lookup(Dictionary<string, long> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;
}
```

- [ ] **Step 7: Add the handlers**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetOverviewQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetOverviewQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetOverviewQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<SqliteOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.overview.get",
            secret => operations.GetOverviewAsync(secret, ct), ct);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetTableSizesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetTableSizesQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetTableSizesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<TableSizes>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.overview.sizes",
            secret => operations.GetTableSizesAsync(secret, ct), ct);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS.

- [ ] **Step 9: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): overview PRAGMAs and table sizes with the no-dbstat fallback"
```

---

### Task 6: Integrity check (command, stored result, last-result query)

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/IntegrityCheckOutcome.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Integrity.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/IntegrityResult.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/IntegrityResultStore.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/RunIntegrityCheckCommandHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetLastIntegrityResultQueryHandler.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/InMemoryPluginStore.cs`, `.../Client/SqliteOperationsIntegrityTests.cs`, `.../Overview/IntegrityTests.cs`

**Interfaces:**
- Consumes: Task 4 plumbing; `SqlitePlugin.PluginId`.
- Produces:
  - `record IntegrityCheckOutcome(IReadOnlyList<string> Messages, int ForeignKeyViolations)` with `const int MaxMessages = 20`, `bool IntegrityOk`
  - `ISqliteOperations.RunIntegrityCheckAsync(string secret, CancellationToken ct = default) : Task<IntegrityCheckOutcome>`
  - `record IntegrityResult(bool IntegrityOk, IReadOnlyList<string> Messages, int ForeignKeyViolations, DateTimeOffset RanAt, TimeSpan Duration)`
  - `static class IntegrityResultStore { Key(Guid), ReadAsync(IPluginStore, Guid, CancellationToken), WriteAsync(IPluginStore, Guid, IntegrityResult, CancellationToken) }`
  - `record RunIntegrityCheckCommand(Guid ConnectionId, string ConnectionName)`; `RunIntegrityCheckCommandHandler.HandleAsync(cmd, ct) : Task<PluginResult<IntegrityResult>>`, `const string Action = "sqlite.integrity.check"`
  - `GetLastIntegrityResultQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) : Task<IntegrityResult?>`
  - Test helper `InMemoryPluginStore : IPluginStore` (public `Values` dictionary)

- [ ] **Step 1: Create the in-memory store test helper**

Create `tests/SbConsole.Plugins.Sqlite.Tests/InMemoryPluginStore.cs`:

```csharp
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests;

public sealed class InMemoryPluginStore : IPluginStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.Remove(key));
}
```

- [ ] **Step 2: Write the failing operations tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsIntegrityTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteOperationsIntegrityTests
{
    [Fact]
    public async Task A_healthy_file_reports_ok_and_no_foreign_key_violations()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1);");

        var outcome = await db.Operations().RunIntegrityCheckAsync(db.Secret());

        outcome.Messages.Should().Equal("ok");
        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(0);
    }

    [Fact]
    public async Task Orphaned_child_rows_are_counted_as_foreign_key_violations()
    {
        // foreign_keys is per-connection and off by default, which is exactly how orphans get in.
        using var db = new TempDatabase("""
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
            INSERT INTO child VALUES (1, 99), (2, 98);
            """);

        var outcome = await db.Operations().RunIntegrityCheckAsync(db.Secret());

        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(2);
    }

    [Fact]
    public void Integrity_is_ok_only_for_the_single_ok_row()
    {
        new IntegrityCheckOutcome(["ok"], 0).IntegrityOk.Should().BeTrue();
        new IntegrityCheckOutcome(["row 2041 missing from index idx_events_ts"], 0).IntegrityOk.Should().BeFalse();
        new IntegrityCheckOutcome([], 0).IntegrityOk.Should().BeFalse();
    }
}
```

- [ ] **Step 3: Write the failing handler/store tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Overview/IntegrityTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Overview;

public class IntegrityTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero));

    private RunIntegrityCheckCommandHandler Handler() =>
        new(_operations, _connections, _audit, _store, _time, NullLogger<RunIntegrityCheckCommandHandler>.Instance);

    private RunIntegrityCheckCommand Command => new(_connectionId, "sqlite-reports");

    [Fact]
    public async Task A_check_is_timed_stored_and_audited_as_a_safe_action()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(41));
            return new IntegrityCheckOutcome(["ok"], 0);
        });

        var result = await Handler().HandleAsync(Command);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));
        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeEquivalentTo(result.Value);
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, true,
            "ok · 0 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_check_is_still_a_successful_command_that_reports_the_problems()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(new IntegrityCheckOutcome(["Page 48112: btreeInitPage() returns error code 11", "row 2041 missing from index idx_events_ts"], 0));

        var result = await Handler().HandleAsync(Command);

        result.Value!.IntegrityOk.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, true,
            "2 problems · 0 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_error_running_the_check_is_audited_as_failed_and_nothing_is_stored()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<IntegrityCheckOutcome>>(_ => throw new SqliteStatementTimeoutException(30, new TimeoutException()));

        var result = await Handler().HandleAsync(Command);

        result.Error.Should().Be("Statement exceeded the 30 s statement timeout.");
        _store.Values.Should().BeEmpty();
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, false,
            "Statement exceeded the 30 s statement timeout.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_an_audit_row()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(Command);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task The_last_result_query_reads_what_the_command_stored()
    {
        var stored = new IntegrityResult(true, ["ok"], 0, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(3));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, stored);

        var result = await new GetLastIntegrityResultQueryHandler(_store).HandleAsync(_connectionId);

        result.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task A_corrupt_stored_value_reads_as_never_run()
    {
        _store.Values[IntegrityResultStore.Key(_connectionId)] = "{not json";

        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeNull();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS.

- [ ] **Step 5: Implement the operation**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/IntegrityCheckOutcome.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Raw PRAGMA integrity_check rows (capped at MaxMessages) plus the PRAGMA foreign_key_check row count.</summary>
public sealed record IntegrityCheckOutcome(IReadOnlyList<string> Messages, int ForeignKeyViolations)
{
    public const int MaxMessages = 20;

    public bool IntegrityOk => Messages is ["ok"];
}
```

Add to `ISqliteOperations`:

```csharp
    Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default);
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Integrity.cs`:

```csharp
using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c => new IntegrityCheckOutcome(
            SqliteSql.Rows(c, $"PRAGMA integrity_check({IntegrityCheckOutcome.MaxMessages.ToString(CultureInfo.InvariantCulture)})", r => r.GetString(0)),
            SqliteSql.Rows(c, "PRAGMA foreign_key_check", _ => 0).Count), ct);
}
```

- [ ] **Step 6: Implement result, store, and handlers**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/IntegrityResult.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Overview;

/// <summary>The last integrity check for one connection, as stored in the plugin store and shown on the Overview strip.</summary>
public sealed record IntegrityResult(
    bool IntegrityOk,
    IReadOnlyList<string> Messages,
    int ForeignKeyViolations,
    DateTimeOffset RanAt,
    TimeSpan Duration);
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/IntegrityResultStore.cs`:

```csharp
using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

/// <summary>
/// JSON in the plugin store under "integrity:{connectionId}". Static over IPluginStore (not a DI
/// service) because SqlitePlugin.GetDashboardProblemsAsync is handed the store as a parameter.
/// </summary>
public static class IntegrityResultStore
{
    public static string Key(Guid connectionId) => $"integrity:{connectionId}";

    public static async Task<IntegrityResult?> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IntegrityResult>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task WriteAsync(IPluginStore store, Guid connectionId, IntegrityResult result, CancellationToken ct = default) =>
        store.SetAsync(Key(connectionId), JsonSerializer.Serialize(result), ct);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/RunIntegrityCheckCommandHandler.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed record RunIntegrityCheckCommand(Guid ConnectionId, string ConnectionName);

/// <summary>
/// PRAGMA integrity_check + foreign_key_check, on demand only (they walk the whole file). Safe but
/// audited -- it can hold a read transaction for a long time on a big file (design spec §5).
/// </summary>
public sealed class RunIntegrityCheckCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<RunIntegrityCheckCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.integrity.check";

    public async Task<PluginResult<IntegrityResult>> HandleAsync(RunIntegrityCheckCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IntegrityResult>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var ranAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        IntegrityCheckOutcome outcome;
        try
        {
            outcome = await operations.RunIntegrityCheckAsync(secret, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<IntegrityResult>.Fail(friendly);
        }

        var result = new IntegrityResult(outcome.IntegrityOk, outcome.Messages, outcome.ForeignKeyViolations, ranAt, clock.GetElapsedTime(started));
        try
        {
            await IntegrityResultStore.WriteAsync(store, cmd.ConnectionId, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The check itself happened; losing the cached copy only means "never run" next visit.
            logger.LogError(ex, "Storing the integrity result for {Target} failed.", cmd.ConnectionName);
        }

        var detail = result.IntegrityOk
            ? $"ok · {result.ForeignKeyViolations} FK violations"
            : $"{result.Messages.Count} problems · {result.ForeignKeyViolations} FK violations";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true, detail);
        return PluginResult<IntegrityResult>.Ok(result);
    }
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Overview/GetLastIntegrityResultQueryHandler.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

public sealed class GetLastIntegrityResultQueryHandler(
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store) : IScopedService
{
    public Task<IntegrityResult?> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        IntegrityResultStore.ReadAsync(store, connectionId, ct);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS.

- [ ] **Step 8: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green — `ProgramDiRegistrationTests.Every_handler_in_core_and_every_plugin_resolves...` now also proves the keyed-store constructor injection resolves.

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): on-demand integrity check, stored per connection and audited"
```

---
### Task 7: Schema listing and table detail

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SchemaModels.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Schema.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ListSchemaQueryHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/GetTableDetailQueryHandler.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsSchemaTests.cs`, `tests/SbConsole.Plugins.Sqlite.Tests/Tables/SchemaQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `ReadSchemaObjects`, `ReadSchemaRow`, `ReadStat1Rows`, `TryReadDbstatSizes`, `Lookup`, `SchemaRow`, `SchemaObjectType` (Task 5).
- Produces:
  - `record SchemaObject(string Name, SchemaObjectType Type, string TableName, long? EstimatedRows, long? Bytes)`
  - `record SchemaListing(IReadOnlyList<SchemaObject> Objects, bool SizesAvailable)` with `Tables`, `Views`, `Count(SchemaObjectType)`
  - `record ColumnInfo(string Name, string DeclaredType, bool NotNull, string? DefaultValue, int PrimaryKeyOrdinal, bool Hidden, bool Generated)`
  - `record IndexInfo(string Name, bool Unique, string Origin, bool Partial, IReadOnlyList<string> Columns)`
  - `record ForeignKeyInfo(int Id, string FromColumn, string ToTable, string? ToColumn, string OnUpdate, string OnDelete)`
  - `record TableDetail(string Name, SchemaObjectType Type, string? Ddl, bool WithoutRowId, long? EstimatedRows, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ForeignKeyInfo> ForeignKeys)` with `HasRowId`
  - `ISqliteOperations.ListSchemaAsync(string secret, CancellationToken ct = default) : Task<SchemaListing>`
  - `ISqliteOperations.GetTableDetailAsync(string secret, string name, CancellationToken ct = default) : Task<TableDetail>`
  - internal statics `SqliteOperations.RequireObject(c, name) : SchemaRow`, `SqliteOperations.ReadDetail(c, name) : TableDetail`
  - `ListSchemaQueryHandler.HandleAsync(Guid, CancellationToken)`; `GetTableDetailQueryHandler.HandleAsync(Guid, string table, CancellationToken)`

- [ ] **Step 1: Write the failing operations tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsSchemaTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsSchemaTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE events(
            id INTEGER PRIMARY KEY,
            tenant_id INTEGER NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'web',
            ts INTEGER NOT NULL);
        CREATE INDEX idx_events_tenant_ts ON events(tenant_id, ts);
        CREATE TABLE kv(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;
        CREATE VIEW v_tenant_events AS SELECT t.name, COUNT(*) AS n FROM events e JOIN tenants t ON t.id = e.tenant_id GROUP BY t.id;
        CREATE TRIGGER trg_events_ai AFTER INSERT ON events BEGIN SELECT 1; END;
        INSERT INTO tenants VALUES (1, 'acme'), (2, 'globex');
        INSERT INTO events VALUES (1, 1, 'page.view', 'web', 1759052466780), (2, 2, 'report.viewed', 'api', 1759052468112);
        """);

    [Fact]
    public async Task Listing_groups_schema_objects_and_hides_sqlite_internals()
    {
        var listing = await _db.Operations().ListSchemaAsync(_db.Secret());

        listing.Tables.Select(t => t.Name).Should().Equal("events", "kv", "tenants");
        listing.Views.Select(v => v.Name).Should().Equal("v_tenant_events");
        listing.Count(SchemaObjectType.Index).Should().Be(1, "only idx_events_tenant_ts -- the UNIQUE autoindex is internal");
        listing.Count(SchemaObjectType.Trigger).Should().Be(1);
        listing.SizesAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Row_estimates_appear_only_after_analyze()
    {
        (await _db.Operations().ListSchemaAsync(_db.Secret())).Tables.Should().OnlyContain(t => t.EstimatedRows == null);

        _db.Execute("ANALYZE;");
        var listing = await _db.Operations().ListSchemaAsync(_db.Secret());

        listing.Tables.Single(t => t.Name == "events").EstimatedRows.Should().Be(2);
    }

    [Fact]
    public async Task Detail_reads_columns_indexes_foreign_keys_and_ddl()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "events");

        detail.Type.Should().Be(SchemaObjectType.Table);
        detail.WithoutRowId.Should().BeFalse();
        detail.HasRowId.Should().BeTrue();
        detail.Ddl.Should().StartWith("CREATE TABLE events(");
        detail.Columns.Select(c => c.Name).Should().Equal("id", "tenant_id", "name", "source", "ts");
        detail.Columns[0].Should().Be(new ColumnInfo("id", "INTEGER", false, null, 1, false, false));
        detail.Columns[3].DefaultValue.Should().Be("'web'");
        detail.Columns[1].NotNull.Should().BeTrue();
        detail.Indexes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"]));
        detail.ForeignKeys.Should().ContainSingle().Which.Should().Be(
            new ForeignKeyInfo(0, "tenant_id", "tenants", "id", "NO ACTION", "CASCADE"));
    }

    [Fact]
    public async Task Detail_flags_without_rowid_tables()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "kv");

        detail.WithoutRowId.Should().BeTrue();
        detail.HasRowId.Should().BeFalse();
        detail.Indexes.Should().ContainSingle().Which.Origin.Should().Be("pk");
    }

    [Fact]
    public async Task Detail_of_a_view_has_columns_but_no_indexes_or_foreign_keys()
    {
        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "v_tenant_events");

        detail.Type.Should().Be(SchemaObjectType.View);
        detail.Columns.Select(c => c.Name).Should().Equal("name", "n");
        detail.Indexes.Should().BeEmpty();
        detail.ForeignKeys.Should().BeEmpty();
        detail.HasRowId.Should().BeFalse();
    }

    [Fact]
    public async Task Detail_of_an_unknown_name_throws_not_found()
    {
        var act = () => _db.Operations().GetTableDetailAsync(_db.Secret(), "nope");

        await act.Should().ThrowAsync<SqliteObjectNotFoundException>();
    }

    [Fact]
    public async Task Names_needing_quotes_are_handled()
    {
        _db.Execute("CREATE TABLE \"we\"\"ird name\" (x);");

        var detail = await _db.Operations().GetTableDetailAsync(_db.Secret(), "we\"ird name");

        detail.Columns.Should().ContainSingle().Which.Name.Should().Be("x");
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Write the failing handler tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Tables/SchemaQueryHandlerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class SchemaQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public SchemaQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    [Fact]
    public async Task Listing_returns_the_operations_result()
    {
        var listing = new SchemaListing([new SchemaObject("events", SchemaObjectType.Table, "events", 41_200_000, null)], false);
        _operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(listing);

        var result = await new ListSchemaQueryHandler(_operations, _connections, NullLogger<ListSchemaQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Value.Should().Be(listing);
    }

    [Fact]
    public async Task Detail_passes_the_table_name_and_maps_not_found()
    {
        _operations.GetTableDetailAsync(Secret, "gone", Arg.Any<CancellationToken>())
            .Returns<Task<TableDetail>>(_ => throw new SqliteObjectNotFoundException("gone"));

        var result = await new GetTableDetailQueryHandler(_operations, _connections, NullLogger<GetTableDetailQueryHandler>.Instance)
            .HandleAsync(_connectionId, "gone");

        result.Error.Should().Be("No table or view named 'gone' — it may have been dropped since the page loaded.");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS.

- [ ] **Step 4: Add the models**

Append to `src/Plugins/SbConsole.Plugins.Sqlite/Client/SchemaModels.cs`:

```csharp
public sealed record SchemaObject(string Name, SchemaObjectType Type, string TableName, long? EstimatedRows, long? Bytes);

/// <summary>Tables page left list (mockup 1b). Estimates come from sqlite_stat1, sizes from dbstat when present.</summary>
public sealed record SchemaListing(IReadOnlyList<SchemaObject> Objects, bool SizesAvailable)
{
    public IEnumerable<SchemaObject> Tables => Objects.Where(o => o.Type == SchemaObjectType.Table);

    public IEnumerable<SchemaObject> Views => Objects.Where(o => o.Type == SchemaObjectType.View);

    public int Count(SchemaObjectType type) => Objects.Count(o => o.Type == type);
}

/// <summary>pragma_table_xinfo row. Hidden = virtual-table hidden column (not in SELECT *); Generated = generated column.</summary>
public sealed record ColumnInfo(
    string Name, string DeclaredType, bool NotNull, string? DefaultValue, int PrimaryKeyOrdinal, bool Hidden, bool Generated);

/// <summary>pragma_index_list + pragma_index_info. Origin: "c" (CREATE INDEX), "u" (UNIQUE), "pk".</summary>
public sealed record IndexInfo(string Name, bool Unique, string Origin, bool Partial, IReadOnlyList<string> Columns);

public sealed record ForeignKeyInfo(int Id, string FromColumn, string ToTable, string? ToColumn, string OnUpdate, string OnDelete);

public sealed record TableDetail(
    string Name,
    SchemaObjectType Type,
    string? Ddl,
    bool WithoutRowId,
    long? EstimatedRows,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys)
{
    public bool HasRowId => Type == SchemaObjectType.Table && !WithoutRowId;
}
```

- [ ] **Step 5: Extend the interface and implement**

Add to `ISqliteOperations`:

```csharp
    Task<SchemaListing> ListSchemaAsync(string secret, CancellationToken ct = default);

    Task<TableDetail> GetTableDetailAsync(string secret, string name, CancellationToken ct = default);
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Schema.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<SchemaListing> ListSchemaAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var rows = ReadStat1Rows(c);
            var sizes = TryReadDbstatSizes(c);
            var objects = ReadSchemaObjects(c)
                .Select(o => new SchemaObject(
                    o.Name,
                    o.Type,
                    o.TableName,
                    o.Type == SchemaObjectType.Table ? Lookup(rows, o.Name) : null,
                    sizes is null ? null : Lookup(sizes, o.Name)))
                .ToList();
            return new SchemaListing(objects, sizes is not null);
        }, ct);

    public Task<TableDetail> GetTableDetailAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, c => ReadDetail(c, name), ct);

    /// <summary>
    /// The gate for every per-table query: the name must be a table or view in sqlite_schema before
    /// it is ever quoted into SQL.
    /// </summary>
    internal static SchemaRow RequireObject(SqliteConnection c, string name) =>
        SqliteSql.Rows(c, "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name = $name AND type IN ('table', 'view')",
                ReadSchemaRow, ("$name", name))
            .FirstOrDefault()
        ?? throw new SqliteObjectNotFoundException(name);

    internal static TableDetail ReadDetail(SqliteConnection c, string name)
    {
        var obj = RequireObject(c, name);
        var isTable = obj.Type == SchemaObjectType.Table;

        var columns = SqliteSql.Rows(c,
            "SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($name)",
            r => new ColumnInfo(
                r.GetString(0),
                r.GetString(1),
                r.GetInt64(2) != 0,
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4),
                Hidden: r.GetInt64(5) == 1,
                Generated: r.GetInt64(5) is 2 or 3),
            ("$name", obj.Name));

        IReadOnlyList<IndexInfo> indexes = isTable ? ReadIndexes(c, obj.Name) : [];

        IReadOnlyList<ForeignKeyInfo> foreignKeys = isTable
            ? SqliteSql.Rows(c,
                "SELECT id, \"from\", \"table\", \"to\", on_update, on_delete FROM pragma_foreign_key_list($name) ORDER BY id, seq",
                r => new ForeignKeyInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5)),
                ("$name", obj.Name))
            : [];

        var withoutRowId = isTable
            && SqliteSql.Long(c, "SELECT wr FROM pragma_table_list WHERE schema = 'main' AND name = $name", ("$name", obj.Name)) != 0;

        return new TableDetail(
            obj.Name, obj.Type, obj.Sql, withoutRowId,
            isTable ? Lookup(ReadStat1Rows(c), obj.Name) : null,
            columns, indexes, foreignKeys);
    }

    private static List<IndexInfo> ReadIndexes(SqliteConnection c, string table) =>
        SqliteSql.Rows(c, "SELECT name, \"unique\", origin, partial FROM pragma_index_list($name) ORDER BY name",
                r => (Name: r.GetString(0), Unique: r.GetInt64(1) != 0, Origin: r.GetString(2), Partial: r.GetInt64(3) != 0),
                ("$name", table))
            .Select(i => new IndexInfo(i.Name, i.Unique, i.Origin, i.Partial,
                SqliteSql.Rows(c, "SELECT name FROM pragma_index_info($index) ORDER BY seqno",
                    r => r.IsDBNull(0) ? "(expression)" : r.GetString(0), ("$index", i.Name))))
            .ToList();
}
```

- [ ] **Step 6: Add the handlers**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ListSchemaQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class ListSchemaQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<ListSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<SchemaListing>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.schema.list",
            secret => operations.ListSchemaAsync(secret, ct), ct);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/GetTableDetailQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class GetTableDetailQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetTableDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<TableDetail>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.detail",
            secret => operations.GetTableDetailAsync(secret, table, ct), ct);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS.

- [ ] **Step 8: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): schema listing and table detail (columns, indexes, FKs, DDL)"
```

---

### Task 8: Column notes, data preview and exact row count

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/RowModels.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Rows.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ColumnNoteBuilder.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/GetColumnSamplesQueryHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/PreviewRowsQueryHandler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/CountRowsQueryHandler.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsRowsTests.cs`, `.../Tables/ColumnNoteBuilderTests.cs`, `.../Tables/RowsQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `ReadDetail`, `RequireObject`, `TableDetail`, `ColumnInfo` (Task 7).
- Produces:
  - `enum CellKind { Null, Integer, Real, Text, Blob }`; `record CellValue(CellKind Kind, string Display, long? Integer = null, bool Truncated = false)`
  - `record RowPage(string Sql, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<CellValue>> Rows)` with `const int PreviewLimit = 50`
  - `record ColumnSample(string Column, int SampledRows, int NonNullCount, int DistinctCount, int TextCount, int JsonCount, int IntegerCount, int EpochMsCount)` with `const int DefaultSampleRows = 1000`, `const long EpochMsMin = 946684800000`, `const long EpochMsMax = 4102444800000`
  - `ISqliteOperations.SampleColumnsAsync(string secret, string name, int sampleRows, CancellationToken ct = default) : Task<IReadOnlyList<ColumnSample>>`
  - `ISqliteOperations.PreviewRowsAsync(string secret, string name, int limit, CancellationToken ct = default) : Task<RowPage>`
  - `ISqliteOperations.CountRowsAsync(string secret, string name, CancellationToken ct = default) : Task<long>`
  - `ColumnNoteBuilder.Build(TableDetail detail, IReadOnlyList<ColumnSample>? samples) : IReadOnlyDictionary<string, IReadOnlyList<string>>`; `ColumnNoteBuilder.UnixMsNote = "unix ms? (sampled)"`
  - `GetColumnSamplesQueryHandler.HandleAsync(Guid, string table, ct) : Task<PluginResult<IReadOnlyList<ColumnSample>>>`
  - `PreviewRowsQueryHandler.HandleAsync(Guid, string table, ct) : Task<PluginResult<RowPage>>`
  - `CountRowsQueryHandler.HandleAsync(Guid, string table, ct) : Task<PluginResult<long>>`

- [ ] **Step 1: Write the failing operations tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteOperationsRowsTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsRowsTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE events(id INTEGER PRIMARY KEY, name TEXT, ts INTEGER, props TEXT, payload BLOB, score REAL);
        INSERT INTO events VALUES
          (1, 'page.view', 1759052466780, '{"path":"/a"}', NULL, 1.5),
          (2, 'page.view', 1759052468112, '{"path":"/b"}', x'00010203', NULL),
          (3, 'export.csv', 1759052461004, 'not json', NULL, NULL),
          (4, NULL, 1759052455201, NULL, NULL, NULL);
        CREATE TABLE kv(k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID;
        INSERT INTO kv VALUES ('a', '1'), ('c', '3'), ('b', '2');
        CREATE VIEW v_names AS SELECT name FROM events;
        """);

    [Fact]
    public async Task Preview_returns_newest_rows_first_with_the_sql_it_ran()
    {
        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 2);

        page.Sql.Should().Be("SELECT * FROM \"events\" ORDER BY rowid DESC LIMIT 2");
        page.Columns.Should().Equal("id", "name", "ts", "props", "payload", "score");
        page.Rows.Select(r => r[0].Display).Should().Equal("4", "3");
    }

    [Fact]
    public async Task Preview_renders_null_blob_real_and_integer_cells()
    {
        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 50);
        var row2 = page.Rows.Single(r => r[0].Display == "2");
        var row1 = page.Rows.Single(r => r[0].Display == "1");

        row2[4].Should().Be(new CellValue(CellKind.Blob, "BLOB · 4 bytes"));
        row2[5].Should().Be(new CellValue(CellKind.Null, "NULL"));
        row1[5].Should().Be(new CellValue(CellKind.Real, "1.5"));
        row1[2].Should().Be(new CellValue(CellKind.Integer, "1759052466780", 1759052466780));
    }

    [Fact]
    public async Task Preview_truncates_long_text()
    {
        _db.Execute($"INSERT INTO events(id, props) VALUES (5, '{new string('x', 500)}');");

        var page = await _db.Operations().PreviewRowsAsync(_db.Secret(), "events", 1);

        page.Rows[0][3].Truncated.Should().BeTrue();
        page.Rows[0][3].Display.Should().HaveLength(SqliteOperations.MaxCellText + 1).And.EndWith("…");
    }

    [Fact]
    public async Task Preview_orders_without_rowid_tables_by_primary_key_and_views_not_at_all()
    {
        var kv = await _db.Operations().PreviewRowsAsync(_db.Secret(), "kv", 50);
        var view = await _db.Operations().PreviewRowsAsync(_db.Secret(), "v_names", 50);

        kv.Sql.Should().Be("SELECT * FROM \"kv\" ORDER BY \"k\" DESC LIMIT 50");
        kv.Rows.Select(r => r[0].Display).Should().Equal("c", "b", "a");
        view.Sql.Should().Be("SELECT * FROM \"v_names\" LIMIT 50");
    }

    [Fact]
    public async Task Count_is_exact()
    {
        (await _db.Operations().CountRowsAsync(_db.Secret(), "events")).Should().Be(4);
    }

    [Fact]
    public async Task Count_refuses_names_not_in_the_schema()
    {
        var act = () => _db.Operations().CountRowsAsync(_db.Secret(), "events; DROP TABLE events");

        await act.Should().ThrowAsync<SqliteObjectNotFoundException>();
    }

    [Fact]
    public async Task Samples_count_json_epoch_ms_and_distinct_values()
    {
        var samples = await _db.Operations().SampleColumnsAsync(_db.Secret(), "events", 1000);

        samples.Single(s => s.Column == "props").Should().Be(new ColumnSample("props", 4, 3, 3, 3, 2, 0, 0));
        samples.Single(s => s.Column == "ts").Should().Be(new ColumnSample("ts", 4, 4, 4, 0, 0, 4, 4));
        samples.Single(s => s.Column == "name").Should().Be(new ColumnSample("name", 4, 3, 2, 3, 0, 0, 0));
    }

    [Fact]
    public async Task Samples_respect_the_sample_size()
    {
        var samples = await _db.Operations().SampleColumnsAsync(_db.Secret(), "events", 2);

        samples.Should().OnlyContain(s => s.SampledRows == 2);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Write the failing note-builder tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Tables/ColumnNoteBuilderTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class ColumnNoteBuilderTests
{
    private static TableDetail Events(bool withoutRowId = false) => new(
        "events", SchemaObjectType.Table, "CREATE TABLE events(...)", withoutRowId, 41_200_000,
        [
            new ColumnInfo("id", "INTEGER", false, null, 1, false, false),
            new ColumnInfo("session_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("tenant_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("props", "TEXT", false, null, 0, false, false),
            new ColumnInfo("ts", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("source", "TEXT", true, "'web'", 0, false, false),
        ],
        [new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"])],
        [new ForeignKeyInfo(0, "session_id", "sessions", "id", "NO ACTION", "CASCADE")]);

    [Fact]
    public void Schema_notes_appear_without_samples()
    {
        var notes = ColumnNoteBuilder.Build(Events(), null);

        notes["id"].Should().Equal("alias of rowid");
        notes["session_id"].Should().Equal("→ sessions.id · ON DELETE CASCADE");
        notes["tenant_id"].Should().Equal("indexed · idx_events_tenant_ts");
        notes["ts"].Should().BeEmpty("it's the second column of the index, not its leading one");
    }

    [Fact]
    public void An_integer_pk_on_a_without_rowid_table_is_not_a_rowid_alias()
    {
        ColumnNoteBuilder.Build(Events(withoutRowId: true), null)["id"].Should().BeEmpty();
    }

    [Fact]
    public void Sample_notes_cover_json_epoch_ms_and_distinct_counts()
    {
        ColumnSample[] samples =
        [
            new("props", 1000, 992, 992, 992, 984, 0, 0),
            new("ts", 1000, 1000, 1000, 0, 0, 1000, 1000),
            new("source", 1000, 1000, 4, 1000, 0, 0, 0),
        ];

        var notes = ColumnNoteBuilder.Build(Events(), samples);

        notes["props"].Should().Equal("JSON in 99.2% of sampled rows");
        notes["ts"].Should().Equal(ColumnNoteBuilder.UnixMsNote);
        notes["source"].Should().Equal("4 distinct ≈ (sampled)");
    }

    [Fact]
    public void Integers_outside_the_epoch_ms_range_get_no_date_hint()
    {
        var notes = ColumnNoteBuilder.Build(Events(), [new ColumnSample("ts", 10, 10, 10, 0, 0, 10, 9)]);

        notes["ts"].Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Write the failing handler tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Tables/RowsQueryHandlerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class RowsQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public RowsQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    [Fact]
    public async Task Samples_use_the_default_sample_size()
    {
        IReadOnlyList<ColumnSample> samples = [new ColumnSample("ts", 1000, 1000, 1000, 0, 0, 1000, 1000)];
        _operations.SampleColumnsAsync(Secret, "events", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>()).Returns(samples);

        var result = await new GetColumnSamplesQueryHandler(_operations, _connections, NullLogger<GetColumnSamplesQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().BeSameAs(samples);
    }

    [Fact]
    public async Task Preview_uses_the_preview_limit()
    {
        var page = new RowPage("SELECT 1", ["x"], []);
        _operations.PreviewRowsAsync(Secret, "events", RowPage.PreviewLimit, Arg.Any<CancellationToken>()).Returns(page);

        var result = await new PreviewRowsQueryHandler(_operations, _connections, NullLogger<PreviewRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Count_returns_the_exact_count()
    {
        _operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(41_204_117L);

        var result = await new CountRowsQueryHandler(_operations, _connections, NullLogger<CountRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().Be(41_204_117L);
    }

    [Fact]
    public async Task A_cancelled_count_rethrows_instead_of_reporting_an_error()
    {
        using var cts = new CancellationTokenSource();
        _operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns<Task<long>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => new CountRowsQueryHandler(_operations, _connections, NullLogger<CountRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS.

- [ ] **Step 5: Add the models and interface methods**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/RowModels.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

public enum CellKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

/// <summary>A preview cell, already rendered for display. Integer keeps the raw value so the page can add a date hint.</summary>
public sealed record CellValue(CellKind Kind, string Display, long? Integer = null, bool Truncated = false);

public sealed record RowPage(string Sql, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<CellValue>> Rows)
{
    public const int PreviewLimit = 50;
}

/// <summary>
/// Aggregates over the first N rows of a table, per column (design spec §6 "Notes column").
/// EpochMsCount = INTEGER values inside 2000-01-01..2100-01-01 as unix milliseconds.
/// </summary>
public sealed record ColumnSample(
    string Column, int SampledRows, int NonNullCount, int DistinctCount, int TextCount, int JsonCount, int IntegerCount, int EpochMsCount)
{
    public const int DefaultSampleRows = 1000;
    public const long EpochMsMin = 946_684_800_000;
    public const long EpochMsMax = 4_102_444_800_000;
}
```

Add to `ISqliteOperations`:

```csharp
    Task<IReadOnlyList<ColumnSample>> SampleColumnsAsync(string secret, string name, int sampleRows, CancellationToken ct = default);

    Task<RowPage> PreviewRowsAsync(string secret, string name, int limit, CancellationToken ct = default);

    Task<long> CountRowsAsync(string secret, string name, CancellationToken ct = default);
```

- [ ] **Step 6: Implement the row operations**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Rows.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    internal const int MaxCellText = 200;

    public Task<RowPage> PreviewRowsAsync(string secret, string name, int limit, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var detail = ReadDetail(c, name);
            var sql = $"SELECT * FROM {SqliteSql.QuoteIdentifier(detail.Name)}{Order(detail, descending: true)} LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<CellValue>>();
            while (reader.Read())
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => ReadCell(reader, i)).ToList());
            }

            return new RowPage(sql, columns, rows);
        }, ct);

    public Task<long> CountRowsAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, c => SqliteSql.Long(c, $"SELECT COUNT(*) FROM {SqliteSql.QuoteIdentifier(RequireObject(c, name).Name)}"), ct);

    public Task<IReadOnlyList<ColumnSample>> SampleColumnsAsync(string secret, string name, int sampleRows, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<ColumnSample>>(secret, c =>
        {
            var detail = ReadDetail(c, name);
            var source = $"SELECT * FROM {SqliteSql.QuoteIdentifier(detail.Name)}{Order(detail, descending: false)} LIMIT {sampleRows.ToString(CultureInfo.InvariantCulture)}";
            var min = ColumnSample.EpochMsMin.ToString(CultureInfo.InvariantCulture);
            var max = ColumnSample.EpochMsMax.ToString(CultureInfo.InvariantCulture);

            return detail.Columns.Where(col => !col.Hidden).Select(col =>
            {
                var q = SqliteSql.QuoteIdentifier(col.Name);
                var sql = $$"""
                    WITH s AS ({{source}})
                    SELECT COUNT(*), COUNT({{q}}), COUNT(DISTINCT {{q}}),
                           COALESCE(SUM(typeof({{q}}) = 'text'), 0),
                           COALESCE(SUM(typeof({{q}}) = 'text' AND substr(ltrim({{q}}), 1, 1) IN ('{', '[') AND json_valid({{q}})), 0),
                           COALESCE(SUM(typeof({{q}}) = 'integer'), 0),
                           COALESCE(SUM(typeof({{q}}) = 'integer' AND {{q}} BETWEEN {{min}} AND {{max}}), 0)
                    FROM s
                    """;
                return SqliteSql.Rows(c, sql, r => new ColumnSample(
                    col.Name, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)))[0];
            }).ToList();
        }, ct);

    /// <summary>
    /// rowid order for rowid tables (newest first when descending), primary-key order for WITHOUT
    /// ROWID tables, none for views. A column literally named rowid shadows it, so fall back to the
    /// other two aliases SQLite accepts.
    /// </summary>
    private static string Order(TableDetail detail, bool descending)
    {
        var direction = descending ? " DESC" : "";
        if (detail.HasRowId)
        {
            var names = detail.Columns.Select(col => col.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var alias = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(a => !names.Contains(a));
            return alias is null ? "" : $" ORDER BY {alias}{direction}";
        }

        var pk = detail.Columns.Where(col => col.PrimaryKeyOrdinal > 0).OrderBy(col => col.PrimaryKeyOrdinal).ToList();
        return detail.Type == SchemaObjectType.Table && pk.Count > 0
            ? " ORDER BY " + string.Join(", ", pk.Select(col => SqliteSql.QuoteIdentifier(col.Name) + direction))
            : "";
    }

    internal static CellValue ReadCell(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return new CellValue(CellKind.Null, "NULL");
        }

        return reader.GetValue(ordinal) switch
        {
            long integer => new CellValue(CellKind.Integer, integer.ToString(CultureInfo.InvariantCulture), integer),
            double real => new CellValue(CellKind.Real, real.ToString("R", CultureInfo.InvariantCulture)),
            byte[] blob => new CellValue(CellKind.Blob, $"BLOB · {SqliteFormat.Count(blob.Length)} bytes"),
            var other => Text(Convert.ToString(other, CultureInfo.InvariantCulture) ?? ""),
        };

        static CellValue Text(string text) => text.Length <= MaxCellText
            ? new CellValue(CellKind.Text, text)
            : new CellValue(CellKind.Text, text[..MaxCellText] + "…", Truncated: true);
    }
}
```

(The SQL is a `$$"""` raw string: `{{expr}}` interpolates and a single `{` is a literal, so the SQL receives `IN ('{', '[')`.)

- [ ] **Step 7: Implement the note builder**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ColumnNoteBuilder.cs`:

```csharp
using System.Globalization;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>
/// The Tables page's Notes column (mockup 1b): schema-derived notes are exact, sample-derived ones
/// say "sampled". Values are never altered -- these are hints only.
/// </summary>
public static class ColumnNoteBuilder
{
    public const string UnixMsNote = "unix ms? (sampled)";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Build(TableDetail detail, IReadOnlyList<ColumnSample>? samples)
    {
        var notes = detail.Columns.ToDictionary(c => c.Name, _ => new List<string>(), StringComparer.Ordinal);

        // INTEGER PRIMARY KEY on a rowid table *is* the rowid (exact declared type, single-column PK).
        var pk = detail.Columns.Where(c => c.PrimaryKeyOrdinal > 0).ToList();
        if (detail.HasRowId && pk is [var only] && string.Equals(only.DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            notes[only.Name].Add("alias of rowid");
        }

        foreach (var fk in detail.ForeignKeys)
        {
            if (notes.TryGetValue(fk.FromColumn, out var list))
            {
                list.Add($"→ {fk.ToTable}.{fk.ToColumn ?? "rowid"} · ON DELETE {fk.OnDelete}");
            }
        }

        foreach (var index in detail.Indexes.Where(i => i.Origin != "pk" && i.Columns.Count > 0))
        {
            if (notes.TryGetValue(index.Columns[0], out var list) && !list.Any(n => n.StartsWith("indexed", StringComparison.Ordinal)))
            {
                list.Add($"indexed · {index.Name}");
            }
        }

        foreach (var sample in samples ?? Array.Empty<ColumnSample>())
        {
            if (!notes.TryGetValue(sample.Column, out var list) || sample.NonNullCount == 0)
            {
                continue;
            }

            if (sample.JsonCount > 0)
            {
                var percent = (100.0 * sample.JsonCount / sample.NonNullCount).ToString("0.#", CultureInfo.InvariantCulture);
                list.Add($"JSON in {percent}% of sampled rows");
            }

            if (sample.IntegerCount > 0 && sample.EpochMsCount == sample.IntegerCount)
            {
                list.Add(UnixMsNote);
            }
            else if (sample.DistinctCount < sample.NonNullCount)
            {
                list.Add($"{SqliteFormat.Count(sample.DistinctCount)} distinct ≈ (sampled)");
            }
        }

        return notes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}
```

- [ ] **Step 8: Add the handlers**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/GetColumnSamplesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class GetColumnSamplesQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<GetColumnSamplesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<ColumnSample>>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.samples",
            secret => operations.SampleColumnsAsync(secret, table, ColumnSample.DefaultSampleRows, ct), ct);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/PreviewRowsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed class PreviewRowsQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<PreviewRowsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<RowPage>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.preview",
            secret => operations.PreviewRowsAsync(secret, table, RowPage.PreviewLimit, ct), ct);
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/CountRowsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>Exact COUNT(*) -- a click, never a default, because it walks the whole table (mockup 1b footnote).</summary>
public sealed class CountRowsQueryHandler(
    ISqliteOperations operations, IConnectionProvider connections, ILogger<CountRowsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<long>> HandleAsync(Guid connectionId, string table, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.table.count",
            secret => operations.CountRowsAsync(secret, table, ct), ct);
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS.

- [ ] **Step 10: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): sampled column notes, data preview and exact row count"
```

---

### Task 9: CSV export

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/CsvFormat.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/CsvExport.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Export.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/Client/ISqliteOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ExportTableCsvCommandHandler.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/CsvExportTests.cs`, `tests/SbConsole.Plugins.Sqlite.Tests/Tables/ExportTableCsvCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `RequireObject` (Task 7), `HandlerRunner.AuditQuietlyAsync` (Task 4).
- Produces:
  - `CsvFormat.Field(string?) : string`, `CsvFormat.Row(IEnumerable<string?>) : string`
  - `record CsvExport(string FileName, byte[] Content, int RowCount, bool Truncated)` with `const int MaxRows = 100_000`
  - `ISqliteOperations.ExportCsvAsync(string secret, string name, int maxRows, CancellationToken ct = default) : Task<CsvExport>`
  - `record ExportTableCsvCommand(Guid ConnectionId, string ConnectionName, string Table)`; `ExportTableCsvCommandHandler.HandleAsync(cmd, ct) : Task<PluginResult<CsvExport>>`, `const string Action = "sqlite.table.export"`

- [ ] **Step 1: Write the failing tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/CsvExportTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class CsvExportTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Fields_are_quoted_per_rfc_4180(string? value, string expected) =>
        CsvFormat.Field(value).Should().Be(expected);

    [Fact]
    public async Task Export_writes_a_header_then_rows_with_null_and_blob_handling()
    {
        using var db = new TempDatabase("""
            CREATE TABLE "my table"(id INTEGER PRIMARY KEY, name TEXT, data BLOB);
            INSERT INTO "my table" VALUES (1, 'a,b', x'0102'), (2, NULL, NULL);
            """);

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "my table", 100);

        export.FileName.Should().Be("my_table.csv");
        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeFalse();
        Encoding.UTF8.GetString(export.Content).Should().Be("id,name,data\r\n1,\"a,b\",AQI=\r\n2,,\r\n");
    }

    [Fact]
    public async Task Export_stops_at_the_cap_and_says_so()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1), (2), (3);");

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 2);

        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeTrue();
    }
}
```

Create `tests/SbConsole.Plugins.Sqlite.Tests/Tables/ExportTableCsvCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class ExportTableCsvCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    private ExportTableCsvCommandHandler Handler() =>
        new(_operations, _connections, _audit, NullLogger<ExportTableCsvCommandHandler>.Instance);

    [Fact]
    public async Task Export_is_capped_and_audited_with_the_row_count()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        var export = new CsvExport("events.csv", [1, 2], 100_000, Truncated: true);
        _operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, Arg.Any<CancellationToken>()).Returns(export);

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.Value.Should().BeSameAs(export);
        await _audit.Received(1).RecordAsync("sqlite.table.export", "sqlite-reports/events", ActionRisk.Safe, true,
            "100,000 rows (truncated at 100,000)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_export_is_audited_as_failed()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, Arg.Any<CancellationToken>())
            .Returns<Task<CsvExport>>(_ => throw new SqliteObjectNotFoundException("events"));

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.table.export", "sqlite-reports/events", ActionRisk.Safe, false,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_auditing()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: build FAILS.

- [ ] **Step 3: Implement CSV pieces**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/CsvFormat.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>RFC 4180 fields: quote when the value holds a quote, comma, CR or LF; double embedded quotes. Values are exported verbatim.</summary>
public static class CsvFormat
{
    public static string Field(string? value)
    {
        if (value is null)
        {
            return "";
        }

        return value.AsSpan().IndexOfAny("\",\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    public static string Row(IEnumerable<string?> values) => string.Join(",", values.Select(Field));
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/CsvExport.cs`:

```csharp
namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// A whole table as CSV, built in memory and handed to the browser through a DotNetStreamReference
/// (the host has no plugin-endpoint seam -- design spec §6), hence the hard row cap.
/// </summary>
public sealed record CsvExport(string FileName, byte[] Content, int RowCount, bool Truncated)
{
    public const int MaxRows = 100_000;
}
```

Add to `ISqliteOperations`:

```csharp
    Task<CsvExport> ExportCsvAsync(string secret, string name, int maxRows, CancellationToken ct = default);
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteOperations.Export.cs`:

```csharp
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<CsvExport> ExportCsvAsync(string secret, string name, int maxRows, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var obj = RequireObject(c, name);
            using var command = c.CreateCommand();
            // One extra row tells "exactly maxRows" apart from "more than maxRows".
            command.CommandText = $"SELECT * FROM {SqliteSql.QuoteIdentifier(obj.Name)} LIMIT {(maxRows + 1).ToString(CultureInfo.InvariantCulture)}";
            using var reader = command.ExecuteReader();

            var csv = new StringBuilder();
            csv.Append(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName))).Append("\r\n");
            var count = 0;
            var truncated = false;
            while (reader.Read())
            {
                if (count == maxRows)
                {
                    truncated = true;
                    break;
                }

                csv.Append(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(i => CsvValue(reader, i)))).Append("\r\n");
                count++;
            }

            return new CsvExport($"{SafeFileName(obj.Name)}.csv", Encoding.UTF8.GetBytes(csv.ToString()), count, truncated);
        }, ct);

    private static string? CsvValue(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : reader.GetValue(ordinal) switch
        {
            byte[] blob => Convert.ToBase64String(blob),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };

    internal static string SafeFileName(string name) =>
        new(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
}
```

- [ ] **Step 4: Implement the handler**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Tables/ExportTableCsvCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

public sealed record ExportTableCsvCommand(Guid ConnectionId, string ConnectionName, string Table);

/// <summary>Safe (read-only) but audited: data leaves the server (design spec §6).</summary>
public sealed class ExportTableCsvCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ExportTableCsvCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.table.export";

    public async Task<PluginResult<CsvExport>> HandleAsync(ExportTableCsvCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Table}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<CsvExport>.Fail(HandlerRunner.ConnectionNotFound);
        }

        CsvExport export;
        try
        {
            export = await operations.ExportCsvAsync(secret, cmd.Table, CsvExport.MaxRows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<CsvExport>.Fail(friendly);
        }

        var detail = $"{SqliteFormat.Count(export.RowCount)} rows"
            + (export.Truncated ? $" (truncated at {SqliteFormat.Count(CsvExport.MaxRows)})" : "");
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Safe, succeeded: true, detail);
        return PluginResult<CsvExport>.Ok(export);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests`
Expected: PASS.

- [ ] **Step 6: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): audited CSV export capped at 100k rows"
```

---
### Task 10: Connection form

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteConnectionFields.razor`
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteConnectionFieldsTests.cs`, `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginTests.cs`

**Interfaces:**
- Consumes: `SqliteConnectionSettings`, `SqliteAccessMode` (Task 3).
- Produces: `SqliteConnectionFields` component with the host contract parameters `InitialSecret`, `SecretChanged`, `IsProd` (see `IPlugin.ConnectionFormComponentType` doc); `SqlitePlugin.ConnectionFormComponentType => typeof(SqliteConnectionFields)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Client/SqliteConnectionFieldsTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SqliteConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SqliteConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<SqliteConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Hydrates_the_fields_from_the_initial_secret()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db", BusyTimeoutMs: 2500, StatementTimeoutS: 12).ToSecretString(), out _);

        cut.Find(".sqlite-conn-path input").GetAttribute("value").Should().Be("/data/reports.db");
        cut.Find(".sqlite-conn-busy input").GetAttribute("value").Should().Be("2500");
        cut.Find(".sqlite-conn-statement input").GetAttribute("value").Should().Be("12");
    }

    [Fact]
    public void Editing_the_path_emits_a_full_secret_with_defaults()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".sqlite-conn-path input").Input("/data/new.db");

        SqliteConnectionSettings.Parse(emitted.Last()).Should().Be(new SqliteConnectionSettings("/data/new.db"));
    }

    [Fact]
    public void Editing_a_timeout_emits_it()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-statement input").Input("45");

        SqliteConnectionSettings.Parse(emitted.Last()).StatementTimeoutS.Should().Be(45);
    }

    [Fact]
    public void A_path_with_a_semicolon_is_flagged_and_not_emitted()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".sqlite-conn-path input").Input("/data/a;b.db");

        emitted.Should().BeEmpty();
        cut.Find(".sqlite-conn-path-error").TextContent.Should().Contain("can't contain ';'");
    }

    [Fact]
    public void Read_write_is_shown_but_disabled_in_phase_one()
    {
        var cut = RenderFields(null, out _);

        cut.Find(".sqlite-conn-mode-readwrite input").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-conn-mode-readwrite").TextContent.Should().Contain("not available yet");
    }
}
```

Append to `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginTests.cs`:

```csharp
    [Fact]
    public void Connection_form_is_the_sqlite_fields_component()
    {
        new SqlitePlugin().ConnectionFormComponentType.Should().Be(typeof(SqliteConnectionFields));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqliteConnectionFieldsTests|FullyQualifiedName~SqlitePluginTests"`
Expected: build FAILS — `SqliteConnectionFields` doesn't exist.

- [ ] **Step 3: Implement the component**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteConnectionFields.razor`:

```razor
@* src/Plugins/SbConsole.Plugins.Sqlite/Client/SqliteConnectionFields.razor -- mockup 1f's connection
   form: no host, port or credentials, just a file path and a mode (design spec §4.1). *@
@using MudBlazor

<MudTextField T="string" Class="sqlite-conn-path" Label="File path" Value="_path" ValueChanged="OnPathChanged" Immediate="true"
              HelperText="Path is resolved inside the SbConsole container — mount the volume there." />

@if (_pathError is not null)
{
    <MudAlert Severity="Severity.Warning" Dense="true" Class="mt-2 sqlite-conn-path-error">@_pathError</MudAlert>
}

<MudRadioGroup T="SqliteAccessMode" Value="_mode" ValueChanged="OnModeChanged" Class="mt-3">
    <MudRadio T="SqliteAccessMode" Value="SqliteAccessMode.ReadOnly" Class="sqlite-conn-mode-readonly">Read-only</MudRadio>
    <MudRadio T="SqliteAccessMode" Value="SqliteAccessMode.ReadWrite" Disabled="true" Class="sqlite-conn-mode-readwrite">Read-write — not available yet</MudRadio>
</MudRadioGroup>

<MudNumericField T="int" Class="mt-3 sqlite-conn-busy" Label="Busy timeout (ms)" Value="_busyTimeoutMs" ValueChanged="OnBusyTimeoutChanged" Min="1" Immediate="true" />
<MudNumericField T="int" Class="mt-3 sqlite-conn-statement" Label="Statement timeout (s)" Value="_statementTimeoutS" ValueChanged="OnStatementTimeoutChanged" Min="1" Max="600" Immediate="true" />

@code {
    [Parameter] public string? InitialSecret { get; set; }
    [Parameter] public EventCallback<string> SecretChanged { get; set; }
    [Parameter] public bool IsProd { get; set; }

    private string _path = "";
    private SqliteAccessMode _mode = SqliteAccessMode.ReadOnly;
    private int _busyTimeoutMs = SqliteConnectionSettings.DefaultBusyTimeoutMs;
    private int _statementTimeoutS = SqliteConnectionSettings.DefaultStatementTimeoutS;
    private string? _pathError;

    // Read once -- see IPlugin.ConnectionFormComponentType's doc comment: the host round-trips
    // whatever this component last emitted back into InitialSecret on every parent render, and
    // re-parsing in OnParametersSet would fight in-progress typing.
    protected override void OnInitialized()
    {
        if (string.IsNullOrEmpty(InitialSecret))
        {
            return;
        }

        try
        {
            var settings = SqliteConnectionSettings.Parse(InitialSecret);
            _path = settings.Path;
            _mode = settings.Mode;
            _busyTimeoutMs = settings.BusyTimeoutMs;
            _statementTimeoutS = settings.StatementTimeoutS;
        }
        catch (FormatException)
        {
            // Unparsable initial secret: keep the defaults so the form still renders.
        }
    }

    private Task OnPathChanged(string? value)
    {
        _path = value ?? "";
        return RaiseChangedAsync();
    }

    private Task OnModeChanged(SqliteAccessMode value)
    {
        _mode = value;
        return RaiseChangedAsync();
    }

    private Task OnBusyTimeoutChanged(int value)
    {
        _busyTimeoutMs = value;
        return RaiseChangedAsync();
    }

    private Task OnStatementTimeoutChanged(int value)
    {
        _statementTimeoutS = value;
        return RaiseChangedAsync();
    }

    private Task RaiseChangedAsync()
    {
        // Mid-typing states (empty path, a ';', a 0 timeout) can't be formatted: skip this keystroke's
        // SecretChanged so the last good secret stays in effect -- throwing out of a Blazor event
        // handler would kill the circuit (same rule as RedisConnectionFields).
        _pathError = _path.Contains(';') ? "The path can't contain ';'." : null;
        string secret;
        try
        {
            secret = new SqliteConnectionSettings(_path.Trim(), _mode, _busyTimeoutMs, _statementTimeoutS).ToSecretString();
        }
        catch (ArgumentException)
        {
            return Task.CompletedTask;
        }

        return SecretChanged.InvokeAsync(secret);
    }
}
```

In `SqlitePlugin.cs` add (below `ConnectionKindDisplayName`):

```csharp
    public Type? ConnectionFormComponentType => typeof(SqliteConnectionFields);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqliteConnectionFieldsTests|FullyQualifiedName~SqlitePluginTests"`
Expected: PASS. If a selector misses because MudBlazor 9.9 puts `Class` on a different wrapper element, inspect `cut.Markup` and adjust the selector (never the component's behavior).

- [ ] **Step 5: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): connection form (path, read-only mode, timeouts)"
```

---

### Task 11: Page chrome — stylesheet, JS module, header, connection picker, stat tile, test base

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/wwwroot/sqlite.css`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/wwwroot/sqlite.js`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Components/_Imports.razor`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqlitePageHeader.razor`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqliteConnectionPicker.razor`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqliteStatTile.razor`
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Pages/_Imports.razor`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Pages/SqlitePageTestBase.cs`, `tests/SbConsole.Plugins.Sqlite.Tests/Pages/SqliteConnectionPickerTests.cs`

**Interfaces:**
- Consumes: every handler from Tasks 5–9; `InMemoryPluginStore` (Task 6).
- Produces:
  - `SqlitePageHeader` (`Title`, `Chips`, `Meta`, `Actions` fragments) — also links `_content/SbConsole.Plugins.Sqlite/sqlite.css`
  - `SqliteConnectionPicker` (`EventCallback<ConnectionInfo> SelectionChanged`, `bool Loading`), keeps `?connectionId=` in the URL
  - `SqliteStatTile` (`Label`, `LabelClass`, `Value`, `ValueClass`, `Aside`, `ChildContent`, `Caption`, `Class`)
  - JS module `./_content/SbConsole.Plugins.Sqlite/sqlite.js` exporting `downloadFromStream(fileName, streamRef)`
  - Test base `SqlitePageTestBase` (fields `Operations`, `Connections`, `Audit`, `Store`, `Time`, `ConnectionId`, consts `Secret`, `ConnectionName`; `SettleAsync`, `RenderWithPopovers`)

- [ ] **Step 1: Write the test base and failing picker tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Pages/SqlitePageTestBase.cs`:

```csharp
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// BunitContext + Render<T> (not TestContext/RenderComponent): bUnit 2.10.3 marks the old names
// [Obsolete], which TreatWarningsAsErrors turns into a build failure (same as RedisPageTestBase).
public abstract class SqlitePageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected const string Secret = "Path=/data/reports.db";
    protected const string ConnectionName = "sqlite-reports";
    protected readonly ISqliteOperations Operations = Substitute.For<ISqliteOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
    protected readonly InMemoryPluginStore Store = new();
    protected readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 13, 9, 40, 51, TimeSpan.Zero));
    protected readonly Guid ConnectionId = Guid.NewGuid();

    protected SqlitePageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);

        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Connections);
        Services.AddSingleton(new GetOverviewQueryHandler(Operations, Connections, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new GetTableSizesQueryHandler(Operations, Connections, NullLogger<GetTableSizesQueryHandler>.Instance));
        Services.AddSingleton(new RunIntegrityCheckCommandHandler(Operations, Connections, Audit, Store, Time, NullLogger<RunIntegrityCheckCommandHandler>.Instance));
        Services.AddSingleton(new GetLastIntegrityResultQueryHandler(Store));
        Services.AddSingleton(new ListSchemaQueryHandler(Operations, Connections, NullLogger<ListSchemaQueryHandler>.Instance));
        Services.AddSingleton(new GetTableDetailQueryHandler(Operations, Connections, NullLogger<GetTableDetailQueryHandler>.Instance));
        Services.AddSingleton(new GetColumnSamplesQueryHandler(Operations, Connections, NullLogger<GetColumnSamplesQueryHandler>.Instance));
        Services.AddSingleton(new PreviewRowsQueryHandler(Operations, Connections, NullLogger<PreviewRowsQueryHandler>.Instance));
        Services.AddSingleton(new CountRowsQueryHandler(Operations, Connections, NullLogger<CountRowsQueryHandler>.Instance));
        Services.AddSingleton(new ExportTableCsvCommandHandler(Operations, Connections, Audit, NullLogger<ExportTableCsvCommandHandler>.Instance));
    }

    /// <summary>MudMenu items render into a MudPopoverProvider (hosted by the real layout), so menu tests render one alongside.</summary>
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPopovers(RenderFragment content)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.AddContent(1, content);
        };

        return Render(fragment);
    }

    protected static async Task SettleAsync<TComponent>(IRenderedComponent<TComponent> cut) where TComponent : IComponent
    {
        await Task.Delay(30);
        cut.Render();
    }
}
```

Create `tests/SbConsole.Plugins.Sqlite.Tests/Pages/SqliteConnectionPickerTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

public class SqliteConnectionPickerTests : SqlitePageTestBase
{
    private readonly Guid _secondId = Guid.NewGuid();

    private IRenderedComponent<SqliteConnectionPicker> RenderPicker(List<ConnectionInfo> published) =>
        Render<SqliteConnectionPicker>(p => p.Add(x => x.SelectionChanged, EventCallback.Factory.Create<ConnectionInfo>(this, published.Add)));

    private void TwoConnections() =>
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"], Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" }),
            new ConnectionInfo(_secondId, "sqlite-scratch", "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "scratch.db", ["Mode"] = "read-only" }),
        ]);

    [Fact]
    public void Publishes_the_first_connection_and_shows_its_file_and_prod_tag()
    {
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Should().ContainSingle().Which.Id.Should().Be(ConnectionId);
        cut.Find(".sqlite-selected-connection").TextContent.Should().Be(ConnectionName);
        cut.Find(".sqlite-prod-chip").TextContent.Should().Be("prod");
        cut.Find(".sqlite-file-chip").TextContent.Should().Contain("reports.db").And.Contain("read-only");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"connectionId={ConnectionId}");
    }

    [Fact]
    public void Honors_the_connection_id_in_the_query_string()
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/sqlite/overview?connectionId={_secondId}");
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Single().Id.Should().Be(_secondId);
        cut.FindAll(".sqlite-prod-chip").Should().BeEmpty();
    }

    [Fact]
    public void Explains_when_there_are_no_sqlite_connections()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(Array.Empty<ConnectionInfo>());
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Should().BeEmpty();
        cut.Find(".sqlite-no-connections").TextContent.Should().Contain("No SQLite connections yet");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqliteConnectionPickerTests"`
Expected: build FAILS — `SqliteConnectionPicker` doesn't exist.

- [ ] **Step 3: Add the imports and shared components**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Components/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@using SbConsole.Sdk
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Pages/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.JSInterop
@using MudBlazor
@using SbConsole.Sdk
@using SbConsole.Plugins.Sqlite.Components
@attribute [Authorize]
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqlitePageHeader.razor`:

```razor
@* The header bar every SQLite page opens with (mockup 1a-1e): title, connection chips, mono meta,
   right-aligned actions. Also links the plugin stylesheet, so a page that renders a header gets the
   sql-* classes with no host wiring (same approach as RedisPageHeader). *@
<HeadContent>
    <link rel="stylesheet" href="_content/SbConsole.Plugins.Sqlite/sqlite.css" />
</HeadContent>

<div class="sql-header">
    <span class="sql-title">@Title</span>
    @Chips
    @if (Meta is not null)
    {
        <span class="sql-meta">@Meta</span>
    }
    <span class="sql-spacer"></span>
    <div class="sql-actions">@Actions</div>
</div>

@code {
    [Parameter, EditorRequired] public string Title { get; set; } = "";

    [Parameter] public RenderFragment? Chips { get; set; }

    [Parameter] public RenderFragment? Meta { get; set; }

    [Parameter] public RenderFragment? Actions { get; set; }
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqliteStatTile.razor`:

```razor
@* One stat tile (mockup 1a's File / Journal / Page cache row). *@
<div class="sql-tile @Class">
    <div class="sql-tile-head">
        <span class="sql-overline @LabelClass">@Label</span>
        @if (Aside is not null)
        {
            <span class="sql-tile-aside">@Aside</span>
        }
    </div>
    <div class="sql-tile-value @ValueClass">@Value</div>
    @ChildContent
    @if (Caption is not null)
    {
        <div class="sql-tile-caption">@Caption</div>
    }
</div>

@code {
    [Parameter, EditorRequired] public string Label { get; set; } = "";

    [Parameter] public string? LabelClass { get; set; }

    [Parameter] public RenderFragment? Value { get; set; }

    [Parameter] public string? ValueClass { get; set; }

    [Parameter] public string? Aside { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public RenderFragment? Caption { get; set; }

    [Parameter] public string? Class { get; set; }
}
```

Create `src/Plugins/SbConsole.Plugins.Sqlite/Components/SqliteConnectionPicker.razor`:

```razor
@using Microsoft.AspNetCore.WebUtilities
@inject IConnectionProvider Connections
@inject NavigationManager Navigation

@* Header chips: the connection menu, then the file + mode (mockup 1a: "the connection is the file,
   so the page picker shows the file and its mode"). The choice lives in ?connectionId= so links
   stay shareable, matching RedisConnectionDatabasePicker minus the database axis. *@
@if (!_loaded)
{
    <MudProgressCircular Class="picker-loading" Size="Size.Small" Indeterminate="true" />
}
else if (_connections.Count == 0)
{
    <span class="sql-muted sqlite-no-connections">No SQLite connections yet — add one under Connections.</span>
}
else
{
    <MudMenu Class="sqlite-connection-menu" AnchorOrigin="Origin.BottomLeft" TransformOrigin="Origin.TopLeft">
        <ActivatorContent>
            <span class="sql-chip">
                <span class="sqlite-selected-connection">@_connection?.Name</span>
                @if (_connection?.IsProd == true)
                {
                    <span class="sql-prod sqlite-prod-chip">prod</span>
                }
                <span class="sql-chip-caret">▾</span>
            </span>
        </ActivatorContent>
        <ChildContent>
            @foreach (var connection in _connections)
            {
                <MudMenuItem Class="sqlite-connection-option" OnClick="@(() => SelectAsync(connection))">@connection.Name</MudMenuItem>
            }
        </ChildContent>
    </MudMenu>

    @if (_connection is { } selected && selected.Summary.TryGetValue("File", out var file))
    {
        <span class="sql-chip sql-chip-static sqlite-file-chip">
            <span class="sql-mono">@file</span>
            <span class="sql-chip-label">@selected.Summary.GetValueOrDefault("Mode")</span>
        </span>
    }

    @if (Loading)
    {
        <MudProgressCircular Class="page-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
    }
}

@code {
    public const string Kind = "sqlite";

    /// <summary>Raised once after the initial selection resolves, then on every change. Never raised when there are no connections.</summary>
    [Parameter] public EventCallback<ConnectionInfo> SelectionChanged { get; set; }

    [Parameter] public bool Loading { get; set; }

    private IReadOnlyList<ConnectionInfo> _connections = [];
    private ConnectionInfo? _connection;
    private bool _loaded;

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync(Kind);
        _loaded = true;
        if (_connections.Count == 0)
        {
            return;
        }

        var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
        ConnectionInfo? fromQuery = null;
        if (query.TryGetValue("connectionId", out var idValue) && Guid.TryParse(idValue.ToString(), out var id))
        {
            fromQuery = _connections.FirstOrDefault(c => c.Id == id);
        }

        await SelectAsync(fromQuery ?? _connections[0]);
    }

    private async Task SelectAsync(ConnectionInfo connection)
    {
        _connection = connection;
        var uri = Navigation.GetUriWithQueryParameter("connectionId", connection.Id.ToString());
        if (uri != Navigation.Uri)
        {
            Navigation.NavigateTo(uri, replace: true);
        }

        await SelectionChanged.InvokeAsync(connection);
    }
}
```

- [ ] **Step 4: Add the JS module**

Create `src/Plugins/SbConsole.Plugins.Sqlite/wwwroot/sqlite.js`:

```js
// Saves a file the server built (Tables page "Export CSV", design spec §6). The .NET side passes a
// DotNetStreamReference because the host has no plugin download endpoint.
export async function downloadFromStream(fileName, streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: "text/csv;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "export.csv";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}
```

- [ ] **Step 5: Add the stylesheet**

Create `src/Plugins/SbConsole.Plugins.Sqlite/wwwroot/sqlite.css`:

```css
/* SQLite plugin styles (mockup "SbConsole SQLite.dc.html"). Colors derive only from MudBlazor's
   --mud-palette-* tokens (CLAUDE.md theming rule) so light and dark both stay correct. Every class
   is prefixed sql- to stay out of other plugins' way. */
@import url("https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500&display=swap");

.sql-page {
    --sql-mono: "JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace;
    --sql-line: var(--mud-palette-lines-default);
    --sql-faint: color-mix(in srgb, var(--mud-palette-text-primary) 7%, transparent);
    --sql-panel: color-mix(in srgb, var(--mud-palette-background) 55%, var(--mud-palette-surface));
    --sql-muted: var(--mud-palette-text-secondary);
    --sql-accent: var(--mud-palette-primary);
    --sql-warn: var(--mud-palette-warning);
    --sql-danger: var(--mud-palette-error);
    --sql-ok: var(--mud-palette-success);
    background: var(--sql-panel);
    border: 1px solid var(--sql-line);
    border-radius: 10px;
    overflow: hidden;
    color: var(--mud-palette-text-primary);
    font-size: 14px;
}

.sql-mono { font-family: var(--sql-mono); }
.sql-muted { color: var(--sql-muted); }
.sql-warn { color: var(--sql-warn); }
.sql-danger { color: var(--sql-danger); }
.sql-ok { color: var(--sql-ok); }
.sql-spacer { flex: 1 1 auto; }
.sql-num { text-align: right; font-variant-numeric: tabular-nums; }

/* ---- header ---- */
.sql-header {
    display: flex; align-items: center; flex-wrap: wrap; gap: 10px 12px;
    min-height: 56px; padding: 8px 20px; border-bottom: 1px solid var(--sql-line);
}
.sql-title { font-size: 18px; font-weight: 500; margin-right: 4px; }
.sql-meta { font-family: var(--sql-mono); font-size: 13px; color: var(--sql-muted); overflow-wrap: anywhere; }
.sql-actions { display: flex; align-items: center; gap: 12px; }
.sql-chip {
    display: inline-flex; align-items: center; gap: 8px; height: 32px; padding: 0 10px;
    border: 1px solid var(--sql-line); border-radius: 6px; background: var(--mud-palette-background);
    font-family: var(--sql-mono); font-size: 14px; cursor: pointer; user-select: none;
}
.sql-chip:hover { border-color: color-mix(in srgb, var(--sql-accent) 60%, transparent); }
.sql-chip-static { cursor: default; }
.sql-chip-static:hover { border-color: var(--sql-line); }
.sql-chip-label { font-size: 12px; color: var(--sql-muted); }
.sql-chip-caret { font-size: 10px; color: var(--sql-muted); }
.sql-prod {
    display: inline-flex; align-items: center; height: 20px; padding: 0 7px; border-radius: 4px;
    font-family: "Inter", system-ui, sans-serif; font-size: 12px;
    color: var(--sql-warn); background: color-mix(in srgb, var(--sql-warn) 16%, transparent);
}

/* ---- buttons & links ---- */
.sql-btn {
    display: inline-flex; align-items: center; gap: 4px; height: 30px; padding: 0 12px;
    border-radius: 6px; border: 1px solid var(--sql-line); background: transparent;
    color: var(--mud-palette-text-primary); font: inherit; font-size: 13px; cursor: pointer; white-space: nowrap;
}
.sql-btn:hover { background: var(--sql-faint); }
.sql-btn:disabled { opacity: .45; cursor: not-allowed; }
.sql-link { border: 0; background: none; padding: 0; font: inherit; font-size: 13px; color: var(--sql-accent); cursor: pointer; }
.sql-link:hover { text-decoration: underline; }

/* ---- tiles ---- */
.sql-tiles { display: grid; gap: 12px; padding: 16px 20px 0; }
.ov-tiles { grid-template-columns: 2fr 1fr 1fr; }
.sql-tile { background: var(--mud-palette-surface); border-radius: 8px; padding: 12px 16px; min-width: 0; }
.sql-tile-head { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }
.sql-overline { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: var(--sql-muted); }
.sql-tile-aside { font-family: var(--sql-mono); font-size: 12px; color: var(--sql-muted); }
.sql-tile-value { font-size: 26px; font-weight: 500; margin: 4px 0 6px; font-variant-numeric: tabular-nums; }
.sql-tile-value small { font-size: 13px; font-weight: 400; margin-left: 6px; }
.sql-tile-caption { font-size: 12px; color: var(--sql-muted); }

/* ---- bars: used part (span) then free/remaining part (i) ---- */
.sql-bar { display: flex; height: 8px; border-radius: 4px; overflow: hidden; background: var(--sql-faint); margin: 6px 0; }
.sql-bar > span { background: var(--sql-accent); }
.sql-bar > i { background: color-mix(in srgb, var(--sql-warn) 45%, transparent); }
.sql-bar-thin { height: 4px; margin: 0; flex: 1 1 auto; min-width: 40px; }

/* ---- body, strips, banners, cards ---- */
.sql-body { padding: 16px 20px 20px; display: grid; gap: 12px; }
.sql-strip {
    display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 10px 14px;
    border-radius: 8px; background: var(--mud-palette-surface); border-left: 3px solid var(--sql-line);
}
.sql-strip-ok { border-left-color: var(--sql-ok); }
.sql-strip-danger { border-left-color: var(--sql-danger); }
.sql-banner { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 8px; }
.sql-banner-danger { background: color-mix(in srgb, var(--sql-danger) 12%, transparent); }
.sql-error-list {
    margin: 0; padding: 10px 14px; border-radius: 8px; font-family: var(--sql-mono); font-size: 12px;
    white-space: pre-wrap; background: color-mix(in srgb, var(--sql-danger) 8%, transparent);
}
.sql-cards { display: grid; grid-template-columns: 2fr 1fr; gap: 12px; }
.sql-card { background: var(--mud-palette-surface); border-radius: 8px; padding: 12px 16px; min-width: 0; }
.sql-card-head { display: flex; align-items: baseline; gap: 8px; margin-bottom: 8px; }
.sql-card-title { font-weight: 500; }
.sql-card-sub { font-family: var(--sql-mono); font-size: 12px; color: var(--sql-muted); }
.sql-size-row { display: flex; align-items: center; gap: 12px; padding: 4px 0; font-size: 13px; }
.sql-size-name { width: 30%; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sql-size-bytes, .sql-size-rows { width: 90px; flex: none; }
.sql-kv { display: grid; grid-template-columns: auto 1fr; gap: 6px 16px; margin: 0; font-size: 13px; }
.sql-kv dt { font-family: var(--sql-mono); color: var(--sql-muted); }
.sql-kv dd { margin: 0; }
.sql-footnote { font-size: 12px; color: var(--sql-muted); margin-top: 8px; }

/* ---- tables browser ---- */
.sql-split { display: grid; grid-template-columns: minmax(220px, 300px) 1fr; min-height: 480px; }
.sql-list { border-right: 1px solid var(--sql-line); padding: 12px; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.sql-filter {
    height: 32px; padding: 0 10px; margin-bottom: 8px; border-radius: 6px; border: 1px solid var(--sql-line);
    background: var(--mud-palette-background); color: var(--mud-palette-text-primary); font: inherit; font-size: 13px;
}
.sql-list-group { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: var(--sql-muted); margin: 10px 4px 4px; }
.sql-list-row {
    display: flex; align-items: center; gap: 8px; width: 100%; padding: 6px 8px; border: 0; border-radius: 6px;
    background: transparent; color: inherit; font: inherit; font-size: 13px; text-align: left; cursor: pointer;
}
.sql-list-row:hover { background: var(--sql-faint); }
.sql-list-row.is-selected { background: color-mix(in srgb, var(--sql-accent) 14%, transparent); }
.sql-list-name { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sql-detail { padding: 16px 20px; min-width: 0; }
.sql-detail-head { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
.sql-detail-title { font-size: 18px; }
.sql-detail-meta { font-size: 13px; color: var(--sql-muted); margin: 4px 0 12px; }
.sql-tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--sql-line); margin-bottom: 12px; overflow-x: auto; }
.sql-tab {
    border: 0; border-bottom: 2px solid transparent; background: none; color: var(--sql-muted);
    font: inherit; font-size: 13px; padding: 8px 10px; cursor: pointer; white-space: nowrap;
}
.sql-tab.is-active { color: var(--mud-palette-text-primary); border-bottom-color: var(--sql-accent); }
.sql-table { width: 100%; border-collapse: collapse; font-size: 13px; }
.sql-table th { text-align: left; font-weight: 500; color: var(--sql-muted); padding: 6px 8px; border-bottom: 1px solid var(--sql-line); }
.sql-table td { padding: 6px 8px; border-bottom: 1px solid var(--sql-faint); vertical-align: top; }
.sql-grid-wrap { overflow-x: auto; }
.sql-grid td { white-space: nowrap; max-width: 360px; overflow: hidden; text-overflow: ellipsis; }
.sql-cell-null, .sql-cell-blob { color: var(--sql-muted); font-style: italic; }
.sql-hint { display: block; font-size: 11px; }
.sql-badge { display: inline-block; font-size: 10px; padding: 1px 5px; border-radius: 4px; margin-right: 4px; }
.sql-badge-pk { color: var(--sql-accent); background: color-mix(in srgb, var(--sql-accent) 14%, transparent); }
.sql-badge-fk { color: var(--sql-warn); background: color-mix(in srgb, var(--sql-warn) 14%, transparent); }
.sql-ddl {
    margin: 0; padding: 12px; border-radius: 8px; background: var(--mud-palette-surface);
    font-family: var(--sql-mono); font-size: 12px; white-space: pre-wrap; overflow-x: auto;
}
.sql-caption { font-size: 12px; color: var(--sql-muted); margin-bottom: 8px; overflow-wrap: anywhere; }
.sql-empty { padding: 48px 20px; text-align: center; display: grid; gap: 8px; }
.sql-empty-title { font-size: 16px; font-weight: 500; }

@media (max-width: 900px) {
    .ov-tiles, .sql-cards, .sql-split { grid-template-columns: 1fr; }
    .sql-list { border-right: 0; border-bottom: 1px solid var(--sql-line); }
    .sql-header, .sql-tiles, .sql-body, .sql-detail { padding-left: 16px; padding-right: 16px; }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqliteConnectionPickerTests"`
Expected: PASS.

- [ ] **Step 7: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): page chrome — stylesheet, header, connection picker, stat tile, download module"
```

---

### Task 12: Overview page (mockup 1a)

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Pages/Overview.razor`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Pages/OverviewPageTests.cs`

**Interfaces:**
- Consumes: `GetOverviewQueryHandler`, `GetTableSizesQueryHandler`, `RunIntegrityCheckCommandHandler`, `GetLastIntegrityResultQueryHandler` (Tasks 5–6); `SqlitePageHeader`, `SqliteConnectionPicker`, `SqliteStatTile` (Task 11); `SqliteFormat` (Task 3).
- Produces: routable page `/p/sqlite/overview` (`SbConsole.Plugins.Sqlite.Pages.Overview`).

- [ ] **Step 1: Write the failing page tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Pages/OverviewPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// The page type is fully qualified: this namespace has SbConsole.Plugins.Sqlite as an ancestor,
// whose nested "Overview" namespace would win over a using directive (same as Redis's OverviewPageTests).
public class OverviewPageTests : SqlitePageTestBase
{
    private static SqliteOverview Sample(long freelist = 46_387, long? wal = 50_331_648) => new(
        "/data/reports.db", "3.53.3", 2_254_857_830, wal, new DateTimeOffset(2026, 10, 13, 9, 40, 51, TimeSpan.Zero),
        4096, 550_502, freelist, "WAL", "NORMAL", 1000, -2000, 5000);

    private static readonly TableSizes NoDbstat = new(false, 6, 19,
    [
        new TableSizeEntry("event_props", SchemaObjectType.Table, null, 88_000_000),
        new TableSizeEntry("events", SchemaObjectType.Table, null, 41_200_000),
        new TableSizeEntry("sessions", SchemaObjectType.Table, null, 2_418_902),
        new TableSizeEntry("users", SchemaObjectType.Table, null, 184_220),
        new TableSizeEntry("tenants", SchemaObjectType.Table, null, 412),
        new TableSizeEntry("schedules", SchemaObjectType.Table, null, null),
    ]);

    private void Arrange(SqliteOverview? overview = null, TableSizes? sizes = null)
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(overview ?? Sample());
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(sizes ?? NoDbstat);
    }

    private async Task<IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Overview>> RenderPageAsync()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Overview>();
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Renders_the_file_journal_and_page_cache_tiles()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-meta").TextContent.Should().Be("/data/reports.db · 3.53.3 · WAL");
        cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB").And.Contain("+ 48 MB -wal");
        cut.Find(".sqlite-overview-file").TextContent.Should().Contain("page 4 KB · 550,502 pages")
            .And.Contain("8% free pages (181 MB)").And.Contain("VACUUM").And.Contain("2026-10-13 09:40:51 UTC");
        cut.Find(".sqlite-overview-journal-mode").TextContent.Should().Be("WAL");
        cut.Find(".sqlite-overview-journal").TextContent.Should().Contain("synchronous NORMAL").And.Contain("checkpoint every 1,000 pages");
        cut.Find(".sqlite-overview-cache-pages").TextContent.Should().Be("500 p");
        cut.Find(".sqlite-overview-busy").TextContent.Should().Be("5,000 ms");
    }

    [Fact]
    public async Task A_file_without_free_pages_says_so()
    {
        Arrange(Sample(freelist: 0, wal: null));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-free").TextContent.Should().Contain("no free pages");
        cut.FindAll(".sqlite-overview-wal").Should().BeEmpty();
    }

    [Fact]
    public async Task Without_dbstat_the_panel_ranks_the_largest_tables_by_estimated_rows()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Largest tables");
        cut.Find(".sqlite-overview-sizes .sql-card-sub").TextContent.Should().Be("sqlite_stat1 · 6 tables, 19 indexes");
        var rows = cut.FindAll(".sqlite-overview-size-row");
        rows.Should().HaveCount(4);
        rows[0].TextContent.Should().Contain("event_props").And.Contain("88.0M rows ≈");
        cut.Find(".sqlite-overview-size-more").TextContent.Should().Contain("2 more");
        cut.Find(".sqlite-overview-no-dbstat").TextContent.Should().Contain("dbstat");
    }

    [Fact]
    public async Task With_dbstat_the_panel_shows_bytes_and_marks_indexes()
    {
        Arrange(sizes: new TableSizes(true, 1, 1,
        [
            new TableSizeEntry("events", SchemaObjectType.Table, 1_395_864_371, 41_200_000),
            new TableSizeEntry("idx_events_ts", SchemaObjectType.Index, 100_663_296, null),
        ]));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Size by table");
        var rows = cut.FindAll(".sqlite-overview-size-row");
        rows[0].TextContent.Should().Contain("1.3 GB").And.Contain("41.2M rows ≈");
        rows[1].TextContent.Should().Contain("96 MB").And.Contain("index");
        cut.FindAll(".sqlite-overview-no-dbstat").Should().BeEmpty();
    }

    [Fact]
    public async Task The_integrity_strip_says_never_run_until_a_result_is_stored()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("never run");
    }

    [Fact]
    public async Task The_integrity_strip_shows_the_last_stored_result()
    {
        Arrange();
        await IntegrityResultStore.WriteAsync(Store, ConnectionId,
            new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok");
        cut.Find(".sqlite-overview-integrity-meta").TextContent.Should()
            .Contain("2026-10-13 02:00 UTC").And.Contain("in 41 s").And.Contain("foreign_key_check 0 violations");
    }

    [Fact]
    public async Task Run_now_runs_the_check_audits_it_and_lists_the_errors()
    {
        Arrange();
        Operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>()).Returns(new IntegrityCheckOutcome(
            ["*** in database main ***", "row 2041 missing from index idx_events_ts"], 0));
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-run-integrity").Click();
        await SettleAsync(cut);

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("2 errors");
        cut.Find(".sqlite-overview-integrity-errors").TextContent.Should().Contain("row 2041 missing from index idx_events_ts");
        await Audit.Received(1).RecordAsync("sqlite.integrity.check", ConnectionName, ActionRisk.Safe, true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_load_shows_the_friendly_error_and_retry_recovers()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SqliteOverview>>(_ => throw new SqliteException("unable to open database file", 14));
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(NoDbstat);
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-error").TextContent.Should().Contain("File not found, or not readable by the SbConsole container user.");

        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample());
        cut.Find(".sqlite-overview-retry").Click();
        await SettleAsync(cut);

        cut.FindAll(".sqlite-overview-error").Should().BeEmpty();
        cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~OverviewPageTests"`
Expected: build FAILS — `SbConsole.Plugins.Sqlite.Pages.Overview` doesn't exist.

- [ ] **Step 3: Implement the page**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Pages/Overview.razor`:

```razor
@* src/Plugins/SbConsole.Plugins.Sqlite/Pages/Overview.razor -- mockup 1a, design spec §5. *@
@page "/p/sqlite/overview"
@using System.Globalization
@using SbConsole.Plugins.Sqlite.Client
@using SbConsole.Plugins.Sqlite.Overview
@inject GetOverviewQueryHandler GetOverview
@inject GetTableSizesQueryHandler GetTableSizes
@inject RunIntegrityCheckCommandHandler RunIntegrityCheck
@inject GetLastIntegrityResultQueryHandler GetLastIntegrity
@inject TimeProvider Clock
@implements IDisposable

<PageTitle>SQLite overview</PageTitle>

<div class="sql-page">
    <SqlitePageHeader Title="Overview">
        <Chips>
            <SqliteConnectionPicker Loading="_loading" SelectionChanged="OnSelectionChangedAsync" />
        </Chips>
        <Meta>
            @if (_overview is { } meta)
            {
                <span class="sqlite-overview-meta">@meta.Path · @meta.SqliteVersion · @meta.JournalMode</span>
            }
        </Meta>
        <Actions>
            @if (_loadedAt is { } at)
            {
                <span class="sql-meta sqlite-overview-loaded">PRAGMA @at.ToString("HH:mm:ss", Inv) UTC</span>
            }
            @if (_connection is not null)
            {
                <button type="button" class="sql-btn sqlite-overview-refresh" disabled="@_loading" @onclick="LoadAsync">Refresh</button>
            }
        </Actions>
    </SqlitePageHeader>

    @if (_error is not null)
    {
        <div class="sql-body">
            <div class="sql-banner sql-banner-danger sqlite-overview-error">
                <span>@_error</span>
                <span class="sql-spacer"></span>
                <a class="sql-link" href="/connections">Connection settings</a>
                <button type="button" class="sql-btn sqlite-overview-retry" @onclick="LoadAsync">Retry</button>
            </div>
        </div>
    }
    else if (_overview is { } o)
    {
        <div class="sql-tiles ov-tiles">
            <SqliteStatTile Label="File" Class="sqlite-overview-file" ValueClass="sqlite-overview-file-size"
                            Aside="@($"page {SqliteFormat.Bytes(o.PageSize)} · {SqliteFormat.Count(o.PageCount)} pages")">
                <Value>
                    @SqliteFormat.Bytes(o.FileBytes)
                    @if (o.WalBytes is { } wal)
                    {
                        <small class="sql-muted sqlite-overview-wal">+ @SqliteFormat.Bytes(wal) -wal</small>
                    }
                </Value>
                <ChildContent>
                    @* Free pages are drawn inside the file bar, so the size reads as "reclaimable vs. real" (mockup 1a note). *@
                    <div class="sql-bar" title="free pages, drawn inside the file">
                        <span style="width: @Pct(100 - o.FreePercent)%"></span>
                        <i style="width: @Pct(o.FreePercent)%"></i>
                    </div>
                </ChildContent>
                <Caption>
                    <span class="sqlite-overview-free">
                        @if (o.FreelistCount > 0)
                        {
                            <text>@o.FreePercent.ToString("0", Inv)% free pages (@SqliteFormat.Bytes(o.FreeBytes)) — <span class="sql-mono">VACUUM</span> would reclaim them</text>
                        }
                        else
                        {
                            <text>no free pages</text>
                        }
                    </span>
                    · last modified @o.LastModified.ToString("yyyy-MM-dd HH:mm:ss", Inv) UTC
                </Caption>
            </SqliteStatTile>

            <SqliteStatTile Label="Journal" Class="sqlite-overview-journal" ValueClass="sqlite-overview-journal-mode">
                <Value>@o.JournalMode</Value>
                <Caption>
                    synchronous <span class="sql-mono">@o.Synchronous</span>@(o.JournalMode == "WAL" ? $" · checkpoint every {SqliteFormat.Count(o.WalAutoCheckpoint)} pages" : "") · per-connection
                </Caption>
            </SqliteStatTile>

            <SqliteStatTile Label="Page cache" Class="sqlite-overview-cache" ValueClass="sqlite-overview-cache-pages">
                <Value>@SqliteFormat.Count(o.CachePages) p</Value>
                <Caption>@SqliteFormat.Bytes(o.CacheBytes) · per-connection default</Caption>
            </SqliteStatTile>
        </div>

        <div class="sql-body">
            @* Integrity is a status strip, not a tile: it is either ok or the only thing that matters (mockup 1a note). *@
            <div class="sql-strip sqlite-overview-integrity @IntegrityStripClass">
                <span class="sql-mono">PRAGMA integrity_check</span>
                <span class="sql-muted">→</span>
                @if (_integrityRunning)
                {
                    <span class="sql-muted sqlite-overview-integrity-status">running…</span>
                }
                else if (_integrity is null)
                {
                    <span class="sql-muted sqlite-overview-integrity-status">never run</span>
                }
                else if (_integrity.IntegrityOk)
                {
                    <span class="sql-ok sqlite-overview-integrity-status">ok</span>
                }
                else
                {
                    <span class="sql-danger sqlite-overview-integrity-status">@ErrorCount(_integrity)</span>
                }
                @if (_integrity is { } last && !_integrityRunning)
                {
                    <span class="sql-muted sqlite-overview-integrity-meta">· ran @last.RanAt.ToString("yyyy-MM-dd HH:mm", Inv) UTC in @SqliteFormat.Duration(last.Duration) · foreign_key_check @last.ForeignKeyViolations violations</span>
                }
                <span class="sql-spacer"></span>
                <button type="button" class="sql-btn sqlite-overview-run-integrity" disabled="@_integrityRunning" @onclick="RunIntegrityAsync">Run now</button>
            </div>

            @if (_integrity is { IntegrityOk: false } failed && !_integrityRunning)
            {
                <pre class="sql-error-list sqlite-overview-integrity-errors">@string.Join("\n", failed.Messages)</pre>
                <div class="sql-footnote">
                    Treat this file as suspect and copy it before changing anything. <span class="sql-mono">REINDEX</span> can fix
                    index-only errors; page errors need <span class="sql-mono">.recover</span> — both arrive with write mode.
                </div>
            }

            @if (_integrityError is not null)
            {
                <MudAlert Severity="Severity.Warning" Dense="true" Class="sqlite-overview-integrity-error">@_integrityError</MudAlert>
            }

            <div class="sql-cards">
                <div class="sql-card sqlite-overview-sizes">
                    <div class="sql-card-head">
                        <span class="sql-card-title">@(_sizes is { SizesAvailable: false } ? "Largest tables" : "Size by table")</span>
                        <span class="sql-spacer"></span>
                        @if (_sizes is { } head)
                        {
                            <span class="sql-card-sub">@(head.SizesAvailable ? "dbstat" : "sqlite_stat1") · @head.TableCount tables, @head.IndexCount indexes</span>
                        }
                    </div>
                    @if (_sizesError is not null)
                    {
                        <MudAlert Severity="Severity.Warning" Dense="true" Class="sqlite-overview-sizes-error">@_sizesError</MudAlert>
                    }
                    else if (_sizes is null)
                    {
                        <div class="sql-footnote">Reading sizes…</div>
                    }
                    else if (_sizes.Entries.Count == 0)
                    {
                        <div class="sql-footnote">No tables yet.</div>
                    }
                    else
                    {
                        var max = BarMax(_sizes);
                        foreach (var entry in _sizes.Entries.Take(TopRows))
                        {
                            <div class="sql-size-row sqlite-overview-size-row">
                                <span class="sql-mono sql-size-name">@entry.Name</span>
                                <div class="sql-bar sql-bar-thin"><span style="width: @Pct(Share(_sizes, entry, max))%"></span></div>
                                @if (_sizes.SizesAvailable)
                                {
                                    <span class="sql-num sql-size-bytes">@(entry.Bytes is { } bytes ? SqliteFormat.Bytes(bytes) : "—")</span>
                                }
                                <span class="sql-num sql-muted sql-size-rows">@RowsLabel(entry)</span>
                            </div>
                        }
                        var rest = _sizes.Entries.Skip(TopRows).ToList();
                        if (rest.Count > 0)
                        {
                            <div class="sql-size-row sql-muted sqlite-overview-size-more">
                                <span class="sql-size-name">@rest.Count more</span>
                                @if (_sizes.SizesAvailable)
                                {
                                    <span class="sql-num sql-size-bytes">@SqliteFormat.Bytes(rest.Sum(e => e.Bytes ?? 0))</span>
                                }
                            </div>
                        }
                        if (!_sizes.SizesAvailable)
                        {
                            <div class="sql-footnote sqlite-overview-no-dbstat">
                                Sizes need <span class="sql-mono">dbstat</span>, which this SQLite build doesn't include — tables are ranked by
                                <span class="sql-mono">sqlite_stat1</span> row estimates (≈). Run <span class="sql-mono">ANALYZE</span> to refresh them.
                            </div>
                        }
                    }
                </div>

                <div class="sql-card sqlite-overview-settings">
                    <div class="sql-card-head"><span class="sql-card-title">This console's connection</span></div>
                    <dl class="sql-kv">
                        <dt>busy_timeout</dt>
                        <dd class="sql-mono sqlite-overview-busy">@SqliteFormat.Count(o.BusyTimeoutMs) ms</dd>
                        <dt>foreign_keys</dt>
                        <dd>per-connection · off by default</dd>
                        <dt>mode</dt>
                        <dd>read-only (<span class="sql-mono">mode=ro</span>)</dd>
                    </dl>
                </div>
            </div>
        </div>
    }
</div>

@code {
    private const int TopRows = 4;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly CancellationTokenSource _cts = new();
    private ConnectionInfo? _connection;
    private SqliteOverview? _overview;
    private TableSizes? _sizes;
    private string? _sizesError;
    private IntegrityResult? _integrity;
    private string? _integrityError;
    private bool _integrityRunning;
    private string? _error;
    private bool _loading;
    private DateTimeOffset? _loadedAt;

    private string? IntegrityStripClass => _integrity switch
    {
        null => null,
        { IntegrityOk: true } => "sql-strip-ok",
        _ => "sql-strip-danger",
    };

    private async Task OnSelectionChangedAsync(ConnectionInfo connection)
    {
        _connection = connection;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _loading = true;
        _error = null;
        _sizes = null;
        _sizesError = null;
        _integrityError = null;
        try
        {
            var overview = await GetOverview.HandleAsync(connection.Id, _cts.Token);
            var integrity = await GetLastIntegrity.HandleAsync(connection.Id, _cts.Token);
            if (!ReferenceEquals(connection, _connection))
            {
                return;
            }

            _integrity = integrity;
            if (!overview.IsSuccess)
            {
                _overview = null;
                _loadedAt = null;
                _error = overview.Error;
                return;
            }

            _overview = overview.Value;
            _loadedAt = Clock.GetUtcNow();
            _loading = false;
            StateHasChanged();

            // Sizes load separately so the tiles never wait on them (spec §5).
            var sizes = await GetTableSizes.HandleAsync(connection.Id, _cts.Token);
            if (!ReferenceEquals(connection, _connection))
            {
                return;
            }

            if (sizes.IsSuccess)
            {
                _sizes = sizes.Value;
            }
            else
            {
                _sizesError = sizes.Error;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Page disposed mid-load.
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RunIntegrityAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _integrityRunning = true;
        _integrityError = null;
        try
        {
            var result = await RunIntegrityCheck.HandleAsync(new RunIntegrityCheckCommand(connection.Id, connection.Name), _cts.Token);
            if (!ReferenceEquals(connection, _connection))
            {
                return;
            }

            if (result.IsSuccess)
            {
                _integrity = result.Value;
            }
            else
            {
                _integrityError = result.Error;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Page disposed mid-check.
        }
        finally
        {
            _integrityRunning = false;
        }
    }

    private static string ErrorCount(IntegrityResult result) =>
        result.Messages.Count == 1 ? "1 error" : $"{result.Messages.Count} errors";

    private static string Pct(double value) => Math.Clamp(value, 0, 100).ToString("0.#", Inv);

    private static double BarMax(TableSizes sizes) =>
        Math.Max(1, sizes.Entries.Max(e => (double)(sizes.SizesAvailable ? e.Bytes ?? 0 : e.EstimatedRows ?? 0)));

    private static double Share(TableSizes sizes, TableSizeEntry entry, double max) =>
        100.0 * (sizes.SizesAvailable ? entry.Bytes ?? 0 : entry.EstimatedRows ?? 0) / max;

    private static string RowsLabel(TableSizeEntry entry) => entry.Type == SchemaObjectType.Index
        ? "index"
        : entry.EstimatedRows is { } rows ? $"{SqliteFormat.Approx(rows)} rows ≈" : "rows —";

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~OverviewPageTests"`
Expected: PASS.

- [ ] **Step 5: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): Overview page — file, journal, cache, integrity strip, largest tables"
```

---
### Task 13: Tables page (mockup 1b)

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Sqlite/Pages/Tables.razor`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/Pages/TablesPageTests.cs`

**Interfaces:**
- Consumes: `ListSchemaQueryHandler`, `GetTableDetailQueryHandler` (Task 7); `GetColumnSamplesQueryHandler`, `PreviewRowsQueryHandler`, `CountRowsQueryHandler`, `ColumnNoteBuilder` (Task 8); `ExportTableCsvCommandHandler`, `CsvExport` (Task 9); chrome + `sqlite.js` (Task 11).
- Produces: routable page `/p/sqlite/tables` (`SbConsole.Plugins.Sqlite.Pages.Tables`), honoring `?table=`.

- [ ] **Step 1: Write the failing page tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/Pages/TablesPageTests.cs`:

```csharp
using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// Page type fully qualified for the same namespace-collision reason as OverviewPageTests.
public class TablesPageTests : SqlitePageTestBase
{
    private static readonly SchemaListing Listing = new(
    [
        new SchemaObject("events", SchemaObjectType.Table, "events", 41_200_000, null),
        new SchemaObject("idx_events_tenant_ts", SchemaObjectType.Index, "events", null, null),
        new SchemaObject("sessions", SchemaObjectType.Table, "sessions", 2_418_902, null),
        new SchemaObject("tenants", SchemaObjectType.Table, "tenants", null, null),
        new SchemaObject("trg_events_ai", SchemaObjectType.Trigger, "events", null, null),
        new SchemaObject("v_daily_active", SchemaObjectType.View, "v_daily_active", null, null),
    ], SizesAvailable: false);

    private static readonly TableDetail Events = new(
        "events", SchemaObjectType.Table, "CREATE TABLE events(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id))",
        false, 41_200_000,
        [
            new ColumnInfo("id", "INTEGER", false, null, 1, false, false),
            new ColumnInfo("session_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("tenant_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("props", "TEXT", false, null, 0, false, false),
            new ColumnInfo("ts", "INTEGER", true, null, 0, false, false),
        ],
        [new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"])],
        [new ForeignKeyInfo(0, "session_id", "sessions", "id", "NO ACTION", "CASCADE")]);

    private static readonly TableDetail Sessions = new(
        "sessions", SchemaObjectType.Table, "CREATE TABLE sessions(id INTEGER PRIMARY KEY)", false, 2_418_902,
        [new ColumnInfo("id", "INTEGER", false, null, 1, false, false)], [], []);

    private static readonly RowPage Preview = new(
        "SELECT * FROM \"events\" ORDER BY rowid DESC LIMIT 50",
        ["id", "session_id", "tenant_id", "props", "ts"],
        [
            [new CellValue(CellKind.Integer, "2", 2), new CellValue(CellKind.Integer, "2418902", 2418902), new CellValue(CellKind.Integer, "88", 88),
             new CellValue(CellKind.Text, "{\"reportId\":9106}"), new CellValue(CellKind.Integer, "1759052468112", 1759052468112)],
            [new CellValue(CellKind.Integer, "1", 1), new CellValue(CellKind.Integer, "2418902", 2418902), new CellValue(CellKind.Integer, "12", 12),
             new CellValue(CellKind.Null, "NULL"), new CellValue(CellKind.Integer, "1759052455201", 1759052455201)],
        ]);

    public TablesPageTests()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(Listing);
        Operations.GetTableDetailAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(Events);
        Operations.GetTableDetailAsync(Secret, "sessions", Arg.Any<CancellationToken>()).Returns(Sessions);
        Operations.SampleColumnsAsync(Secret, "events", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>()).Returns(
            [new ColumnSample("props", 4, 4, 4, 4, 4, 0, 0), new ColumnSample("ts", 4, 4, 4, 0, 0, 4, 4)]);
        Operations.SampleColumnsAsync(Secret, "sessions", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ColumnSample>());
        Operations.PreviewRowsAsync(Secret, "events", RowPage.PreviewLimit, Arg.Any<CancellationToken>()).Returns(Preview);
        Operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(41_204_117L);
    }

    private async Task<IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Tables>> RenderPageAsync()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Tables>();
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Lists_tables_then_views_with_estimates_and_a_schema_summary()
    {
        var cut = await RenderPageAsync();

        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name"))
            .Should().Equal("events", "sessions", "tenants", "v_daily_active");
        cut.Find("[data-name='events']").TextContent.Should().Contain("41.2M ≈");
        cut.Find("[data-name='tenants']").TextContent.Should().Contain("—");
        cut.Find(".sqlite-tables-summary").TextContent.Should().Be("3 tables · 1 indexes · 1 views · 1 triggers · from sqlite_schema");
    }

    [Fact]
    public async Task Opens_the_first_table_with_columns_badges_and_notes()
    {
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-table-name").TextContent.Should().Be("events");
        cut.Find(".sqlite-table-rows").TextContent.Should().Be("41.2M rows ≈");
        cut.FindAll(".sqlite-column-row").Should().HaveCount(5);
        cut.Find("[data-column='id'] .sqlite-column-notes").TextContent.Should().Be("alias of rowid");
        cut.Find("[data-column='session_id'] .sqlite-column-notes").TextContent.Should().Be("→ sessions.id · ON DELETE CASCADE");
        cut.Find("[data-column='props'] .sqlite-column-notes").TextContent.Should().Be("JSON in 100% of sampled rows");
        cut.Find("[data-column='id'] .sql-badge-pk").TextContent.Should().Be("PK");
        cut.Find("[data-column='session_id'] .sql-badge-fk").TextContent.Should().Be("FK");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("table=events");
    }

    [Fact]
    public async Task Honors_the_table_in_the_query_string()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/p/sqlite/tables?table=sessions");

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions");
    }

    [Fact]
    public async Task The_filter_narrows_the_list()
    {
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-tables-filter").Input("sess");

        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name")).Should().Equal("sessions");
    }

    [Fact]
    public async Task Clicking_a_table_opens_it()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-name='sessions']").Click();
        await SettleAsync(cut);

        cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions");
        cut.Find("[data-name='sessions']").ClassList.Should().Contain("is-selected");
    }

    [Fact]
    public async Task Count_exactly_replaces_the_estimate()
    {
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-table-count").Click();
        await SettleAsync(cut);

        cut.Find(".sqlite-table-rows").TextContent.Should().Be("41,204,117 rows (exact)");
        cut.FindAll(".sqlite-table-count").Should().BeEmpty();
    }

    [Fact]
    public async Task The_data_tab_shows_the_sql_cells_and_unix_ms_date_hints()
    {
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-tab-data").Click();
        await SettleAsync(cut);

        cut.Find(".sqlite-preview-sql").TextContent.Should().Be(Preview.Sql);
        var rows = cut.FindAll(".sqlite-preview tbody tr");
        rows.Should().HaveCount(2);
        rows[1].QuerySelectorAll("td")[3].ClassList.Should().Contain("sql-cell-null");
        rows[0].QuerySelectorAll("td")[4].TextContent.Should().Contain("1759052468112").And.Contain("2025-09-28");
        rows[0].QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("88", "tenant_id has no unix-ms note, so no hint");
    }

    [Fact]
    public async Task Indexes_foreign_keys_and_ddl_tabs_show_the_schema()
    {
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-tab-indexes").Click();
        cut.Find(".sqlite-indexes").TextContent.Should().Contain("idx_events_tenant_ts").And.Contain("tenant_id, ts");

        cut.Find(".sqlite-tab-foreignkeys").Click();
        cut.Find(".sqlite-foreign-keys").TextContent.Should().Contain("sessions.id").And.Contain("CASCADE");

        cut.Find(".sqlite-tab-ddl").Click();
        cut.Find(".sqlite-ddl").TextContent.Should().StartWith("CREATE TABLE events(");
    }

    [Fact]
    public async Task Export_downloads_through_the_js_module_and_is_audited()
    {
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("events.csv", Encoding.UTF8.GetBytes("id\r\n1\r\n2\r\n"), 2, false));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-table-export").Click();
        await SettleAsync(cut);

        module.VerifyInvoke("downloadFromStream").Arguments[0].Should().Be("events.csv");
        cut.Find(".sqlite-table-export-message").TextContent.Should().Be("Exported 2 rows to events.csv.");
        await Audit.Received(1).RecordAsync("sqlite.table.export", $"{ConnectionName}/events", ActionRisk.Safe, true,
            "2 rows", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_file_shows_the_empty_state()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(new SchemaListing([], false));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-tables-empty").TextContent.Should().Contain("No tables in").And.Contain("reports.db");
    }

    [Fact]
    public async Task A_listing_failure_shows_the_friendly_error()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SchemaListing>>(_ => throw new SqliteException("file is not a database", 26));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-tables-error").TextContent.Should().Contain("The file is not a valid or healthy SQLite database.");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~TablesPageTests"`
Expected: build FAILS — `SbConsole.Plugins.Sqlite.Pages.Tables` doesn't exist.

- [ ] **Step 3: Implement the page**

Create `src/Plugins/SbConsole.Plugins.Sqlite/Pages/Tables.razor`:

```razor
@* src/Plugins/SbConsole.Plugins.Sqlite/Pages/Tables.razor -- mockup 1b, design spec §6. Read-only:
   Rename / Drop / Create table / "Open in Query" arrive with phase 2's write mode. *@
@page "/p/sqlite/tables"
@using System.Globalization
@using Microsoft.AspNetCore.WebUtilities
@using SbConsole.Plugins.Sqlite.Client
@using SbConsole.Plugins.Sqlite.Tables
@inject ListSchemaQueryHandler ListSchema
@inject GetTableDetailQueryHandler GetDetail
@inject GetColumnSamplesQueryHandler GetSamples
@inject PreviewRowsQueryHandler PreviewRows
@inject CountRowsQueryHandler CountRows
@inject ExportTableCsvCommandHandler ExportCsv
@inject NavigationManager Navigation
@inject IJSRuntime JS
@implements IAsyncDisposable

<PageTitle>SQLite tables</PageTitle>

<div class="sql-page">
    <SqlitePageHeader Title="Tables">
        <Chips>
            <SqliteConnectionPicker Loading="_loading" SelectionChanged="OnSelectionChangedAsync" />
        </Chips>
    </SqlitePageHeader>

    @if (_error is not null)
    {
        <div class="sql-body">
            <div class="sql-banner sql-banner-danger sqlite-tables-error">
                <span>@_error</span>
                <span class="sql-spacer"></span>
                <a class="sql-link" href="/connections">Connection settings</a>
                <button type="button" class="sql-btn" @onclick="LoadListingAsync">Retry</button>
            </div>
        </div>
    }
    else if (_listing is { } listing)
    {
        if (listing.Count(SchemaObjectType.Table) == 0 && listing.Count(SchemaObjectType.View) == 0)
        {
            <div class="sql-empty sqlite-tables-empty">
                <div class="sql-empty-title">No tables in <span class="sql-mono">@FileName</span></div>
                <div class="sql-muted">The file has no schema yet. Tables show up here once a migration or an application creates them.</div>
            </div>
        }
        else
        {
            <div class="sql-split">
                <aside class="sql-list sqlite-tables-list">
                    <input class="sql-filter sqlite-tables-filter" placeholder="Filter tables…" value="@_filter" @oninput="OnFilter" />

                    <div class="sql-list-group">Tables</div>
                    @foreach (var item in Filtered(listing.Tables))
                    {
                        <button type="button" class="sql-list-row sqlite-tables-row @(item.Name == _selected ? "is-selected" : null)"
                                data-name="@item.Name" @onclick="() => SelectAsync(item.Name)">
                            <span class="sql-mono sql-list-name">@item.Name</span>
                            @if (listing.SizesAvailable)
                            {
                                <span class="sql-num sql-muted">@(item.Bytes is { } bytes ? SqliteFormat.Bytes(bytes) : "")</span>
                            }
                            <span class="sql-num sql-muted">@RowsLabel(item)</span>
                        </button>
                    }

                    @if (listing.Count(SchemaObjectType.View) > 0)
                    {
                        <div class="sql-list-group">Views</div>
                        foreach (var item in Filtered(listing.Views))
                        {
                            <button type="button" class="sql-list-row sqlite-tables-row @(item.Name == _selected ? "is-selected" : null)"
                                    data-name="@item.Name" @onclick="() => SelectAsync(item.Name)">
                                <span class="sql-mono sql-list-name">@item.Name</span>
                                <span class="sql-num sql-muted">view</span>
                            </button>
                        }
                    }

                    <div class="sql-footnote sqlite-tables-summary">@Summary(listing)</div>
                    <div class="sql-footnote">
                        ≈ rows come from <span class="sql-mono">sqlite_stat1</span> — an exact <span class="sql-mono">COUNT(*)</span>
                        on a large table takes seconds, so it's a click, not a default.
                    </div>
                </aside>

                <section class="sql-detail sqlite-table-detail">
                    @if (_detailError is not null)
                    {
                        <MudAlert Severity="Severity.Warning" Class="sqlite-table-detail-error">@_detailError</MudAlert>
                    }
                    else if (_detail is { } d)
                    {
                        <div class="sql-detail-head">
                            <span class="sql-overline">@(d.Type == SchemaObjectType.View ? "view" : "table")</span>
                            <span class="sql-detail-title sql-mono sqlite-table-name">@d.Name</span>
                            <span class="sql-spacer"></span>
                            <button type="button" class="sql-btn sqlite-table-export" disabled="@_exporting" @onclick="ExportAsync">
                                @(_exporting ? "Exporting…" : "Export CSV")
                            </button>
                        </div>
                        <div class="sql-detail-meta">
                            @d.Columns.Count columns · @d.Indexes.Count indexes
                            @if (d.Type == SchemaObjectType.Table)
                            {
                                <text> · WITHOUT ROWID @(d.WithoutRowId ? "yes" : "no")</text>
                            }
                            · <span class="sqlite-table-rows">@RowsText(d)</span>
                            @if (_counting)
                            {
                                <button type="button" class="sql-link sqlite-table-count-cancel" @onclick="CancelCount">cancel</button>
                            }
                            else if (_exactRows is null)
                            {
                                <button type="button" class="sql-link sqlite-table-count" @onclick="CountAsync">count exactly</button>
                            }
                        </div>
                        if (_countError is not null)
                        {
                            <MudAlert Severity="Severity.Warning" Dense="true" Class="sqlite-table-count-error">@_countError</MudAlert>
                        }
                        if (_exportMessage is not null)
                        {
                            <div class="sql-footnote sqlite-table-export-message">@_exportMessage</div>
                        }

                        <div class="sql-tabs">
                            @foreach (var tab in Enum.GetValues<DetailTab>())
                            {
                                <button type="button" class="sql-tab sqlite-tab-@(tab.ToString().ToLowerInvariant()) @(tab == _tab ? "is-active" : null)"
                                        @onclick="() => SelectTabAsync(tab)">@TabLabel(tab, d)</button>
                            }
                        </div>

                        switch (_tab)
                        {
                            case DetailTab.Columns:
                                <table class="sql-table sqlite-columns">
                                    <thead><tr><th></th><th>Column</th><th>Type</th><th>Null</th><th>Default</th><th>Notes</th></tr></thead>
                                    <tbody>
                                        @foreach (var column in d.Columns)
                                        {
                                            <tr class="sqlite-column-row" data-column="@column.Name">
                                                <td>
                                                    @if (column.PrimaryKeyOrdinal > 0)
                                                    {
                                                        <span class="sql-badge sql-badge-pk">PK</span>
                                                    }
                                                    @if (d.ForeignKeys.Any(fk => fk.FromColumn == column.Name))
                                                    {
                                                        <span class="sql-badge sql-badge-fk">FK</span>
                                                    }
                                                </td>
                                                <td class="sql-mono">@column.Name</td>
                                                <td class="sql-mono sql-muted">@(string.IsNullOrEmpty(column.DeclaredType) ? "—" : column.DeclaredType)</td>
                                                <td>@(column.NotNull ? "no" : "yes")</td>
                                                <td class="sql-mono">@(column.DefaultValue ?? "—")</td>
                                                <td class="sql-muted sqlite-column-notes">@NotesText(column.Name)</td>
                                            </tr>
                                        }
                                    </tbody>
                                </table>
                                if (_samplesLoading)
                                {
                                    <div class="sql-footnote sqlite-notes-loading">Sampling the first @SqliteFormat.Count(ColumnSample.DefaultSampleRows) rows for notes…</div>
                                }
                                if (_samplesError is not null)
                                {
                                    <div class="sql-footnote sqlite-notes-error">Sampled notes unavailable: @_samplesError</div>
                                }
                                break;

                            case DetailTab.Indexes:
                                <table class="sql-table sqlite-indexes">
                                    <thead><tr><th>Index</th><th>Columns</th><th>Unique</th><th>Origin</th><th>Partial</th></tr></thead>
                                    <tbody>
                                        @foreach (var index in d.Indexes)
                                        {
                                            <tr>
                                                <td class="sql-mono">@index.Name</td>
                                                <td class="sql-mono">@string.Join(", ", index.Columns)</td>
                                                <td>@(index.Unique ? "yes" : "no")</td>
                                                <td>@OriginLabel(index.Origin)</td>
                                                <td>@(index.Partial ? "yes" : "no")</td>
                                            </tr>
                                        }
                                    </tbody>
                                </table>
                                break;

                            case DetailTab.ForeignKeys:
                                <table class="sql-table sqlite-foreign-keys">
                                    <thead><tr><th>Column</th><th>References</th><th>On update</th><th>On delete</th></tr></thead>
                                    <tbody>
                                        @foreach (var fk in d.ForeignKeys)
                                        {
                                            <tr>
                                                <td class="sql-mono">@fk.FromColumn</td>
                                                <td class="sql-mono">@fk.ToTable.@(fk.ToColumn ?? "rowid")</td>
                                                <td>@fk.OnUpdate</td>
                                                <td>@fk.OnDelete</td>
                                            </tr>
                                        }
                                    </tbody>
                                </table>
                                <div class="sql-footnote">
                                    <span class="sql-mono">foreign_keys</span> is per-connection and off by default in SQLite, so these are
                                    enforced only by connections that turn it on.
                                </div>
                                break;

                            case DetailTab.Ddl:
                                <pre class="sql-ddl sqlite-ddl">@(d.Ddl ?? "—")</pre>
                                break;

                            case DetailTab.Data:
                                if (_previewError is not null)
                                {
                                    <MudAlert Severity="Severity.Warning" Dense="true" Class="sqlite-preview-error">@_previewError</MudAlert>
                                }
                                else if (_preview is not { } preview)
                                {
                                    <div class="sql-footnote">Loading rows…</div>
                                }
                                else
                                {
                                    <div class="sql-caption">Data preview · <span class="sql-mono sqlite-preview-sql">@preview.Sql</span></div>
                                    <div class="sql-grid-wrap">
                                        <table class="sql-table sql-grid sqlite-preview">
                                            <thead>
                                                <tr>
                                                    @foreach (var name in preview.Columns)
                                                    {
                                                        <th class="sql-mono">@name</th>
                                                    }
                                                </tr>
                                            </thead>
                                            <tbody>
                                                @foreach (var row in preview.Rows)
                                                {
                                                    <tr>
                                                        @for (var i = 0; i < row.Count; i++)
                                                        {
                                                            var cell = row[i];
                                                            var hint = DateHint(preview.Columns[i], cell);
                                                            <td class="sql-mono sql-cell-@(cell.Kind.ToString().ToLowerInvariant())"
                                                                title="@(cell.Truncated ? "truncated to 200 characters" : null)">
                                                                @cell.Display
                                                                @if (hint is not null)
                                                                {
                                                                    <small class="sql-muted sql-hint">@hint</small>
                                                                }
                                                            </td>
                                                        }
                                                    </tr>
                                                }
                                            </tbody>
                                        </table>
                                    </div>
                                    if (preview.Rows.Count == 0)
                                    {
                                        <div class="sql-footnote">No rows.</div>
                                    }
                                    <div class="sql-footnote">Integer columns that look like unix milliseconds get a date hint — the value is never changed.</div>
                                }
                                break;
                        }
                    }
                    else if (_selected is not null)
                    {
                        <div class="sql-footnote">Loading…</div>
                    }
                </section>
            </div>
        }
    }
</div>

@code {
    private enum DetailTab
    {
        Columns,
        Indexes,
        ForeignKeys,
        Ddl,
        Data,
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly CancellationTokenSource _pageCts = new();
    private CancellationTokenSource _tableCts = new();
    private CancellationTokenSource? _countCts;
    private IJSObjectReference? _module;

    private ConnectionInfo? _connection;
    private SchemaListing? _listing;
    private string? _error;
    private bool _loading;
    private string _filter = "";

    private string? _selected;
    private TableDetail? _detail;
    private string? _detailError;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _notes;
    private bool _samplesLoading;
    private string? _samplesError;
    private RowPage? _preview;
    private string? _previewError;
    private bool _previewLoading;
    private long? _exactRows;
    private bool _counting;
    private string? _countError;
    private bool _exporting;
    private string? _exportMessage;
    private DetailTab _tab = DetailTab.Columns;

    private string FileName => _connection?.Summary.GetValueOrDefault("File") ?? "this file";

    private async Task OnSelectionChangedAsync(ConnectionInfo connection)
    {
        _connection = connection;
        await LoadListingAsync();
    }

    private async Task LoadListingAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        ResetTable();
        _selected = null;
        _loading = true;
        _error = null;
        PluginResult<SchemaListing> result;
        try
        {
            result = await ListSchema.HandleAsync(connection.Id, _pageCts.Token);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _loading = false;
        }

        if (!ReferenceEquals(connection, _connection))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            _listing = null;
            _error = result.Error;
            return;
        }

        _listing = result.Value!;
        var requested = QueryTable();
        var first = _listing.Objects.FirstOrDefault(o => o.Name == requested && o.Type is SchemaObjectType.Table or SchemaObjectType.View)
            ?? _listing.Tables.FirstOrDefault()
            ?? _listing.Views.FirstOrDefault();
        if (first is not null)
        {
            await SelectAsync(first.Name);
        }
    }

    private string? QueryTable()
    {
        var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
        return query.TryGetValue("table", out var value) ? value.ToString() : null;
    }

    /// <summary>Cancels everything in flight for the previous table and clears its state.</summary>
    private void ResetTable()
    {
        _tableCts.Cancel();
        _tableCts.Dispose();
        _tableCts = new CancellationTokenSource();
        _detail = null;
        _detailError = null;
        _notes = null;
        _samplesLoading = false;
        _samplesError = null;
        _preview = null;
        _previewError = null;
        _previewLoading = false;
        _exactRows = null;
        _counting = false;
        _countError = null;
        _exportMessage = null;
        _tab = DetailTab.Columns;
    }

    private async Task SelectAsync(string name)
    {
        if (_connection is not { } connection)
        {
            return;
        }

        ResetTable();
        _selected = name;
        var uri = Navigation.GetUriWithQueryParameter("table", name);
        if (uri != Navigation.Uri)
        {
            Navigation.NavigateTo(uri, replace: true);
        }

        var token = _tableCts.Token;
        try
        {
            var detail = await GetDetail.HandleAsync(connection.Id, name, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!detail.IsSuccess)
            {
                _detailError = detail.Error;
                return;
            }

            _detail = detail.Value!;
            _notes = ColumnNoteBuilder.Build(_detail, null); // schema notes now, sampled notes when they arrive
            _samplesLoading = true;
            StateHasChanged();

            var samples = await GetSamples.HandleAsync(connection.Id, name, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _samplesLoading = false;
            if (samples.IsSuccess)
            {
                _notes = ColumnNoteBuilder.Build(_detail, samples.Value);
            }
            else
            {
                _samplesError = samples.Error;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Another table was selected, or the page went away.
        }
    }

    private async Task SelectTabAsync(DetailTab tab)
    {
        _tab = tab;
        if (tab != DetailTab.Data || _preview is not null || _previewLoading || _connection is not { } connection || _detail is not { } detail)
        {
            return;
        }

        _previewLoading = true;
        _previewError = null;
        var token = _tableCts.Token;
        try
        {
            var page = await PreviewRows.HandleAsync(connection.Id, detail.Name, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (page.IsSuccess)
            {
                _preview = page.Value;
            }
            else
            {
                _previewError = page.Error;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _previewLoading = false;
            }
        }
    }

    private async Task CountAsync()
    {
        if (_connection is not { } connection || _detail is not { } detail)
        {
            return;
        }

        var tableToken = _tableCts.Token;
        _countCts = CancellationTokenSource.CreateLinkedTokenSource(tableToken);
        var token = _countCts.Token;
        _counting = true;
        _countError = null;
        try
        {
            var result = await CountRows.HandleAsync(connection.Id, detail.Name, token);
            if (tableToken.IsCancellationRequested)
            {
                return;
            }

            if (result.IsSuccess)
            {
                _exactRows = result.Value;
            }
            else
            {
                _countError = result.Error;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancelled from the "cancel" link, or the table changed.
        }
        finally
        {
            if (!tableToken.IsCancellationRequested)
            {
                _counting = false;
            }

            _countCts?.Dispose();
            _countCts = null;
        }
    }

    private void CancelCount() => _countCts?.Cancel();

    private async Task ExportAsync()
    {
        if (_connection is not { } connection || _detail is not { } detail)
        {
            return;
        }

        _exporting = true;
        _exportMessage = null;
        var token = _tableCts.Token;
        try
        {
            var result = await ExportCsv.HandleAsync(new ExportTableCsvCommand(connection.Id, connection.Name, detail.Name), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                _exportMessage = result.Error;
                return;
            }

            var export = result.Value!;
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/SbConsole.Plugins.Sqlite/sqlite.js");
            using var stream = new MemoryStream(export.Content);
            using var streamRef = new DotNetStreamReference(stream);
            await _module.InvokeVoidAsync("downloadFromStream", export.FileName, streamRef);
            _exportMessage = export.Truncated
                ? $"Exported the first {SqliteFormat.Count(CsvExport.MaxRows)} rows to {export.FileName} — the table has more."
                : $"Exported {SqliteFormat.Count(export.RowCount)} rows to {export.FileName}.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (JSException)
        {
            _exportMessage = "The browser couldn't save the file.";
        }
        finally
        {
            _exporting = false;
        }
    }

    private void OnFilter(ChangeEventArgs e) => _filter = e.Value?.ToString() ?? "";

    private IEnumerable<SchemaObject> Filtered(IEnumerable<SchemaObject> items) =>
        string.IsNullOrWhiteSpace(_filter)
            ? items
            : items.Where(i => i.Name.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string RowsLabel(SchemaObject item) =>
        item.EstimatedRows is { } rows ? $"{SqliteFormat.Approx(rows)} ≈" : "—";

    private static string Summary(SchemaListing listing) =>
        $"{listing.Count(SchemaObjectType.Table)} tables · {listing.Count(SchemaObjectType.Index)} indexes · " +
        $"{listing.Count(SchemaObjectType.View)} views · {listing.Count(SchemaObjectType.Trigger)} triggers · from sqlite_schema";

    private string RowsText(TableDetail detail) => _counting
        ? "counting…"
        : _exactRows is { } exact
            ? $"{SqliteFormat.Count(exact)} rows (exact)"
            : detail.EstimatedRows is { } estimate ? $"{SqliteFormat.Approx(estimate)} rows ≈" : "rows —";

    private static string TabLabel(DetailTab tab, TableDetail detail) => tab switch
    {
        DetailTab.Indexes => $"Indexes {detail.Indexes.Count}",
        DetailTab.ForeignKeys => $"Foreign keys {detail.ForeignKeys.Count}",
        DetailTab.Ddl => "DDL",
        _ => tab.ToString(),
    };

    private static string OriginLabel(string origin) => origin switch
    {
        "c" => "CREATE INDEX",
        "u" => "UNIQUE constraint",
        "pk" => "PRIMARY KEY",
        _ => origin,
    };

    private string NotesText(string column) =>
        _notes is not null && _notes.TryGetValue(column, out var notes) ? string.Join(" · ", notes) : "";

    private string? DateHint(string column, CellValue cell) =>
        cell is { Kind: CellKind.Integer, Integer: { } value }
        && value is >= ColumnSample.EpochMsMin and <= ColumnSample.EpochMsMax
        && _notes is not null && _notes.TryGetValue(column, out var notes) && notes.Contains(ColumnNoteBuilder.UnixMsNote)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("yyyy-MM-dd", Inv)
            : null;

    public async ValueTask DisposeAsync()
    {
        _pageCts.Cancel();
        _tableCts.Cancel();
        _countCts?.Cancel();
        _pageCts.Dispose();
        _tableCts.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone.
            }
        }
    }
}
```

Razor notes for the implementer: once inside a C# block (an `if`/`else`/`switch`/`case` body), nested statements take **no** `@` (`if`, `foreach`, `switch`) — `@` is only for statements that sit directly inside a markup element; Razor rejects `@if` in code context. Inside `switch`, each `case` body is C#, so markup lines start with `<` and `break;` ends the case; `@fk.ToTable.@(fk.ToColumn ?? "rowid")` renders `sessions.id` (the `.` between two expressions is literal text).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~TablesPageTests"`
Expected: PASS. If `@fk.ToTable.@(...)` is parsed as a member access on `ToTable`, write it as `@($"{fk.ToTable}.{fk.ToColumn ?? "rowid"}")`.

- [ ] **Step 5: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): Tables page — schema list, columns with notes, indexes, FKs, DDL, data, count, CSV"
```

---

### Task 14: Dashboard integration

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.Sqlite/SqlitePlugin.cs`
- Test: `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginDashboardTests.cs`

**Interfaces:**
- Consumes: `IntegrityResultStore`, `IntegrityResult` (Task 6); `ISqliteOperations.GetOverviewAsync` (Task 5).
- Produces: `SqlitePlugin.GetDashboardMetricsAsync` ("Databases" = 1), `SqlitePlugin.GetDashboardProblemsAsync`, `internal static SqlitePlugin.ComputeProblemsAsync(ISqliteOperations, Guid, string, IPluginStore, CancellationToken)`, `internal const double FreePagesProblemPercent = 20.0`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SbConsole.Plugins.Sqlite.Tests/SqlitePluginDashboardTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests;

public class SqlitePluginDashboardTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly InMemoryPluginStore _store = new();

    private static SqliteOverview Overview(long freelist) => new(
        "/data/reports.db", "3.53.3", 419_430_400, null, DateTimeOffset.UnixEpoch, 4096, 102_400, freelist, "WAL", "NORMAL", 1000, -2000, 5000);

    private Task<IReadOnlyList<PluginDashboardProblem>> Problems() =>
        SqlitePlugin.ComputeProblemsAsync(_operations, _connectionId, Secret, _store, CancellationToken.None);

    [Fact]
    public async Task Each_connection_counts_as_one_database()
    {
        (await new SqlitePlugin().GetDashboardMetricsAsync(Secret)).Should().Equal(new PluginDashboardMetric("Databases", 1));
    }

    [Fact]
    public async Task A_healthy_file_reports_no_problems()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 100));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(true, ["ok"], 0, DateTimeOffset.UnixEpoch, TimeSpan.Zero));

        (await Problems()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_integrity_check_is_an_error()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(false, ["a", "b"], 0,
            new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.Zero));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Error", "Integrity check failed", "2 problems found at 2026-10-13 02:00 UTC", "/p/sqlite/overview"));
    }

    [Fact]
    public async Task Foreign_key_violations_are_a_warning()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(true, ["ok"], 3, DateTimeOffset.UnixEpoch, TimeSpan.Zero));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "Foreign key violations", "3 rows violate a foreign key", "/p/sqlite/overview"));
    }

    [Fact]
    public async Task Many_free_pages_are_a_warning()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 25_600));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "25% free pages", "VACUUM would reclaim 100 MB", "/p/sqlite/overview"));
    }

    [Fact]
    public async Task An_uninitialized_plugin_reports_nothing()
    {
        (await new SqlitePlugin().GetDashboardProblemsAsync(_connectionId, Secret, _store)).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqlitePluginDashboardTests"`
Expected: build FAILS — `ComputeProblemsAsync` doesn't exist.

- [ ] **Step 3: Implement the hooks**

Add `using System.Globalization;` and `using SbConsole.Plugins.Sqlite.Overview;` to `SqlitePlugin.cs`, then add to the class:

```csharp
    internal const double FreePagesProblemPercent = 20.0;

    // One per connection, so the host's same-label summing reads as "N databases" (design spec §8).
    public Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([new PluginDashboardMetric("Databases", 1)]);

    public Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        _operations is null
            ? Task.FromResult<IReadOnlyList<PluginDashboardProblem>>([])
            : ComputeProblemsAsync(_operations, connectionId, connectionString, store, ct);

    /// <summary>
    /// Error when the last stored integrity check failed; Warning for FK violations it found, and
    /// for a file that's at least FreePagesProblemPercent free pages. An unreadable file throws --
    /// the host already reports unreachable connections on its own.
    /// </summary>
    internal static async Task<IReadOnlyList<PluginDashboardProblem>> ComputeProblemsAsync(
        ISqliteOperations operations, Guid connectionId, string secret, IPluginStore store, CancellationToken ct)
    {
        var problems = new List<PluginDashboardProblem>();
        var integrity = await IntegrityResultStore.ReadAsync(store, connectionId, ct);
        if (integrity is { IntegrityOk: false })
        {
            problems.Add(new PluginDashboardProblem("Error", "Integrity check failed",
                $"{integrity.Messages.Count} problems found at {integrity.RanAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC",
                OverviewHref));
        }

        if (integrity is { ForeignKeyViolations: > 0 })
        {
            problems.Add(new PluginDashboardProblem("Warning", "Foreign key violations",
                $"{SqliteFormat.Count(integrity.ForeignKeyViolations)} rows violate a foreign key", OverviewHref));
        }

        var overview = await operations.GetOverviewAsync(secret, ct);
        if (overview.PageCount > 0 && overview.FreePercent >= FreePagesProblemPercent)
        {
            problems.Add(new PluginDashboardProblem("Warning",
                $"{overview.FreePercent.ToString("0", CultureInfo.InvariantCulture)}% free pages",
                $"VACUUM would reclaim {SqliteFormat.Bytes(overview.FreeBytes)}", OverviewHref));
        }

        return problems;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Sqlite.Tests --filter "FullyQualifiedName~SqlitePluginDashboardTests"`
Expected: PASS.

- [ ] **Step 5: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green. If a `SbConsole.Web.Tests` dashboard/wallboard test hard-codes the set of plugin fleet cards, update its expectation to include SQLite — that is the only acceptable Web-test change here.

```bash
git add src/Plugins/SbConsole.Plugins.Sqlite tests/SbConsole.Plugins.Sqlite.Tests
git commit -m "feat(sqlite): dashboard metric and integrity / free-page problems"
```

---

### Task 15: Local dev stack and documentation

**Files:**
- Create: `docker/sqlite/seed.py`
- Modify: `docker-compose.yml`, `docker/README.md`, `.env.example`, `docs/design.md`, `CLAUDE.md`

**Interfaces:**
- Consumes: everything above (documentation only, plus a demo file the running app can open).
- Produces: `docker compose --profile sqlite --profile app up -d --build` serving a seeded `/sqlite/reports.db` with `SBC_SQLITE_ROOTS=/sqlite`; `python3 docker/sqlite/seed.py ./.sqlite-demo` for `dotnet run`.

- [ ] **Step 1: Write the seed script**

Create `docker/sqlite/seed.py`:

```python
"""Builds the SQLite plugin's demo database (docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §8).

Usage:  python3 -I docker/sqlite/seed.py [target-dir]      (default: /sqlite)

Creates <target-dir>/reports.db (~50 MB) once; a re-run leaves an existing file alone. Shape follows
mockup 1b: tenants, users, sessions, events with JSON props and unix-ms timestamps, a WITHOUT ROWID
event_props table with an FK to events, two indexes on events, two views, a trigger and a
migrations table. ANALYZE runs last so sqlite_stat1 row estimates exist, and every tenth event is
deleted first so the Overview has free pages to show. Journal mode is left as WAL.
"""
import json
import os
import random
import sqlite3
import sys

TARGET = sys.argv[1] if len(sys.argv) > 1 else "/sqlite"
PATH = os.path.join(TARGET, "reports.db")
TENANTS = ["acme-uk", "northwind", "globex", "initech", "umbrella", "hooli", "stark-eu", "wayne-uk"]
EVENT_NAMES = ["page.view", "report.viewed", "export.csv", "login", "search"]
EVENTS = 200_000
SESSIONS = 20_000
USERS = 4_000
START_MS = 1_756_684_800_000  # 2025-09-01T00:00:00Z

SCHEMA = """
CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, status TEXT NOT NULL DEFAULT 'active');
CREATE TABLE users(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL REFERENCES tenants(id),
                   email TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE sessions(id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), started_at INTEGER NOT NULL);
CREATE TABLE events(id INTEGER PRIMARY KEY,
                    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    tenant_id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    ts INTEGER NOT NULL,
                    props TEXT,
                    source TEXT NOT NULL DEFAULT 'web',
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
CREATE INDEX idx_events_ts ON events(ts);
CREATE INDEX idx_events_tenant_ts ON events(tenant_id, ts);
CREATE TABLE event_props(event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
                         key TEXT NOT NULL, value TEXT, PRIMARY KEY(event_id, key)) WITHOUT ROWID;
CREATE TABLE __migrations(version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
CREATE VIEW v_daily_active AS
  SELECT date(ts / 1000, 'unixepoch') AS day, COUNT(DISTINCT session_id) AS sessions FROM events GROUP BY day;
CREATE VIEW v_tenant_usage AS
  SELECT t.name AS tenant, COUNT(*) AS events FROM events e JOIN tenants t ON t.id = e.tenant_id GROUP BY t.id;
CREATE TRIGGER trg_tenants_status AFTER UPDATE OF status ON tenants BEGIN SELECT 1; END;
"""


def main() -> None:
    os.makedirs(TARGET, exist_ok=True)
    if os.path.exists(PATH):
        print(f"{PATH} already exists; leaving it alone.")
        return

    rnd = random.Random(42)
    db = sqlite3.connect(PATH)
    db.executescript(SCHEMA)
    db.executemany("INSERT INTO tenants(id, name, status) VALUES (?, ?, ?)",
                   [(i + 1, name, "churned" if name == "umbrella" else "active") for i, name in enumerate(TENANTS)])
    db.executemany("INSERT INTO users(id, tenant_id, email) VALUES (?, ?, ?)",
                   [(u, rnd.randint(1, len(TENANTS)), f"user{u}@example.test") for u in range(1, USERS + 1)])
    db.executemany("INSERT INTO sessions(id, user_id, started_at) VALUES (?, ?, ?)",
                   [(s, rnd.randint(1, USERS), START_MS + s * 60_000) for s in range(1, SESSIONS + 1)])

    events, props = [], []
    for e in range(1, EVENTS + 1):
        name = rnd.choice(EVENT_NAMES)
        body = None if rnd.random() < 0.01 else json.dumps({"reportId": rnd.randint(1, 9106), "ms": rnd.randint(10, 900)})
        events.append((e, rnd.randint(1, SESSIONS), rnd.randint(1, len(TENANTS)), name, START_MS + e * 6_000, body,
                       rnd.choice(["web", "web", "api", "cli"])))
        props.append((e, "path", f"/reports/{rnd.randint(1, 9106)}"))
        props.append((e, "agent", rnd.choice(["firefox", "chrome", "safari"])))
    db.executemany("INSERT INTO events(id, session_id, tenant_id, name, ts, props, source) VALUES (?, ?, ?, ?, ?, ?, ?)", events)
    db.executemany("INSERT INTO event_props(event_id, key, value) VALUES (?, ?, ?)", props)
    db.executemany("INSERT INTO __migrations(version, name, applied_at) VALUES (?, ?, ?)",
                   [(v, f"migration_{v:04d}", "2026-09-25T14:22:00Z") for v in range(1, 39)])
    db.commit()

    # Cascade the event_props rows too, so the demo file has no FK violations of its own.
    db.execute("PRAGMA foreign_keys = ON")
    db.execute("DELETE FROM events WHERE id % 10 = 0")
    db.commit()
    db.execute("ANALYZE")
    db.execute("PRAGMA journal_mode = WAL")
    db.commit()
    db.close()

    # The app container runs as a non-root user and SQLite needs to create -shm next to a WAL file
    # even for a read-only connection.
    os.chmod(TARGET, 0o777)
    os.chmod(PATH, 0o666)
    print(f"Wrote {PATH} ({os.path.getsize(PATH) / 1024 / 1024:.0f} MB).")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Run the seed locally to check it**

Run: `python3 -I docker/sqlite/seed.py ./.sqlite-demo`
Expected: `Wrote ./.sqlite-demo/reports.db (NN MB).` with NN roughly 30–60. Running it again prints `already exists; leaving it alone.` (The file is ignored by the existing `*.db` rule in `.gitignore`; add `.sqlite-demo/` to `.gitignore` under `# SQLite` so the folder itself never shows up.)

- [ ] **Step 3: Wire the compose profile**

In `docker-compose.yml`, add a new service before the `sbconsole` (app) service section:

```yaml
  # ---------------------------------------------------------------------------------------
  # SQLite demo file (profile: sqlite). One-shot: builds reports.db in the sqlite-demo volume,
  # which the app profile mounts at /sqlite with SBC_SQLITE_ROOTS=/sqlite. Deliberately not /data:
  # that holds SbConsole's own database and data-protection keys.
  # ---------------------------------------------------------------------------------------
  sqlite-init:
    image: python:3.12-alpine
    container_name: nervecenter-sqlite-init
    profiles: ["sqlite"]
    command: ["python", "-I", "/seed.py", "/sqlite"]
    volumes:
      - ./docker/sqlite/seed.py:/seed.py:ro
      - sqlite-demo:/sqlite
    restart: "no"
```

In the `sbconsole` service add to `environment:`

```yaml
      # Directories the SQLite plugin may open files under (fail-closed when unset).
      SBC_SQLITE_ROOTS: /sqlite
```

and to its `volumes:`

```yaml
      - sqlite-demo:/sqlite
```

and to the top-level `volumes:` block:

```yaml
  sqlite-demo:
```

Run: `docker compose config --quiet`
Expected: no output (valid file).

- [ ] **Step 4: Document it**

In `.env.example`, under the app-profile section, add:

```
# SBC_SQLITE_ROOTS is set by docker-compose.yml to /sqlite (the sqlite-demo volume). For `dotnet run`,
# put it in launchSettings.json instead -- see docker/README.md "SQLite".
```

Append a section to `docker/README.md`:

```markdown
## SQLite

SQLite is a file, not a server, so there's no broker container — just a one-shot seed that writes a
~50 MB demo file shaped like the plugin mockups (tenants / sessions / events with JSON props, a
WITHOUT ROWID table, two views, a trigger, `ANALYZE` stats and some free pages).

- **Containerised app:** `docker compose --profile sqlite --profile app up -d --build`. The file
  lands in the `sqlite-demo` volume, mounted at `/sqlite`, and the app gets `SBC_SQLITE_ROOTS=/sqlite`.
  Add a connection of kind *SQLite* with file path `/sqlite/reports.db`.
- **`dotnet run`:** `python3 -I docker/sqlite/seed.py ./.sqlite-demo`, then set
  `SBC_SQLITE_ROOTS` in `launchSettings.json` to the absolute path of `.sqlite-demo` and use
  `<that path>/reports.db` as the connection's file path.

`SBC_SQLITE_ROOTS` is fail-closed: unset means no SQLite connection can open anything. Paths are
resolved (`..` collapsed, every symlink followed) before the check, and SbConsole's own database and
its `-wal`/`-shm`/`-journal` files are always refused. Known limitation: the bundled SQLite has no
`dbstat`, so the Overview ranks the largest tables by `sqlite_stat1` row estimates instead of bytes.
```

Append to `docs/design.md` (new dated section after §14, following the existing heading style):

```markdown
## 15. SQLite plugin (`SbConsole.Plugins.Sqlite`) (2026-10-13)

Spec: `docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md`; plan:
`docs/superpowers/plans/2026-10-13-sqlite-plugin.md`. Phase 1 is a read-only Overview + Tables
browser; write mode / Query (phase 2) and backups (phase 3) follow.

- **First plugin that opens host files.** A SQLite connection is a server-side path, so the SDK
  gained `IHostPathPolicy`, implemented in Core (`HostPathPolicy`) from `SBC_SQLITE_ROOTS`
  (read by `BootstrapOptions`, still the only env reader). It is **fail-closed** (no roots → nothing
  opens), resolves `..` and every symlink before a separator-aware root match, always refuses
  SbConsole's own DB and its sidecars, and hands back the resolved path, which is the only path the
  plugin opens. Core owns this rather than the plugin because only Core knows where its own DB is,
  and a plugin shouldn't learn host env var names.
- **`IPlugin.Initialize(IServiceProvider)`.** Plugins are constructed with `new()`, so instance hooks
  (`TestConnectionAsync`, dashboard hooks) had no DI. The new optional hook runs once after
  `app.Build()` (`PluginRegistry.InitializeAll`); the SQLite plugin resolves its operations there and
  fails closed until then. Existing plugins are unaffected (default no-op).
- **Statement timeout uses `sqlite3_interrupt`.** Verified against Microsoft.Data.Sqlite 10.0.12: a
  cancellation token (or `SqliteCommand.Cancel()`) does not stop a running statement; the raw
  interrupt does.
- **`dbstat` is absent** from the bundled `e_sqlite3`, so size-by-table falls back to
  `sqlite_stat1` row estimates; sizes appear automatically if a future build includes it.
- **Testing exception.** `SqliteOperations` is tested against temp-file databases (no Docker, no
  network) — the only honest way to test PRAGMA parsing, read-only mode and interrupts. Handlers and
  pages still use a substituted `ISqliteOperations`, as every other plugin does.
- **CSV export** is built server-side (capped at 100,000 rows) and handed to the browser through a
  `DotNetStreamReference` and the plugin's `sqlite.js` module — the host has no plugin download
  endpoint, and this didn't justify adding one.
```

In `CLAUDE.md`, under "What this is", add `SbConsole.Plugins.Sqlite` (SQLite files, read-only) to the plugin list; in the solution-layout tree add `│   └── SbConsole.Plugins.Sqlite/` under `Plugins/`; in the local dev stack block add:

```bash
docker compose --profile sqlite --profile app up -d --build   # + SQLite demo file at /sqlite (SBC_SQLITE_ROOTS)
```

and add `SBC_SQLITE_ROOTS` to the list of env vars documented by `launchSettings.json.example`.

- [ ] **Step 5: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`

```bash
git add docker/sqlite/seed.py docker-compose.yml docker/README.md .env.example .gitignore docs/design.md CLAUDE.md
git commit -m "docs(sqlite): demo seed, compose profile, design notes and setup docs"
```

---

### Task 16: End-to-end verification in the running app

**Files:** none (verification only; fix anything it finds with a failing test first).

- [ ] **Step 1: Seed and configure**

Run: `python3 -I docker/sqlite/seed.py ./.sqlite-demo` (skip if it already exists). In your local `src/SbConsole.Web/Properties/launchSettings.json` (git-ignored, unstaged local file — edit it but never commit it), set `SBC_SQLITE_ROOTS` to the absolute path of `.sqlite-demo`.

- [ ] **Step 2: Run the app**

Run: `dotnet run --project src/SbConsole.Web --launch-profile http`
Expected: app listens on `http://localhost:5249`.

- [ ] **Step 3: Walk the golden path in a browser**

1. Sign in, open **Connections**, add a connection of kind **SQLite**, file path `<abs>/.sqlite-demo/reports.db`, tag `prod`. Click **Test** → three passing checks: Path allowed, File opened (`SQLite 3.53.x · NN MB · WAL`), Schema readable (`6 tables`).
2. Add a second connection with path `/etc/hosts` → Test fails with "outside the allowed SQLite roots". Add one pointing at the app's own `SBC_DB_PATH` file (copy it into `.sqlite-demo` via a symlink to prove resolution: `ln -s <abs SBC_DB_PATH> .sqlite-demo/sneaky.db`) → Test fails with "belongs to SbConsole itself". Delete both test connections and the symlink afterwards.
3. **Overview** (`/p/sqlite/overview`): File tile shows size, free pages ≈10%, journal WAL; "Largest tables" lists events / event_props / sessions / users with ≈ rows and the dbstat footnote; **Run now** turns the strip to `ok` and an audit row `sqlite.integrity.check` appears on **Audit**.
4. **Tables** (`/p/sqlite/tables`): list shows 6 tables + 2 views; events opens with PK/FK badges and notes (alias of rowid, `→ sessions.id · ON DELETE CASCADE`, `indexed · idx_events_tenant_ts`, `JSON in …% of sampled rows`, `unix ms? (sampled)`); **count exactly** shows an exact number; **Data** tab shows 50 rows with date hints under `ts`; **DDL**, **Indexes**, **Foreign keys** tabs render; **Export CSV** on `tenants` downloads `tenants.csv` and an audit row `sqlite.table.export` appears.
5. Narrow the window to phone width (~375px): no horizontal page scroll, list stacks above the detail.
6. **Dashboard** shows a SQLite fleet card with "Databases 1".

- [ ] **Step 4: Final gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green. Nothing to commit unless Step 3 found a bug (then: failing test, fix, gate, commit `fix(sqlite): …`).
