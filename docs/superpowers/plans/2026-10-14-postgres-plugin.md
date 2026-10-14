# PostgreSQL Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `SbConsole.Plugins.Postgres` — Overview, Browse, Query, Activity, Maintenance — per
`docs/superpowers/specs/2026-10-14-postgres-plugin-design.md` (the spec) and mockup
`/Users/nimanosrati/Desktop/UI mockups for NerveCenter/SbConsole PostgreSQL.dc.html`.

**Architecture:** One plugin project referencing `SbConsole.Sdk` + Npgsql. A single
`IPostgresOperations` wrapper owns every SQL statement; handlers (`IScopedService`) wrap it with
secret lookup, audit and friendly errors (`HandlerRunner`/`PluginResult`, copied from the SQLite
plugin's pattern); Razor pages under `/p/postgres/*` inject handlers. One additive SDK overload on
`IConfirmationService` for the detailed confirm dialog.

**Tech Stack:** .NET 10, Npgsql 10.0.3, MudBlazor 9.9.0, xUnit 2.9.3, FluentAssertions 7.2.0,
NSubstitute 6.2.0, bUnit 2.10.3, Testcontainers.PostgreSql 4.16.0.

## Global Constraints

- `TreatWarningsAsErrors=true`; gate before every commit: `dotnet build -warnaserror` and the
  affected test projects green; full `dotnet test` at the end of each phase.
- TDD: failing test first for every behaviour.
- Plugin references `SbConsole.Sdk` only (plus NuGet packages). Never the DbContext. Persistence only
  through the keyed `IPluginStore` (`[FromKeyedServices(PostgresPlugin.PluginId)]`).
- DI by marker: handlers `IScopedService`, `PostgresOperations` `ISingletonService`;
  `ConfigureServices` calls `services.AddMarkedServices(typeof(PostgresPlugin).Assembly)`.
- Raw Npgsql exception text never reaches the UI or audit rows except through
  `FriendlyPostgresError` (which caps via `SbConsole.Sdk.FriendlyError.Truncate`); full exceptions are
  logged server-side.
- Destructive actions on prod connections go through `IConfirmationService`; `IsProd` comes from a
  server-side `IConnectionProvider.ListAsync("postgres")` lookup by connection id.
- Colours only via `--mud-palette-*` CSS variables; no hex in `postgres.css`.
- Plugin id / kind `postgres`; routes `/p/postgres/{overview,browse,query,activity,maintenance}`;
  query-string keys `connectionId`, `db`.
- Audit actions (exact): `pg.query.read`, `pg.query.write`, `pg.query.export`, `pg.table.export`,
  `pg.backend.cancel`, `pg.backend.terminate`, `pg.maintenance.vacuum`, `pg.maintenance.analyze`,
  `pg.maintenance.vacuum_full`, `pg.index.reindex`, `pg.index.drop`.
- Every Npgsql connection: `Application Name=sbconsole`, `Include Error Detail=false`.
- Thresholds (exact): idle-in-txn attention 5 min; dead tuples 30 %; bloat 20 %; connections problem
  ≥ 90 %; replication lag problem > 30 s; xid age problem > 1,500,000,000; blocked problem > 1 min;
  query history 90 days / 500 entries; CSV caps 100,000 rows / 50 MB; Data page sizes 25/50/100.
- Test naming: `Snake_case_sentences`; bUnit via `BunitContext` + `Render<T>` (old names are
  obsolete → build error).

---

## File map

```
src/Plugins/SbConsole.Plugins.Postgres/
  SbConsole.Plugins.Postgres.csproj
  PostgresPlugin.cs                       identity, nav, dashboard hooks, nav badge
  HandlerRunner.cs  PluginResult.cs       (SQLite pattern, FriendlyPostgresError)
  Client/
    PgAccessMode.cs  PostgresConnectionSettings.cs  PostgresConnectionFields.razor
    PgSqlLexer.cs  PgSqlGuard.cs  PgSqlRefusedException.cs
    FriendlyPostgresError.cs  PgErrorInfo.cs  PgFormat.cs  PgIdent.cs
    IPostgresOperations.cs
    PostgresOperations.cs (+ .Overview .Schema .Rows .Query .Activity .Maintenance .Export partials)
    OverviewModels.cs SchemaModels.cs RowModels.cs QueryModels.cs ActivityModels.cs MaintenanceModels.cs
    PgRowsSql.cs  CsvExport.cs  CsvFormat.cs
  Overview/  GetOverviewQueryHandler.cs  OverviewSnapshot.cs  OverviewSnapshotStore.cs  ListDatabasesQueryHandler.cs
  Browse/    ListSchemaQueryHandler.cs GetObjectDetailQueryHandler.cs GetDdlQueryHandler.cs
             PreviewRowsQueryHandler.cs ExportTableCsvCommandHandler.cs
             ReindexIndexCommandHandler.cs DropIndexCommandHandler.cs
  Query/     RunQueryCommandHandler.cs ExplainQueryQueryHandler.cs ExportQueryCsvCommandHandler.cs
             QueryHistoryStore.cs QueryHistoryEntry.cs ListQueryHistoryQueryHandler.cs QueryAudit.cs
  Activity/  GetActivityQueryHandler.cs ActivityTree.cs CancelBackendCommandHandler.cs TerminateBackendCommandHandler.cs
  Maintenance/ GetMaintenanceQueryHandler.cs RunMaintenanceCommandHandler.cs
  Components/ _Imports.razor PgPageHeader.razor PgConnectionPicker.razor PgDatabasePicker.razor
              PgStatTile.razor PgNullChip.razor PgJsonCell.razor PgErrorAlert.razor
  Pages/      _Imports.razor Overview.razor Browse.razor Query.razor Activity.razor Maintenance.razor
  wwwroot/    postgres.css postgres.js
tests/SbConsole.Plugins.Postgres.Tests/
  SbConsole.Plugins.Postgres.Tests.csproj  InMemoryPluginStore.cs
  Infrastructure/ PostgresFixture.cs DockerFactAttribute.cs
  Client/ (settings, lexer, guard, friendly error, rows sql, csv, ops-against-container tests)
  Overview/ Browse/ Query/ Activity/ Maintenance/  (handler tests)
  Pages/ PgPageTestBase.cs + one test class per page/component
  PostgresPluginTests.cs PostgresPluginDashboardTests.cs
src/SbConsole.Sdk/IConfirmationService.cs, ConfirmationRequest.cs   (Task 13)
src/SbConsole.Web/Confirmation/MudConfirmationService.cs, Components/Shared/ConfirmDialog.razor (Task 13)
src/SbConsole.Web/Program.cs, SbConsole.Web.csproj; SbConsole.slnx
docker/postgres/{init/01-app.sql,02-analytics.sql,03-billing.sql,blocker.sh}; docker-compose.yml; docker/README.md
docker/sqlite/demo-connections.json (adds pg entries); docs/design.md §18; CLAUDE.md
```

## Shared contract — `IPostgresOperations` and models

Every task reads this section. Names here are final; tasks must not rename them.
`database` parameters: `null` = the connection's own `Database`.

```csharp
namespace SbConsole.Plugins.Postgres.Client;

public interface IPostgresOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListDatabasesAsync(string secret, CancellationToken ct = default);
    Task<PgOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
    Task<int> GetBlockedCountAsync(string secret, CancellationToken ct = default);

    Task<PgSchemaTree> ListSchemaAsync(string secret, string? database, CancellationToken ct = default);
    Task<PgObjectDetail?> GetObjectDetailAsync(string secret, string? database, uint oid, CancellationToken ct = default);
    Task<string?> GetDdlAsync(string secret, string? database, uint oid, CancellationToken ct = default);
    Task<PgRowPage> PreviewRowsAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default);
    Task<CsvExport> ExportTableCsvAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default);
    Task<string> ReindexIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default);   // returns qualified name
    Task<string> DropIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default);

    Task<PgQueryResult> RunQueryAsync(string secret, string? database, PgQueryRequest request, CancellationToken ct = default);
    Task<string> ExplainAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default);
    Task<CsvExport> ExportQueryCsvAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default);

    Task<PgActivity> GetActivityAsync(string secret, CancellationToken ct = default);
    Task<PgBackend?> GetBackendAsync(string secret, int pid, CancellationToken ct = default);
    Task<bool> CancelBackendAsync(string secret, int pid, CancellationToken ct = default);
    Task<bool> TerminateBackendAsync(string secret, int pid, CancellationToken ct = default);

    Task<PgMaintenance> GetMaintenanceAsync(string secret, string? database, CancellationToken ct = default);
    Task<string> RunMaintenanceAsync(string secret, string? database, uint tableOid, PgMaintenanceKind kind, CancellationToken ct = default);
}
```

```csharp
// OverviewModels.cs
public sealed record PgOverview(
    string ServerVersion, bool IsReplica, DateTimeOffset StartedAt, DateTimeOffset CapturedAt,
    int Connections, int MaxConnections, int Idle, int IdleInTransaction, int Waiting,
    double? CacheHitRatio, string SharedBuffers,
    PgLongestTransaction? LongestTransaction,
    long TotalDatabaseBytes, int DatabaseCount,
    PgReplication Replication,
    long OldestXidAge,
    IReadOnlyList<PgDatabaseStat> Databases,
    PgAttention Attention);
public sealed record PgLongestTransaction(int Pid, string User, string Application, string State, TimeSpan Age);
public sealed record PgReplication(int ReplicaCount, TimeSpan? MaxLag, string? LaggiestName, long? WalRetainedBytes);
public sealed record PgDatabaseStat(string Name, long Bytes, string Owner, int Backends, double? HitRatio, long XactCommit, long Deadlocks);
public sealed record PgAttention(
    int BlockedCount, TimeSpan? LongestBlockedFor, int? TopBlockerPid, string? TopBlockerRelation,
    IReadOnlyList<PgIdleTransaction> IdleInTransaction,          // > 5 min only, longest first
    IReadOnlyList<PgDeadTupleTable> DeadTupleTables,             // > 30 %, worst first, current database
    int UnusedIndexCount, long UnusedIndexBytes, string? LargestUnusedIndex, long? LargestUnusedIndexBytes,
    DateTimeOffset? StatsResetAt);
public sealed record PgIdleTransaction(int Pid, string User, string Application, TimeSpan Age);
public sealed record PgDeadTupleTable(string Name, double DeadPercent);

// SchemaModels.cs
public enum PgObjectKind { Table, View, MaterializedView, Function, PartitionedTable, ForeignTable }
public sealed record PgObjectNode(uint Oid, string Schema, string Name, PgObjectKind Kind, long? RowEstimate);
public sealed record PgSchemaNode(string Name, IReadOnlyList<PgObjectNode> Tables, IReadOnlyList<PgObjectNode> Views,
    IReadOnlyList<PgObjectNode> MaterializedViews, IReadOnlyList<PgObjectNode> Functions);
public sealed record PgSchemaTree(string Database, IReadOnlyList<PgSchemaNode> Schemas);
public sealed record PgColumn(int Ordinal, string Name, string Type, bool Nullable, string? Default,
    bool IsPrimaryKey, string? References, string? Comment);
public sealed record PgIndex(uint Oid, string Name, string Definition, string ColumnsText, string Method, long Bytes,
    long Scans, long TuplesRead, bool IsPrimary, bool IsUnique, bool IsPartial, bool IsValid, bool BacksConstraint);
public sealed record PgConstraint(string Name, string Type, string Definition);   // Type: "primary key" | "foreign key" | "unique" | "check" | "exclusion"
public sealed record PgObjectDetail(uint Oid, string Schema, string Name, PgObjectKind Kind, long? RowEstimate,
    long HeapBytes, long IndexBytes, string Owner, IReadOnlyList<PgColumn> Columns, IReadOnlyList<PgIndex> Indexes,
    IReadOnlyList<PgConstraint> Constraints, DateTimeOffset? StatsResetAt);

// RowModels.cs
public enum PgFilterOp { Eq, NotEq, Gt, Gte, Lt, Lte, Like, ILike, IsNull, IsNotNull, Contains }
public sealed record PgFilter(string Column, PgFilterOp Op, string? Value);
public sealed record PgRowsRequest(uint Oid, IReadOnlyList<PgFilter> Filters, string? SortColumn, bool SortDescending, int Offset, int Limit);
public enum PgCellKind { Null, Text, Json, Bytes }
public sealed record PgCell(PgCellKind Kind, string? Display, int? JsonKeyCount = null, string? Full = null);
public sealed record PgResultColumn(string Name, string TypeName);
public sealed record PgRowPage(IReadOnlyList<PgResultColumn> Columns, IReadOnlyList<IReadOnlyList<PgCell>> Rows, long? EstimatedMatching);

// QueryModels.cs
public sealed record PgQueryRequest(string Sql, bool Write, int RowCap, int TimeoutS);
public sealed record PgQueryResult(IReadOnlyList<PgResultColumn> Columns, IReadOnlyList<IReadOnlyList<PgCell>> Rows,
    bool Capped, long? RowsAffected, TimeSpan Elapsed, int StatementCount, string? CommandTag);

// ActivityModels.cs
public sealed record PgBackend(int Pid, string User, string Application, string? Client, string State, string? WaitEvent,
    string Query, DateTimeOffset? XactStart, DateTimeOffset? QueryStart, TimeSpan? Duration,
    IReadOnlyList<int> BlockedBy, string? WaitingOnRelation, string? Database);
public sealed record PgActivity(int OwnPid, DateTimeOffset CapturedAt, IReadOnlyList<PgBackend> Backends);

// MaintenanceModels.cs
public enum PgMaintenanceKind { Vacuum, Analyze, VacuumFull }
public sealed record PgVacuumProgress(int Pid, string Table, DateTimeOffset? StartedAt, double? HeapScannedPercent);
public sealed record PgTableHealth(uint Oid, string Schema, string Name, long TotalBytes, long Live, long Dead, double DeadPercent,
    long? BloatBytes, DateTimeOffset? LastVacuum, DateTimeOffset? LastAutovacuum, DateTimeOffset? LastAnalyze,
    DateTimeOffset? LastAutoanalyze, bool AutovacuumRunning);
public sealed record PgMaintenance(bool AutovacuumOn, int MaxWorkers, IReadOnlyList<PgVacuumProgress> Running,
    long OldestXidAge, long FreezeMaxAge, bool PgstattupleAvailable, IReadOnlyList<PgTableHealth> Tables);
```

`CsvExport` (copy of SQLite's, using `PgFormat`): `record CsvExport(string FileName, byte[] Content, int RowCount, bool Truncated)` with `MaxRows = 100_000`, `MaxBytes = 50 MB`, `Summary`.

Handler result types: `PluginResult` / `PluginResult<T>` exactly like
`src/Plugins/SbConsole.Plugins.Sqlite/PluginResult.cs` but `Fail(Exception)` uses
`FriendlyPostgresError.From(ex)`. Query run additionally returns `PgErrorInfo` (Task 9).

---

# Phase 1 — Scaffold, connection, Overview

### Task 1: Project scaffold, settings, plugin identity

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Postgres/SbConsole.Plugins.Postgres.csproj`, `PostgresPlugin.cs`,
  `Client/PgAccessMode.cs`, `Client/PostgresConnectionSettings.cs`, `Components/_Imports.razor`,
  `Pages/_Imports.razor`, `wwwroot/postgres.css` (empty header comment)
- Create: `tests/SbConsole.Plugins.Postgres.Tests/SbConsole.Plugins.Postgres.Tests.csproj`,
  `InMemoryPluginStore.cs` (copy of SQLite's, namespace changed), `Client/PostgresConnectionSettingsTests.cs`,
  `PostgresPluginTests.cs`
- Modify: `SbConsole.slnx` (add both projects in the same folders as SQLite's), `src/SbConsole.Web/SbConsole.Web.csproj`
  (ProjectReference), `src/SbConsole.Web/Program.cs:66` (add `builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Postgres.PostgresPlugin>();`)

**Interfaces — Produces:**
```csharp
public enum PgAccessMode { ReadOnly, ReadWrite }
public sealed record PostgresConnectionSettings(
    string Host, int Port = 5432, string Database = "postgres", string Username = "", string Password = "",
    string SslMode = "Prefer", int ConnectTimeoutS = 5, int StatementTimeoutS = 30, PgAccessMode Mode = PgAccessMode.ReadOnly)
{
    public static readonly string[] SslModes = ["Disable", "Prefer", "Require", "VerifyCA", "VerifyFull"];
    public static PostgresConnectionSettings Parse(string secret);              // FormatException on bad input
    public string ToSecretString();                                            // ArgumentException on invalid fields
    public string BuildConnectionString(string? database = null);              // Npgsql string, forced keys
    public string Endpoint => $"{Host}:{Port}";
}
public sealed class PostgresPlugin : IPlugin { public const string PluginId = "postgres", Kind = "postgres",
    OverviewHref = "/p/postgres/overview", BrowseHref = "/p/postgres/browse", QueryHref = "/p/postgres/query",
    ActivityHref = "/p/postgres/activity", MaintenanceHref = "/p/postgres/maintenance"; }
```

csproj (mirror SQLite's):
```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <ItemGroup><ProjectReference Include="..\..\SbConsole.Sdk\SbConsole.Sdk.csproj" /></ItemGroup>
  <ItemGroup><InternalsVisibleTo Include="SbConsole.Plugins.Postgres.Tests" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="10.0.3" />
    <PackageReference Include="MudBlazor" Version="9.9.0" />
  </ItemGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
```
Test csproj = SQLite test csproj + `<PackageReference Include="Testcontainers.PostgreSql" Version="4.16.0" />`, referencing the Postgres plugin.

**Parse rules** (secret is `key=value;` like Npgsql): use `NpgsqlConnectionStringBuilder` for the
Npgsql keys after removing `Mode` and `StatementTimeoutS` case-insensitively (split on `;`, first `=`).
`Host` required (FormatException "Host is required."); Port 1–65535; `Timeout` → ConnectTimeoutS
(default 5); `SSL Mode` → one of `SslModes` (case-insensitive, normalised to the canonical spelling;
unknown → FormatException); `Mode` exact enum name (case-insensitive) else FormatException;
`StatementTimeoutS` positive int. `Database` default `postgres`. Any other Npgsql key is ignored
(not preserved) — the form is the source of truth.

`ToSecretString()` → via `NpgsqlConnectionStringBuilder { Host, Port, Database, Username, Password, SslMode, Timeout }`
`.ConnectionString + $";Mode={Mode};StatementTimeoutS={StatementTimeoutS}"`. Throws `ArgumentException` for blank host,
port out of range, timeouts ≤ 0, unknown SSL mode.

`BuildConnectionString(db)` → builder from fields plus `ApplicationName="sbconsole"`,
`IncludeErrorDetail=false`, `Pooling=true`, `Database = db ?? Database`, `CommandTimeout = StatementTimeoutS + 5`.

**Plugin identity:** `DisplayName "PostgreSQL"`, `Monogram "PG"`, `Version "0.1.0"`,
`ConnectionKindDisplayName "PostgreSQL"`, `ConnectionFormComponentType => typeof(PostgresConnectionFields)`
(Task 2 — until then return null), NavItems Overview/Browse/Query/Activity/Maintenance in that order,
`Contribution => new(PageCount: 5, ActionCount: 11)`. `GetConnectionSummary`: `Server = Endpoint`,
`Database`, `Mode = read-only|read-write`; FormatException → empty. `ConfigureServices` →
`AddMarkedServices`. `Initialize` resolves `IPostgresOperations` + `TimeProvider` (fields nullable;
fail closed: `TestConnectionAsync` returns `new(false, "PostgreSQL plugin is not initialized.")`).
`InitializeForTesting(IPostgresOperations, TimeProvider?)` internal. (`IPostgresOperations` interface
file is created in this task with the full contract above, plus the model files, so later tasks only
add implementations.)

- [ ] **Step 1: Write failing tests** — `PostgresConnectionSettingsTests`:
  `Parses_a_full_secret` (all fields), `Defaults_apply_when_keys_are_missing` (`Host=h` →
  5432/postgres/Prefer/5/30/ReadOnly), `Round_trips_through_ToSecretString`,
  `Missing_host_is_a_format_error`, `Unknown_mode_is_a_format_error`, `Unknown_ssl_mode_is_a_format_error`,
  `Non_positive_statement_timeout_is_a_format_error`, `ToSecretString_rejects_blank_host_and_bad_port`,
  `BuildConnectionString_forces_application_name_and_hides_error_detail` (parse result with
  NpgsqlConnectionStringBuilder: ApplicationName "sbconsole", IncludeErrorDetail false, no `Mode` key),
  `BuildConnectionString_overrides_the_database`, `Password_with_semicolon_round_trips` (builder quotes it).
  `PostgresPluginTests`: identity values, nav hrefs in order, summary never contains username/password,
  summary empty on garbage, `TestConnection_fails_closed_before_initialize`.
- [ ] **Step 2:** `dotnet test tests/SbConsole.Plugins.Postgres.Tests` → compile failure / red.
- [ ] **Step 3:** Implement. Add projects to `SbConsole.slnx`, Web reference and Program.cs line.
- [ ] **Step 4:** Tests green; `dotnet build -warnaserror` green; `dotnet test tests/SbConsole.Web.Tests --filter "FullyQualifiedName~ProgramDiRegistration|FullyQualifiedName~PluginRegistry"` green.
- [ ] **Step 5:** Commit `feat(postgres): plugin scaffold, connection settings, identity`.

### Task 2: Connection form

**Files:** Create `Client/PostgresConnectionFields.razor`; Test `tests/.../Client/PostgresConnectionFieldsTests.cs`.
Set `ConnectionFormComponentType => typeof(PostgresConnectionFields)`.

Same structure as `SqliteConnectionFields.razor` (read `InitialSecret` once in `OnInitialized`;
catch `FormatException`; emit `SecretChanged` with `ToSecretString()`, skip emission on
`ArgumentException`; per-field "touched" errors). Fields with CSS classes:
`pg-conn-host` (MudTextField, required "A host is required."), `pg-conn-port` (MudNumericField,
"Port must be 1–65535."), `pg-conn-database`, `pg-conn-username`, `pg-conn-password`
(`InputType.Password`), `pg-conn-ssl` (MudSelect over `SslModes`), `pg-conn-connect-timeout`,
`pg-conn-statement-timeout` ("Must be at least 1 s."), mode radios `pg-conn-mode-readonly` /
`pg-conn-mode-readwrite`, and helper text under Mode: "Read-only runs statements in a read-only
transaction. It is not a privilege boundary — for hard enforcement connect as a read-only role."

- [ ] Tests first (bUnit, `BunitContext`, `Services.AddMudServices()`, `JSInterop.Mode = Loose`):
  `Fills_fields_from_the_initial_secret`, `Typing_a_host_emits_a_secret`, `Clearing_the_host_shows_an_error_and_emits_nothing`,
  `Switching_to_read_write_emits_Mode_ReadWrite`, `Unparsable_initial_secret_renders_defaults`.
- [ ] Red → implement → green → commit `feat(postgres): structured connection form`.

### Task 3: Lexer, guard, friendly errors, formatting

**Files:** Create `Client/PgSqlLexer.cs`, `Client/PgSqlGuard.cs`, `Client/PgSqlRefusedException.cs`,
`Client/FriendlyPostgresError.cs`, `Client/PgErrorInfo.cs`, `Client/PgFormat.cs`, `Client/PgIdent.cs`;
tests `Client/PgSqlLexerTests.cs`, `PgSqlGuardTests.cs`, `FriendlyPostgresErrorTests.cs`, `PgFormatTests.cs`, `PgIdentTests.cs`.

```csharp
public sealed record PgStatement(int Start, int Length, string Text, string FirstKeyword); // keyword upper-case, "" if none
public static class PgSqlLexer
{
    public static IReadOnlyList<PgStatement> Split(string sql);              // top-level ';', empties dropped
    public static (int Line, int Column) LineAndColumn(string sql, int position); // position 1-based chars; clamps
}
public sealed class PgSqlRefusedException(string message) : Exception(message);
public static class PgSqlGuard
{
    // Read mode: exactly one statement, first keyword not in ReadRefused. Write mode: ≥1 statements, none in WriteRefused,
    // and none matching CREATE INDEX CONCURRENTLY / REINDEX ... CONCURRENTLY / CREATE|DROP DATABASE / ALTER SYSTEM.
    public static IReadOnlyList<PgStatement> Check(string sql, bool write);  // throws PgSqlRefusedException
}
public sealed record PgErrorInfo(string Message, string? Hint, string? SqlState, string? ConditionName, int? Line, int? Column, bool IsConnectionFailure);
public static class FriendlyPostgresError
{
    public static string From(Exception ex);                                 // one capped sentence
    public static PgErrorInfo Describe(Exception ex, string? sql = null);    // for Query's error panel
}
public static class PgIdent { public static string Quote(string identifier); public static string Qualified(string schema, string name); }
public static class PgFormat { Bytes(long), Count(long), Duration(TimeSpan) ("14m 02s", "41 s", "0.8 s", "3h 12m", "41d"), Ago(TimeSpan) ("12 s ago"), Percent(double, decimals=1) }
```

Lexer rules: single quotes with `''` escape; `E'…'`/`e'…'` with backslash escapes; `"…"` with `""`;
dollar quotes `$tag$…$tag$` where tag is empty or `[A-Za-z_][A-Za-z0-9_]*` (a `$1` parameter is not a
dollar quote); `-- …\n`; nested `/* /* */ */`. Unterminated quote/comment runs to end (statement kept;
the server will report the syntax error). `FirstKeyword` = first `[A-Za-z_]+` token outside
comments, upper-cased; for `WITH`, keep `WITH` (the guard treats `WITH … DELETE` as write-allowed but
read-mode will be refused by the server inside `READ ONLY` with 25006, which is fine).

Read-refused keywords: `BEGIN START COMMIT END ROLLBACK ABORT SAVEPOINT RELEASE PREPARE SET RESET
DISCARD LISTEN UNLISTEN NOTIFY COPY VACUUM CHECKPOINT DO CALL LOCK`. Write-refused: `BEGIN START COMMIT
END ROLLBACK ABORT SAVEPOINT RELEASE PREPARE VACUUM COPY CHECKPOINT` + the regex cases. Messages:
- multi-statement read: "Read-only mode runs one statement at a time — this has N. Switch to Write to run a script."
- refused keyword: "`COMMIT` can't run here — the console manages the transaction." (keyword substituted)
- VACUUM: "VACUUM can't run in a transaction — use Maintenance."
- empty: "Nothing to run."

FriendlyPostgresError.Describe: for `PostgresException pg` → SqlState `pg.SqlState`, ConditionName
from a small map (42703 undefined_column, 42P01 undefined_table, 42601 syntax_error, 42883
undefined_function, 23505 unique_violation, 23503 foreign_key_violation, 23502 not_null_violation,
23514 check_violation, 25006 read_only_sql_transaction, 57014 query_canceled, 40P01
deadlock_detected, 55P03 lock_not_available, 42501 insufficient_privilege, 28P01
invalid_password, 3D000 invalid_catalog_name, 53300 too_many_connections), Hint `pg.Hint`,
Line/Column from `pg.Position` (>0) via lexer when `sql` given. Message sentences:
- 42703: "Column `X` doesn't exist on `T`." when `pg.ColumnName`/`TableName` known, else "That column doesn't exist." — derive X from MessageText `column "x" does not exist` / `column o.x does not exist` via regex; T from `pg.TableName` or the alias prefix.
- 42P01: "Table `X` doesn't exist." (from `relation "x" does not exist`)
- 42601: "Syntax error near `TOKEN`." (from `at or near "tok"`), else "Syntax error."
- 25006: "This is a read-only transaction — switch to Write to change data."
- 57014: "The statement was cancelled — it hit the timeout or someone cancelled it."
- 23505: "That would duplicate a unique key (`constraint`)."; 23503: "That would break foreign key `constraint`."; 23502: "Column `c` can't be null."; 23514: "Check constraint `c` failed."
- 40P01 "Deadlock — the server rolled this statement back."; 55P03 "The row or table is locked by another session."; 42501 "Permission denied for this role."; 28P01 "Password authentication failed."; 3D000 "Database doesn't exist."; 53300 "The server has no connection slots left."
- default: `FriendlyError.Truncate(pg.MessageText)` with first letter upper-cased.
`NpgsqlException` with `SocketException`/`TimeoutException` inner, or `TimeoutException` → IsConnectionFailure, "Can't reach the server — it didn't answer in time." / "Can't reach the server — the connection was refused."; `PgSqlRefusedException` → its message; `FormatException` → "The saved connection settings are invalid: …"; other → `FriendlyError.From(ex)`.
`From(ex)` = `Describe(ex).Message`. Build `PostgresException` in tests with its public constructor
`new PostgresException(messageText, severity, invariantSeverity, sqlState, detail?, hint?, position, …, tableName:, columnName:, constraintName:)` (named args).

- [ ] Tests first. Lexer: `Splits_on_top_level_semicolons`, `Ignores_semicolons_in_single_quotes_with_doubled_quotes`,
  `Ignores_semicolons_in_E_strings_with_backslash_escapes`, `Ignores_semicolons_in_dollar_quotes_with_tags`,
  `A_parameter_like_$1_is_not_a_dollar_quote`, `Ignores_semicolons_in_line_and_nested_block_comments`,
  `Drops_empty_statements`, `First_keyword_skips_leading_comments`, `Line_and_column_from_position`.
  Guard: one test per refusal message + `Allows_single_select_in_read_mode`, `Allows_multi_statement_write`,
  `Refuses_create_index_concurrently_in_write_mode`. Friendly: one test per mapped code + connection failure +
  `Describe_maps_position_to_line_and_column`. Format: representative values. Ident: `Quote("a\"b") == "\"a\"\"b\""`.
- [ ] Red → implement → green → commit `feat(postgres): SQL lexer/guard and friendly errors`.

### Task 4: Testcontainers fixture + operations core (test, databases, overview)

**Files:** Create `tests/.../Infrastructure/DockerFactAttribute.cs`, `PostgresFixture.cs`;
`Client/PostgresOperations.cs`, `Client/PostgresOperations.Overview.cs`; test `Client/OperationsOverviewTests.cs`.

```csharp
// DockerFactAttribute: Skip set in ctor when SBC_SKIP_DOCKER_TESTS=1 or Docker isn't reachable.
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute() { if (!DockerAvailability.IsAvailable) Skip = "Docker not available"; }
}
internal static class DockerAvailability
{
    // Lazy: runs `docker info --format {{.ServerVersion}}` once (Process, 10 s timeout); true on exit 0.
    public static bool IsAvailable => Lazy.Value;
}
[CollectionDefinition("postgres")] public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
public sealed class PostgresFixture : IAsyncLifetime
{
    // Starts postgres:16-alpine via Testcontainers.PostgreSql (only if Docker available; else no-op).
    // Seeds database "app": schema public with customers(id bigserial pk, email text unique, country text),
    // orders(id bigserial pk, customer_id bigint references customers, status text not null default 'pending',
    // total_cents int, metadata jsonb, created_at timestamptz default now()), index idx_orders_status (status),
    // unused index idx_orders_legacy (total_cents), view v_order_totals, materialized view mv_country,
    // function order_count() returns bigint; 200 customers, 2,000 orders; schema reporting with table daily(d date).
    // ANALYZE. Also CREATE DATABASE analytics.
    public string Secret(PgAccessMode mode = PgAccessMode.ReadOnly, string database = "app");   // via PostgresConnectionSettings
    public string AdminConnectionString(string database = "app");                               // raw Npgsql string for test setup
    public PostgresOperations Operations { get; }                                                // shared instance
}
```
Ops test classes: `[Collection("postgres")]`, ctor takes fixture, every test `[DockerFact]`.

`PostgresOperations` core (`ISingletonService`, ctor `(ILogger<PostgresOperations> logger)`):
- `private NpgsqlConnection Open(PostgresConnectionSettings s, string? db)` → `new NpgsqlConnection(s.BuildConnectionString(db))`; callers `await OpenAsync(ct)`.
- `private async Task<T> ReadAsync<T>(string secret, string? db, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct, int? timeoutS = null)`:
  open; `BEGIN TRANSACTION READ ONLY` via `conn.BeginTransactionAsync(IsolationLevel.ReadCommitted)` then
  `SET TRANSACTION READ ONLY` and `SET LOCAL statement_timeout = '<n>s'` (n integer from settings — never user text);
  run; rollback in `finally`.
- `TestConnectionAsync`: `SELECT current_setting('server_version'), pg_is_in_recovery()`; success
  message `PostgreSQL {version} · {primary|replica}`; failure `new(false, FriendlyPostgresError.From(ex))` + log.
- `ListDatabasesAsync`: `SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname`.
- `GetOverviewAsync`: one read transaction, these queries (exclude own backend with `pid <> pg_backend_pid()`):
  ```sql
  SELECT current_setting('server_version'), pg_is_in_recovery(), pg_postmaster_start_time(), now(),
         current_setting('max_connections')::int, current_setting('shared_buffers'),
         (SELECT max(age(datfrozenxid)) FROM pg_database)::bigint;
  SELECT count(*) FILTER (WHERE backend_type = 'client backend'),
         count(*) FILTER (WHERE state = 'idle'),
         count(*) FILTER (WHERE state LIKE 'idle in transaction%'),
         count(*) FILTER (WHERE wait_event_type = 'Lock')
    FROM pg_stat_activity WHERE pid <> pg_backend_pid();
  SELECT pid, coalesce(usename,''), coalesce(application_name,''), coalesce(state,''), now() - xact_start
    FROM pg_stat_activity WHERE xact_start IS NOT NULL AND pid <> pg_backend_pid() AND backend_type = 'client backend'
    ORDER BY xact_start LIMIT 1;
  SELECT d.datname, pg_database_size(d.oid), pg_get_userbyid(d.datdba), coalesce(s.numbackends,0),
         CASE WHEN s.blks_hit + s.blks_read > 0 THEN s.blks_hit::float8 / (s.blks_hit + s.blks_read) END,
         coalesce(s.xact_commit,0), coalesce(s.deadlocks,0), s.blks_hit, s.blks_read
    FROM pg_database d LEFT JOIN pg_stat_database s ON s.datid = d.oid
   WHERE d.datallowconn AND NOT d.datistemplate ORDER BY pg_database_size(d.oid) DESC;
  -- replication (primary): 
  SELECT count(*), max(replay_lag), (array_agg(application_name ORDER BY replay_lag DESC NULLS LAST))[1],
         (SELECT sum(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn))::bigint FROM pg_replication_slots)
    FROM pg_stat_replication;          -- only when NOT in recovery
  -- replica: SELECT now() - pg_last_xact_replay_timestamp();  ReplicaCount 0, MaxLag = that.
  -- blocked:
  SELECT a.pid, now() - a.query_start, pg_blocking_pids(a.pid) FROM pg_stat_activity a
   WHERE cardinality(pg_blocking_pids(a.pid)) > 0;
  -- top blocker relation: for the blocker pid with most victims:
  SELECT l.relation::regclass::text FROM pg_locks l WHERE l.pid = @victim AND NOT l.granted AND l.relation IS NOT NULL LIMIT 1;
  -- idle in txn > 5 min:
  SELECT pid, coalesce(usename,''), coalesce(application_name,''), now() - state_change FROM pg_stat_activity
   WHERE state LIKE 'idle in transaction%' AND now() - state_change > interval '5 minutes' AND pid <> pg_backend_pid()
   ORDER BY state_change;
  -- dead tuples (current db):
  SELECT schemaname || '.' || relname, 100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0)
    FROM pg_stat_user_tables WHERE n_dead_tup > 0 AND n_live_tup + n_dead_tup >= 1000
     AND 100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0) > 30 ORDER BY 2 DESC;
  -- unused indexes (current db): 
  SELECT count(*), coalesce(sum(pg_relation_size(s.indexrelid)),0),
         (array_agg(s.indexrelname ORDER BY pg_relation_size(s.indexrelid) DESC))[1],
         max(pg_relation_size(s.indexrelid))
    FROM pg_stat_user_indexes s JOIN pg_index i ON i.indexrelid = s.indexrelid
   WHERE s.idx_scan = 0 AND NOT i.indisunique AND NOT i.indisprimary;
  SELECT stats_reset FROM pg_stat_database WHERE datname = current_database();
  ```
  Cache hit ratio = Σhit / (Σhit + Σread) over all rows (null if zero). Connections = client backends
  count + 1 (include ourselves, like the server's own count). Waiting = Lock waits.
  Every `IsDBNull` path maps to null/0. `CapturedAt` = server `now()`.

- [ ] Tests first (`[DockerFact]`): `Test_connection_reports_version_and_role` (contains "PostgreSQL 16", "primary"),
  `Test_connection_with_wrong_password_fails_friendly` (message "Password authentication failed."),
  `Lists_connectable_databases` (contains app, analytics, postgres; no template0),
  `Overview_reads_server_and_database_stats` (MaxConnections > 0, Databases contains "app" with Bytes > 0, Replication.ReplicaCount 0, OldestXidAge > 0),
  `Overview_counts_an_idle_in_transaction_session` (open an admin conn, BEGIN; SELECT 1 → IdleInTransaction ≥ 1; LongestTransaction pid equals it),
  `Overview_sees_a_blocked_session` (session A: BEGIN; UPDATE orders SET status='x' WHERE id=1; session B: UPDATE same row in background task with CancellationToken; poll until BlockedCount==1; TopBlockerPid == A's pid; TopBlockerRelation contains "orders"; then rollback A),
  `Overview_flags_unused_indexes` (idx_orders_legacy counted), `Overview_flags_tables_over_30_percent_dead`
  (in a scratch table with autovacuum_enabled=false: insert 2,000, delete 1,000, `SELECT pg_stat_force_next_flush()` + wait until pg_stat shows it — poll ≤ 5 s).
- [ ] Red (or skipped if Docker absent — confirm Docker is running: `docker info`) → implement → green → commit `feat(postgres): operations core, overview SQL, Testcontainers fixture`.

### Task 5: HandlerRunner, Overview handlers, snapshot store, dashboard hooks

**Files:** Create `HandlerRunner.cs`, `PluginResult.cs`, `Overview/GetOverviewQueryHandler.cs`, `Overview/OverviewSnapshot.cs`,
`Overview/OverviewSnapshotStore.cs`, `Overview/ListDatabasesQueryHandler.cs`; modify `PostgresPlugin.cs` (dashboard + badge);
tests `Overview/GetOverviewQueryHandlerTests.cs`, `PostgresPluginDashboardTests.cs`.

```csharp
public sealed record OverviewSnapshot(PgOverview Overview, DateTimeOffset LoadedAt);
public sealed record OverviewState(OverviewSnapshot? LastGood, DateTimeOffset? LastFailureAt, string? LastError);
internal static class OverviewSnapshotStore
{   // key "overview:<connectionId>", JSON (System.Text.Json, web defaults)
    public static Task<OverviewState> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct);
    public static Task WriteGoodAsync(IPluginStore store, Guid connectionId, OverviewSnapshot snapshot, CancellationToken ct); // clears failure
    public static Task WriteFailureAsync(IPluginStore store, Guid connectionId, DateTimeOffset at, string error, CancellationToken ct); // keeps LastGood
}
public sealed record OverviewView(PgOverview? Overview, bool IsStale, DateTimeOffset? StaleSince, string? Error, PgErrorInfo? ErrorInfo,
    IReadOnlyDictionary<string, double?> CommitsPerSecond);   // keyed by database name; null = no previous sample
public sealed class GetOverviewQueryHandler(IPostgresOperations ops, IConnectionProvider connections,
    [FromKeyedServices(PostgresPlugin.PluginId)] IPluginStore store, TimeProvider clock, ILogger<GetOverviewQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<OverviewView>> HandleAsync(Guid connectionId, CancellationToken ct = default);
}
public sealed class ListDatabasesQueryHandler(...) : IScopedService { Task<PluginResult<IReadOnlyList<string>>> HandleAsync(Guid connectionId, CancellationToken ct = default); }
```
Overview handler: secret not found → `Fail("Connection not found.")`. Success → compute commits/s =
`(now.XactCommit - prev.XactCommit) / (now.CapturedAt - prev.CapturedAt).TotalSeconds` per db when
previous snapshot exists, elapsed > 0 and delta ≥ 0; write good snapshot (store failures logged, ignored);
`Ok(new OverviewView(o, false, null, null, null, rates))`. Failure (not caller-cancel) → log, write
failure, return **Ok** with `Overview = LastGood?.Overview`, `IsStale = LastGood != null`,
`StaleSince = LastGood?.LoadedAt`, `Error` + `ErrorInfo` from `FriendlyPostgresError.Describe`.
(The page needs the stale values, so failure is data, not `Fail`.)

Dashboard (`PostgresPlugin`): `GetDashboardMetricsAsync` → `[new("Servers", 1)]`.
`internal static IReadOnlyList<PluginDashboardProblem> ComputeProblems(PgOverview o)`:
- blocked with `LongestBlockedFor > 1 min`: Error `"{n} sessions blocked"` (1 → "1 session blocked"), detail `"blocker pid {p}{ on rel}"`, href Activity
- idle in txn list non-empty: Warning "Idle in transaction", `"pid {p} for {Duration}"` (+ " and N more"), Activity
- `Connections * 100 / MaxConnections >= 90`: Warning `"Connections at {pct}%"`, `"{c} / {max} max_connections"`, Overview
- `Replication.MaxLag > 30 s`: Warning "Replication lag", `"{Duration} on {name}"`, Overview
- `OldestXidAge > 1_500_000_000`: Error "Transaction ID wraparound", `"oldest xid age {Count}"`, Maintenance
`GetDashboardProblemsAsync` → ops null → `[]`; else `ComputeProblems(await ops.GetOverviewAsync(secret, ct))` (exceptions propagate).
`GetNavBadgeAsync(href, secret)`: href == ActivityHref and ops != null → `GetBlockedCountAsync`; 0 → null; exception → null (logged nowhere — no logger; swallow).
`GetBlockedCountAsync` SQL: `SELECT count(*) FROM pg_stat_activity WHERE cardinality(pg_blocking_pids(pid)) > 0` (implement in Overview partial + add a DockerFact test).

- [ ] Tests first: handler — not found; success writes snapshot; second call computes commits/s (FakeTimeProvider not needed — uses CapturedAt);
  failure with previous snapshot returns stale view + error, keeps LastGood; failure without snapshot returns null overview + error;
  caller cancellation rethrows. Dashboard — each rule (boundary values: exactly 90 % flags, 89 % doesn't; lag 30 s doesn't, 31 s does), nav badge null when 0 / on throw.
- [ ] Red → implement → green → commit `feat(postgres): overview handlers, stale snapshot, dashboard problems`.

### Task 6: Shared components + Overview page

**Files:** Create `Components/PgPageHeader.razor`, `PgConnectionPicker.razor`, `PgDatabasePicker.razor`, `PgStatTile.razor`,
`PgNullChip.razor`, `PgErrorAlert.razor`, `Pages/Overview.razor`, `wwwroot/postgres.css`; tests `Pages/PgPageTestBase.cs`,
`Pages/OverviewPageTests.cs`, `Pages/PgConnectionPickerTests.cs`.

UI uses stock MudBlazor (`MudPaper`, `MudProgressLinear`, `MudChip`, `MudList`, `MudTable`,
`MudAlert`, `MudSkeleton`, `MudCollapse`, `MudMenu`) — the mockup's stated design. CSS classes in
`postgres.css` prefixed `pg-` only for layout (grid of tiles, mono text, dimmed stale) and use
`var(--mud-palette-*)`.

- `PgPageHeader` (params: `Title`, `Chips`, `Meta`, `Actions`, `Class`; links `_content/SbConsole.Plugins.Postgres/postgres.css` via `HeadContent`).
- `PgConnectionPicker` (params: `SelectionChanged: EventCallback<ConnectionInfo>`, `Loading`): lists `postgres` connections,
  `?connectionId=` (replace navigation), first by default; MudMenu `pg-connection-menu`, items `pg-connection-option`, PROD chip `pg-prod-chip`;
  red dot `pg-unreachable-dot` on items whose `OverviewSnapshotStore` state has `LastFailureAt` newer than `LastGood?.LoadedAt`
  (inject keyed `IPluginStore`); summary chip shows `Server` + `Mode`. Empty state `pg-no-connections` with a link to `/connections`.
  Use the activator `@onclick` toggle exactly like `SqliteConnectionPicker` (MudBlazor 9 activator quirk).
- `PgDatabasePicker` (params: `ConnectionId`, `Default`, `SelectedChanged: EventCallback<string>`): loads `ListDatabasesQueryHandler`; `?db=`; MudSelect `pg-database-picker`.
- `PgStatTile` (params: `Label`, `Value`, `Caption`, `Meter` (double? 0–1), `Warning` bool → outlined warning border, `Stale` string? → dimmed + "stale · HH:mm" caption).
- `PgNullChip` → `<MudChip T="string" Size="Size.Small" Variant="Variant.Outlined" Class="pg-null">NULL</MudChip>` (dashed border via CSS).
- `PgErrorAlert` (params: `Info: PgErrorInfo`, `Title`, `ChildContent` actions) → MudAlert error, message first, hint, Details MudCollapse with `SQLSTATE x · condition`.

Overview page `/p/postgres/overview` (inject `GetOverviewQueryHandler`, `TimeProvider`; `@implements IDisposable`):
- Header: "Overview", picker, meta `pg-overview-meta` = `PostgreSQL {ver} · {primary|replica} · up {Duration(now-StartedAt)}`, actions: `refreshed N s ago` (`pg-overview-refreshed`), Refresh button `pg-overview-refresh`.
- Tiles (`pg-tile-connections|cache|longest|size|replication`) with captions exactly as the mockup:
  connections `"{c} / {max} max_connections"`, caption `"{pct}% · {idle} idle · {waiting} waiting"`, meter c/max;
  cache `"{ratio:0.0}%"`, caption `"shared_buffers {SharedBuffers}"`;
  longest `Duration(age)` or "0 s", caption `"pid {p} · {user} · {state}"` + link "View in Activity →" (`pg-longest-link`), `Warning` when > 5 min;
  size `Bytes(total)`, caption `"{n} databases"`;
  replication: no replicas → "—" caption "no replicas"; else `Duration(MaxLag)` caption `"{n} streaming replicas · WAL {Bytes} retained"`.
- Needs attention (`pg-attention`, count chip): rows `pg-attention-item` in order blocked, idle in txn, dead tuples (first 4 names + %), unused idx (count, bytes, largest) each with chip label (`blocked`, `idle in txn`, `bloat`, `unused idx`) and link to Activity/Activity/Maintenance/Browse with `connectionId`. Empty → "Nothing needs attention." (`pg-attention-empty`).
- Databases table (`pg-databases`, MudTable): Name, Size, Owner, Connections, Hit ratio, Commits/s ("—" when null), Deadlocks. Footnote "Metrics are read-only queries against pg_stat_* and are not audited. Everything under Activity and Maintenance is."
- Unreachable: when `Error` non-null → `PgErrorAlert` (`pg-overview-error`) titled "Can't reach {connection name}." with Retry now (`pg-overview-retry`), Edit connection (`/connections`), and a countdown `pg-overview-retry-in` "retrying in N s" (30 s timer via `TimeProvider.CreateTimer`, ticks every 1 s, reloads at 0; disposed in Dispose). If `IsStale` → tiles render with `Stale = $"stale · {StaleSince:HH:mm}"`; attention + databases show their header with 3 `MudSkeleton` rows and "waiting for connection".
- Loading first time → MudSkeleton tiles.

`PgPageTestBase` = `SqlitePageTestBase` pattern: `Operations = Substitute.For<IPostgresOperations>()`, `Connections` returning one
connection `pg-prod-eu` tagged prod with summary `Server=db:5432`, `Mode=read-write`; secret
`Host=db;Mode=ReadWrite`; `Store`, `Audit`, `Confirmation` (returns true for both overloads once Task 13 lands), `Time`;
registers every handler as it is added (later tasks extend the base); `RenderWithPopovers`.

- [ ] Tests first: tiles render captions from a sample overview; longest-txn tile has warning class when > 5 min;
  attention list order + links carry connectionId; empty attention text; commits/s "—" then value; unreachable with stale
  shows error, stale captions, skeleton rows; unreachable without snapshot shows skeleton tiles + error; Retry calls the handler again;
  picker: lists connections, selects from `?connectionId=`, red dot after a stored failure, empty state.
- [ ] Red → implement → green → full `dotnet build -warnaserror` + `dotnet test` → commit `feat(postgres): Overview page and shared components`.

**Phase 1 gate:** `dotnet build -warnaserror && dotnet test` green.

---

# Phase 2 — Browse

### Task 7: Schema, detail, DDL, rows operations

**Files:** Create `Client/PostgresOperations.Schema.cs`, `Client/PostgresOperations.Rows.cs`, `Client/PgRowsSql.cs`,
`Client/PostgresOperations.Export.cs`, `Client/CsvExport.cs`, `Client/CsvFormat.cs` (copy SQLite's RFC 4180 writer);
tests `Client/PgRowsSqlTests.cs` (pure), `Client/OperationsSchemaTests.cs`, `Client/OperationsRowsTests.cs` (Docker).

```csharp
internal static class PgRowsSql
{
    // columns: (name, type) of the relation in ordinal order (from detail). Throws PgSqlRefusedException for an unknown
    // column, a value-less operator that needs one, or Contains on a non-jsonb/array type.
    public static (string Sql, IReadOnlyList<string?> Parameters) Build(string qualifiedName,
        IReadOnlyList<(string Name, string Type)> columns, PgRowsRequest request, bool forCount = false);
}
```
Build: `SELECT * FROM {qualified} WHERE … ORDER BY {Quote(sort)} {ASC|DESC} NULLS LAST LIMIT {limit} OFFSET {offset}`;
filter → `{Quote(col)} = $n::{type}` for Eq (etc.: `<>`, `>`, `>=`, `<`, `<=`), `LIKE $n::text` / `ILIKE $n::text` applied to `{col}::text`,
`IS NULL`, `IS NOT NULL`, `@> $n::{type}` for jsonb or `type` ending `[]`. Type text comes from `format_type` (catalog), never from the
client. `limit` clamped 1–100 (export passes its own path), offset ≥ 0. Parameters are added as `NpgsqlParameter { Value = text }`
with `NpgsqlDbType.Unknown`-free text (`DbType.String`) — the `::type` cast does the conversion.

Schema SQL (`ListSchemaAsync`, read txn):
```sql
SELECT c.oid, n.nspname, c.relname, c.relkind, CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE c.relkind IN ('r','v','m','p','f') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
   AND NOT c.relispartition ORDER BY n.nspname, c.relname;
SELECT p.oid, n.nspname, p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND p.prokind IN ('f','p') ORDER BY 2, 3;
-- plus every schema (so empty schemas show): SELECT nspname FROM pg_namespace WHERE nspname NOT IN (...) AND nspname NOT LIKE 'pg_%'
```
relkind r/p/f → Tables (Kind Table/PartitionedTable/ForeignTable), v → Views, m → MaterializedViews.
Extension-owned objects excluded (`NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')`).

Detail (`GetObjectDetailAsync`): relation by oid (null if gone); `pg_table_size`, `pg_indexes_size`, owner;
columns from `pg_attribute` (attnum > 0, not dropped) with `format_type(atttypid, atttypmod)`, `NOT attnotnull`,
`pg_get_expr(adbin, adrelid)` default, PK membership, FK target (`confrelid::regclass || '(' || cols || ')'`), `col_description`;
indexes from `pg_index i JOIN pg_class ic ... LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = i.indexrelid` with
`pg_get_indexdef(i.indexrelid)`, ColumnsText = substring of the definition from the first `(` (plus ` WHERE …` when partial),
`am.amname`, `pg_relation_size`, `coalesce(s.idx_scan,0)`, `coalesce(s.idx_tup_read,0)`, `indisprimary`, `indisunique`,
`indpred IS NOT NULL`, `indisvalid`, `EXISTS (SELECT 1 FROM pg_constraint WHERE conindid = i.indexrelid)`;
constraints from `pg_constraint` (`contype` p/f/u/c/x → type names) with `pg_get_constraintdef(oid, true)`;
`StatsResetAt` from `pg_stat_database` for the current db. Views/functions: Columns from pg_attribute (views) or empty, no indexes.
Function oid: detail returns Kind Function with `Name`, `Owner`, empty lists, sizes 0.

DDL (`GetDdlAsync`): table → `CREATE TABLE {qualified} (\n    {col} {type}{ DEFAULT d}{ NOT NULL},\n…\n);` then
`ALTER TABLE {q} ADD CONSTRAINT {name} {def};` per constraint, then each non-constraint index `{pg_get_indexdef};`, then
`COMMENT ON COLUMN …` for commented columns. View/matview → `CREATE [MATERIALIZED] VIEW {q} AS\n{pg_get_viewdef(oid, true)}`.
Function → `pg_get_functiondef(oid)`. Missing → null.

Rows (`PreviewRowsAsync`): detail columns → `PgRowsSql.Build`; read txn; reader → columns (`GetName`, `GetDataTypeName`);
cells: DBNull → Null; `json`/`jsonb` → read as string, parse with `JsonDocument`: object → key count, array → element count; Display "{…} N keys"
(object; "1 key") / "[…] N items"; Full = pretty JSON; `bytea` → `\x` + first 16 bytes hex + `… (N bytes)`; else
`Convert.ToString(value, CultureInfo.InvariantCulture)` with `DateTime`/`DateTimeOffset` formatted `yyyy-MM-dd HH:mm:ss`,
truncated at 200 chars (Full = whole up to 10,000). Estimate: no filters → detail `RowEstimate`; filters → `EXPLAIN (FORMAT JSON) <count-free select without LIMIT/OFFSET>` → `[0].Plan["Plan Rows"]`.

Export (`ExportTableCsvAsync`): same SQL without LIMIT/OFFSET, streamed, stop at `MaxRows` or `MaxBytes`; header row
column names; values raw text (null → empty); file name `{schema}.{table}-{yyyyMMdd-HHmmss}.csv` (UTC).

- [ ] Tests first: `PgRowsSqlTests` (each operator SQL text and parameters; unknown column refused; `@>` on text refused;
  identifier with a quote is quoted; limit clamped). Docker: schema lists public tables with estimates, views, matview,
  function, schema reporting; excludes pg_catalog; detail for orders (columns incl. PK/FK/default, indexes incl. primary + unused flags, constraints, sizes > 0);
  detail for a dropped oid → null; DDL contains CREATE TABLE + constraints + index; view DDL; rows page 50 with columns + types;
  each filter op returns expected rows (`status = pending`, `metadata @> {"gift":false}`, `is null`); sort desc; jsonb cell key count; null cell kind;
  estimate with filter > 0; export truncates at a lowered cap (make `MaxRows` overridable via an `internal static int? MaxRowsOverride` for tests).
- [ ] Red → implement → green → commit `feat(postgres): schema, detail, DDL, rows and table export operations`.

### Task 8: Browse handlers + page

**Files:** Create `Browse/ListSchemaQueryHandler.cs`, `GetObjectDetailQueryHandler.cs`, `GetDdlQueryHandler.cs`,
`PreviewRowsQueryHandler.cs`, `ExportTableCsvCommandHandler.cs`, `Components/PgJsonCell.razor`, `Pages/Browse.razor`,
`wwwroot/postgres.js`; tests `Browse/BrowseHandlerTests.cs`, `Pages/BrowsePageTests.cs`.

Handlers (all take `(IPostgresOperations, IConnectionProvider, ILogger<T>)`, queries via `HandlerRunner.QueryAsync`):
```csharp
ListSchemaQueryHandler.HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) → PluginResult<PgSchemaTree>
GetObjectDetailQueryHandler.HandleAsync(Guid connectionId, string? database, uint oid, CancellationToken ct = default) → PluginResult<PgObjectDetail?>
GetDdlQueryHandler.HandleAsync(Guid connectionId, string? database, uint oid, CancellationToken ct = default) → PluginResult<string?>
PreviewRowsQueryHandler.HandleAsync(Guid connectionId, string? database, PgRowsRequest request, CancellationToken ct = default) → PluginResult<PgRowPage>
public sealed record ExportTableCsvCommand(Guid ConnectionId, string ConnectionName, string? Database, string QualifiedName, PgRowsRequest Request);
ExportTableCsvCommandHandler(ops, connections, IAuditScope audit, logger).HandleAsync(ExportTableCsvCommand, ct) → PluginResult<CsvExport>
   // audit pg.table.export, target "{connection}/{db}/{qualified}", ActionRisk.Safe, detail export.Summary (+ " · N filters"); failure audited with friendly text
```
`postgres.js`: `downloadFromStream(fileName, streamRef, contentType)` (copy of sqlite.js) and
`registerRunShortcut(element, dotnetRef)` (Ctrl/Cmd+Enter → `dotnetRef.invokeMethodAsync('RunFromShortcut')`), `unregisterRunShortcut(element)`.

Browse page `/p/postgres/browse` (query params `connectionId`, `db`, `oid`, `tab`):
- Header: picker + database picker (`Default` = connection summary `Database`), actions "Open in Query" (`pg-open-in-query` → `QueryHref?connectionId=&db=&sql=` URL-encoded `SELECT * FROM {qualified} LIMIT 100`), "Export CSV" (`pg-export-csv`).
- Left: `MudTextField` filter `pg-tree-filter`; `MudTreeView` (or nested MudList if MudTreeView in 9.9 is awkward to test — keep the CSS classes) with schema nodes `pg-tree-schema` (count badge), groups `pg-tree-group` (Tables/Views/Materialized views/Functions with counts), items `pg-tree-item` (name + estimate badge via `PgFormat.Count` compact form: 2.1M / 410M / 88K). First 50 tables then `pg-tree-more` "N more…" (click shows all). Filter: case-insensitive substring; shows "x of y" per schema. Footnote "Row badges are reltuples estimates."
- Right (when an object is selected): title `pg-object-title` `schema.name`, kind chip, meta `pg-object-meta` `"{rows} rows ≈ · {heap} + {idx} indexes · owner {o}"`, note "reads only — nothing on this page writes".
  `MudTabs` Data / Columns (count) / Indexes (count) / Constraints (count) / DDL.
- Data: chips "Filters N" and "Sort col dir"; a filter row above the grid — per column a MudSelect operator (`pg-filter-op-{i}`) and MudTextField (`pg-filter-value-{i}`) applied on Enter / blur; MudTable `pg-data-grid` with headers clickable for sort; NULL → `PgNullChip`; Json → `PgJsonCell` (button "{…} 4 keys" toggles a `<pre class="pg-json">` row below); footer `pg-data-footer` "Rows {from}–{to} of {N} matching ≈", rows-per-page MudSelect (25/50/100), pager `pg-data-prev`/`pg-data-next` with "page / pages".
  Filter errors (`PgSqlRefusedException` → Fail) show as MudAlert under the filter row.
- Columns: MudTable (Name, Type, Nullable, Default, Key (PK / → ref), Comment).
- Indexes: header line `pg-index-summary` "From pg_stat_user_indexes · stats reset N days ago · K unused · {bytes}"; MudTable rows `pg-index-row` with flags chips (`primary`, `unique`, `partial`, `invalid`, `unused {N}d` tinted Info); row MudMenu `pg-index-menu` with Copy definition (`pg-index-copy`, JS `navigator.clipboard.writeText` via `IJSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", def)`), and (Task 14) REINDEX / Drop. Footnote about unused = 0 scans since reset.
- Constraints: MudTable Name / Type / Definition (mono).
- DDL: `<pre class="pg-ddl">` + Copy button.
- Selecting an object updates `?oid=` (replace). Changing db reloads tree and clears selection.

- [ ] Tests first: handler not-found + success + export audit (success detail, failure audited). Page: tree renders schemas/groups/items with badges;
  filter narrows ("3 of 31" style counts); clicking item loads detail meta; Data grid shows NULL chip and json key count; applying a filter calls
  PreviewRows with that PgFilter; sort click toggles; pager next calls offset 50; Indexes tab flags + unused chip; DDL tab shows text;
  Open in Query href; Export calls handler and JS `downloadFromStream`.
- [ ] Red → implement → green → full gate → commit `feat(postgres): Browse page`.

---

# Phase 3 — Query

### Task 9: Query operations

**Files:** Create `Client/PostgresOperations.Query.cs`; test `Client/OperationsQueryTests.cs` (Docker).

`RunQueryAsync(secret, db, req)`:
1. `var settings = Parse(secret)`; `PgSqlGuard.Check(req.Sql, req.Write)` (throws refused).
2. `req.Write && settings.Mode != ReadWrite` → throw `PgSqlRefusedException("This connection is read-only — set Mode to Read-write in Connections to write.")`.
3. Timeout = clamp(req.TimeoutS, 1, 3600). RowCap clamp 1–10,000.
4. Open; `BeginTransactionAsync`; read mode → `SET TRANSACTION READ ONLY`; `SET LOCAL statement_timeout = '{t}s'`.
5. Execute the full text as one `NpgsqlCommand` (Npgsql splits multi-statement; `CommandTimeout = t + 5`). Read the **last** result set that has columns
   (`NextResultAsync` loop; for each set with `FieldCount > 0` reset and capture columns; read up to cap rows then stop reading that set (`Capped = true` if more));
   `RowsAffected = reader.RecordsAffected` (≥ 0 when write), `CommandTag` from `reader.Statements[^1].StatementType` upper text.
6. Read mode → rollback; write → commit. Exception → rollback (ignore rollback failure) and rethrow.
7. Cells via the same cell formatter as rows (extract `PgCellFormatter.Format(NpgsqlDataReader, int ordinal)` into `Client/PgCellFormatter.cs` in Task 7 if not already; reuse here).
`ExplainAsync`: guard read-mode check on sql; then `EXPLAIN (FORMAT TEXT) {sql}` in read txn; join lines with `\n`. (If the statement already starts with EXPLAIN, run it as is.)
`ExportQueryCsvAsync`: guard read; read txn; stream rows to CSV with caps; file name `query-{yyyyMMdd-HHmmss}.csv`.

- [ ] Tests first (Docker): select returns typed columns (`text`, `bigint`, `numeric`); row cap sets Capped; read mode refuses `UPDATE` with 25006
  (PostgresException SqlState 25006); multi-statement read refused before hitting server; `COMMIT; DELETE` refused; timeout 1 s on `pg_sleep(3)` → 57014;
  write on a ReadOnly secret refused; write commits (row visible after); write error rolls back the whole script (first statement's insert not visible);
  write reports RowsAffected; undefined column error has Position mapping to the right line via FriendlyPostgresError.Describe; explain returns "Seq Scan" or "Index"; export header + rows.
- [ ] Red → implement → green → commit `feat(postgres): query, explain and query export operations`.

### Task 10: Query handlers, history, audit

**Files:** Create `Query/QueryAudit.cs` (copy of SQLite's, namespace changed), `Query/QueryHistoryEntry.cs`, `Query/QueryHistoryStore.cs`,
`Query/RunQueryCommandHandler.cs`, `Query/ExplainQueryQueryHandler.cs`, `Query/ExportQueryCsvCommandHandler.cs`,
`Query/ListQueryHistoryQueryHandler.cs`; tests `Query/QueryHistoryStoreTests.cs`, `Query/RunQueryCommandHandlerTests.cs`, `Query/QueryHandlersTests.cs`.

```csharp
public sealed record QueryHistoryEntry(string Sql, bool Write, int? Rows, double ElapsedSeconds, string? SqlState,
    string Actor, DateTimeOffset At, string Hash, string? Database);
internal static class QueryHistoryStore
{   // "history:<connectionId>", newest first; on append drop entries older than 90 days, cap 500; Sql truncated to 4,000 chars
    public const int MaxEntries = 500; public const int MaxSqlLength = 4000; public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public static Task<IReadOnlyList<QueryHistoryEntry>> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct);
    public static Task AppendAsync(IPluginStore store, Guid connectionId, QueryHistoryEntry entry, DateTimeOffset now, CancellationToken ct);
}
public sealed record RunQueryCommand(Guid ConnectionId, string ConnectionName, string? Database, PgQueryRequest Request, string Actor);
public sealed record QueryRun(PgQueryResult? Result, PgErrorInfo? Error, string AuditAction, string Hash);
public sealed class RunQueryCommandHandler(IPostgresOperations ops, IConnectionProvider connections, IAuditScope audit,
    [FromKeyedServices(PostgresPlugin.PluginId)] IPluginStore store, TimeProvider clock, ILogger<RunQueryCommandHandler> logger) : IScopedService
{
    public const string ReadAction = "pg.query.read", WriteAction = "pg.query.write";
    public Task<PluginResult<QueryRun>> HandleAsync(RunQueryCommand cmd, CancellationToken ct = default);
}
ListQueryHistoryQueryHandler([FromKeyedServices] IPluginStore store).HandleAsync(Guid connectionId, ct) → IReadOnlyList<QueryHistoryEntry>
ExplainQueryQueryHandler(ops, connections, logger).HandleAsync(Guid connectionId, string? database, string sql, int timeoutS, ct) → PluginResult<string>
public sealed record ExportQueryCsvCommand(Guid ConnectionId, string ConnectionName, string? Database, string Sql, int TimeoutS);
ExportQueryCsvCommandHandler(ops, connections, audit, logger) → PluginResult<CsvExport>; audit "pg.query.export", detail "{hash} · {summary}"
```
Actor: the page passes the signed-in user name (`AuthenticationStateProvider` → `Identity.Name ?? "unknown"`) for history only; the audit row's actor is the host's own.
RunQuery flow: resolve secret (not found → Fail, no audit). Refusal (`PgSqlRefusedException` from ops) → `Ok(QueryRun(null, Describe(ex), action, hash))`, **not audited, not in history** (nothing ran).
Success → audit `succeeded: true`, detail `"{hash} · {preview} · {rows|affected} rows · {elapsed:0.00} s"`, target `"{connection}/{db}"`, risk `Safe` for read / `Mutating` for write; history append; `Ok(QueryRun(result, null, ...))`.
Server error → log, audit `succeeded: false` detail `"{hash} · {preview} · SQLSTATE {code}"`, history append with SqlState; `Ok(QueryRun(null, Describe(ex, sql), ...))`.
Connection failure → same as server error (it ran as far as connecting). Caller cancel → rethrow. History/audit write failures logged only.

- [ ] Tests first: history (newest first, 90-day drop with FakeTimeProvider, cap 500, sql truncation); run handler — not found; refused (no audit, no history);
  read success audit action/risk/detail + history entry; write success Mutating; server error audited failed with SQLSTATE and returns error info with line; cancellation rethrows; explain and export handlers.
- [ ] Red → implement → green → commit `feat(postgres): query handlers, audited runs and history`.

### Task 11: Query page

**Files:** Create `Pages/Query.razor`; tests `Pages/QueryPageTests.cs`. (Extend `PgPageTestBase` with Query handlers + an `AuthenticationStateProvider` test double returning user "admin".)

Layout (mockup 1c):
- Header: "Query", pickers, toolbar: `MudToggleGroup` `pg-mode-toggle` (Read-only / Write; Write disabled with tooltip "Connection is read-only" when the connection summary Mode is read-only), Rows MudSelect `pg-row-cap` (100/1,000/10,000), Timeout MudSelect `pg-timeout` (10/30/60/300 s; default from statement timeout — read from... use 30 when unknown), Export CSV `pg-query-export` (disabled in Write), Run `pg-run` with "Ctrl+↵" hint (Color.Warning in write mode).
- Switching to Write on a PROD connection → `Confirmation.ConfirmAsync("Enter write mode on", connectionName, isProd: true)`; cancel keeps Read-only. On prod in write: `MudAlert` Warning `pg-write-banner` "Write mode on a PROD connection. Statements run in a transaction and are audited with your name. Switch back to Read-only when you're done." + button `pg-back-to-readonly`.
- Editor: `MudTextField` multiline `pg-editor` (Lines 10, monospace class), initial text from `?sql=`; line-number gutter `pg-gutter` rendered from line count, marked line `pg-gutter-error` when an error has a Line.
- Ctrl+Enter via `postgres.js registerRunShortcut` on the editor wrapper; `[JSInvokable] RunFromShortcut`.
- Status line `pg-status`: "● N rows · {elapsed} s · read-only txn · timeout N s · cap N" (or "N rows affected · committed") + "audited as {action}".
- Result tabs `MudTabs`: Grid (`pg-result-grid`: `#` column, header name + type underneath, first 200 rows, "N of M rows shown · Show all" `pg-show-all`), JSON (`pg-result-json` pre), EXPLAIN (`pg-explain`, loads on tab activate via ExplainQueryQueryHandler; note "EXPLAIN without ANALYZE — add it yourself to run the query").
- Error: `PgErrorAlert` `pg-query-error` with message, hint, "Line L, position C", write → "Nothing was changed — the transaction rolled back.", Details "SQLSTATE … · condition · {elapsed} · audited as {action} (failed)"; "Fix and re-run" focuses the editor.
- History panel `pg-history` (right): entries `pg-history-item` (first 60 chars, "{ago} · N rows · X s", `ro`/`write` chip, `error {code}`, actor for writes, "audit row" link `/audit?search={hash}` for writes). Click loads text. Footnote "Kept 90 days per connection. Writes link to their audit row."

- [ ] Tests first: read run renders grid with type names and status; `Show all`; JSON tab; write toggle disabled for read-only connection; prod write asks confirmation (cancel stays read);
  banner appears and "Back to read-only" clears it; error panel shows hint, line, rollback sentence in write mode, gutter marks line; history lists entries and clicking loads text;
  `?sql=` prefill; export calls handler.
- [ ] Red → implement → green → full gate → commit `feat(postgres): Query page`.

---

# Phase 4 — Confirmation overload, Activity, index actions

### Task 12: Activity operations

**Files:** Create `Client/PostgresOperations.Activity.cs`; test `Client/OperationsActivityTests.cs` (Docker).

```sql
SELECT a.pid, coalesce(a.usename,''), coalesce(a.application_name,''), host(a.client_addr), coalesce(a.state,''),
       CASE WHEN a.wait_event_type IS NOT NULL THEN a.wait_event_type || ':' || a.wait_event END,
       coalesce(a.query,''), a.xact_start, a.query_start, now() - coalesce(a.xact_start, a.query_start),
       pg_blocking_pids(a.pid), a.datname,
       (SELECT l.relation::regclass::text FROM pg_locks l WHERE l.pid = a.pid AND NOT l.granted AND l.relation IS NOT NULL LIMIT 1)
  FROM pg_stat_activity a WHERE a.backend_type = 'client backend' ORDER BY a.xact_start NULLS LAST, a.pid;
SELECT pg_backend_pid(), now();
```
Duration null for `state = 'idle'`. `GetBackendAsync` = same query `WHERE a.pid = @pid`. Cancel/terminate:
`settings.Mode != ReadWrite` → throw `PgSqlRefusedException("This connection is read-only — set Mode to Read-write in Connections to cancel or terminate backends.")`;
`SELECT pg_cancel_backend(@pid)` / `pg_terminate_backend(@pid)` (not in a read-only txn — plain autocommit).

- [ ] Tests first (Docker, admin sessions as fixtures): activity lists an idle-in-txn session with state and query; marks OwnPid; blocking chain BlockedBy contains blocker pid and WaitingOnRelation "orders";
  GetBackend returns null for a dead pid; cancel stops `pg_sleep(30)` (task faults with 57014); terminate kills a session (its next command throws); read-only secret refused.
- [ ] Red → implement → green → commit `feat(postgres): activity, cancel and terminate operations`.

### Task 13: SDK detailed confirmation

**Files:** Create `src/SbConsole.Sdk/ConfirmationRequest.cs`; modify `src/SbConsole.Sdk/IConfirmationService.cs`,
`src/SbConsole.Web/Confirmation/MudConfirmationService.cs`, `src/SbConsole.Web/Components/Shared/ConfirmDialog.razor`;
tests `tests/SbConsole.Web.Tests/ConfirmDialogTests.cs` (extend), new `tests/SbConsole.Web.Tests/MudConfirmationServiceTests.cs` if none exists.

```csharp
namespace SbConsole.Sdk;
public sealed record ConfirmationDetail(string Label, string Value);
public sealed record ConfirmationRequest(string Verb, string Target, bool IsProd, string? Consequence = null,
    IReadOnlyList<ConfirmationDetail>? Details = null, string? AuditAction = null, bool Destructive = false);
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default);
    /// <summary>Detailed variant (design: docs/superpowers/specs/2026-10-14-postgres-plugin-design.md §10). Default falls back to the plain dialog.</summary>
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct = default) =>
        ConfirmAsync(request.Verb, request.Target, request.IsProd, null, ct);
}
```
`ConfirmDialog` new optional params `Consequence`, `Details`, `AuditAction`, `Destructive`. When `Consequence` is set it replaces the
"This will … cannot be undone." sentence; `Details` render as a two-column `<dl class="confirm-details">` (MudSimpleTable dense); `AuditAction` → caption
"Audited as {action} with your name."; typed field label "Type {Target} to confirm"; button text `"{Verb} {Target}"` when Details set, otherwise unchanged;
title `PROD` chip when IsProd. `MudConfirmationService` implements the overload with the same typed-for-prod setting logic.

- [ ] Tests first: dialog without new params renders exactly as before (existing tests stay green); with Details renders rows, consequence, audit caption;
  typed confirm with `pid 48213` disabled until exact match; service overload passes parameters (bUnit with `IDialogService`).
- [ ] Red → implement → green → `dotnet test tests/SbConsole.Web.Tests` → commit `feat(sdk): detailed confirmation request`.

### Task 14: Activity handlers, tree, page; index actions

**Files:** Create `Activity/ActivityTree.cs`, `Activity/GetActivityQueryHandler.cs`, `Activity/CancelBackendCommandHandler.cs`,
`Activity/TerminateBackendCommandHandler.cs`, `Browse/ReindexIndexCommandHandler.cs`, `Browse/DropIndexCommandHandler.cs`,
`Client/PostgresOperations.Indexes.cs` (Reindex/Drop), `Pages/Activity.razor`; modify `Pages/Browse.razor` (index menu);
tests `Activity/ActivityTreeTests.cs`, `Activity/BackendCommandHandlerTests.cs`, `Browse/IndexCommandHandlerTests.cs`,
`Client/OperationsIndexTests.cs` (Docker), `Pages/ActivityPageTests.cs`, extend `Pages/BrowsePageTests.cs`.

```csharp
public sealed record ActivityRow(PgBackend Backend, int Depth, int BlocksCount, bool IsOwn);
public static class ActivityTree
{   // top level: backends with no blockers, ordered by XactStart (nulls last) then pid; each blocker followed by its victims (depth 1, victim's
    // first blocker only), recursively (depth 2…), each backend once. Cycles → remaining backends appended at depth 0. BlocksCount = number of
    // backends listing this pid in BlockedBy. activeOnly drops state 'idle'; hidden idle count reported separately.
    public static (IReadOnlyList<ActivityRow> Rows, int HiddenIdle) Build(PgActivity activity, bool activeOnly, bool showIdle);
    public static ActivityRow? WorstBlocker(IReadOnlyList<ActivityRow> rows);   // max BlocksCount, tie → longest Duration
}
GetActivityQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) → PluginResult<PgActivity>
public sealed record BackendCommand(Guid ConnectionId, string ConnectionName, int Pid);
CancelBackendCommandHandler(ops, connections, audit, logger).HandleAsync(BackendCommand, ct) → PluginResult   // "pg.backend.cancel", Mutating
TerminateBackendCommandHandler(...)                                                                         // "pg.backend.terminate", Destructive
public sealed record IndexCommand(Guid ConnectionId, string ConnectionName, string? Database, uint IndexOid);
ReindexIndexCommandHandler → "pg.index.reindex" (Mutating); DropIndexCommandHandler → "pg.index.drop" (Destructive)
```
Backend handlers: secret; `GetBackendAsync` first — null → `Fail("pid N had already ended.")` audited failed;
refuse when `backend.Application == "sbconsole"` (`Fail("That's one of this console's own connections.")`, not audited) — the handler's connection is a different backend than the page's activity read, so pid equality can't identify "own"; call cancel/terminate; `false` → `Fail("pid N had already ended.")` audited failed; success audited with detail `"pid {p} · {user} · {app} · {QueryAudit.Preview(query)}"`; refusals (read-only) → Fail friendly, not audited.
Index ops: `ReindexIndexAsync`: Mode check; resolve `SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n … WHERE c.oid=@oid AND c.relkind='i'` (null → throw `PgSqlRefusedException("That index no longer exists.")`); `REINDEX INDEX CONCURRENTLY {qualified}` autocommit with statement timeout 1 h. `DropIndexAsync`: also refuse when `EXISTS(pg_constraint conindid = oid)` ("This index backs a constraint — drop the constraint instead."); `DROP INDEX CONCURRENTLY {qualified}`.

Activity page `/p/postgres/activity` (mockup 1d):
- Header "Activity", picker; meta `pg-activity-summary` "{n} backends · {b} blocked · {i} idle in txn"; actions: auto-refresh MudSelect `pg-autorefresh` (Off/5 s/15 s, default 5 s) + "next in N s" (`pg-next-refresh`, `TimeProvider` timer), switch `pg-active-only`.
- Banner `pg-blocker-banner` (MudAlert warning) when `WorstBlocker` state starts with "idle in transaction" and BlocksCount > 0: exact copy from spec §7 with relation from its first victim's `WaitingOnRelation`; "Jump to blocker ↓" scrolls (`element.scrollIntoView` via JS eval-free: `IJSRuntime.InvokeVoidAsync("postgresScrollTo", id)` — add `export function scrollToId(id)` to postgres.js and call through the module).
- MudTable `pg-activity-table` rows `pg-activity-row` (`pg-depth-1` etc. with `↳`), columns per spec; chips `blocks N` (`pg-blocks-chip`), `this console` (`pg-own-chip`, by `Pid == OwnPid`); idle rows hidden → footer `pg-idle-hidden` "… N idle backends hidden · show all".
- Inline Cancel ghost button `pg-cancel` disabled when state is idle / idle in transaction or own row; click → `ConfirmAsync("Cancel query of", $"pid {pid}", isProd: false)` → handler → snackbar (`ISnackbar`) success/error → reload.
- Row MudMenu `pg-row-menu`: Copy query, Filter by {user} (`pg-filter-user`, sets a user filter chip), Cancel query (mutating), divider, Terminate backend… (`pg-terminate`, `Color.Error`). Terminate → `ConfirmAsync(new ConfirmationRequest("Terminate", $"pid {pid}", isProd, Consequence: $"This kills the connection with pg_terminate_backend. Its open transaction ({dur}) rolls back and {app} will see a dropped connection." + (blocks>0 ? $" The {n} blocked sessions will proceed." : ""), Details: [pid, user "user · app · client", state "state · duration · blocks N", query], AuditAction: "pg.backend.terminate", Destructive: true))`. `isProd` from the selected `ConnectionInfo.IsProd` (server-side list lookup in the picker).
- Footnote "Blocked rows are indented under their blocker (pg_blocking_pids). Sorted by transaction start. Cancel and Terminate are audited · Audit log".

Browse index menu: REINDEX CONCURRENTLY (`pg-index-reindex`, plain confirm isProd:false) and divider + Drop index… (`pg-index-drop`, error colour, disabled with tooltip for constraint-backed indexes; `ConfirmationRequest("Drop index", name, isProd, Consequence: "DROP INDEX CONCURRENTLY — queries that used it fall back to other plans.", Details: [definition, size, scans], AuditAction: "pg.index.drop", Destructive: true)`), reload detail after.

- [ ] Tests first: tree (chain ordering/depth, multi-level, cycle fallback, activeOnly, hidden idle count, worst blocker); handlers (ended pid audited failed, sbconsole refusal not audited, read-only refusal, success detail, Destructive risk);
  index handlers + Docker ops (reindex ok, drop removes it, constraint-backed refused); Activity page (summary, indented rows, banner text, cancel disabled on idle, terminate sends ConfirmationRequest with typed target "pid 48213" and Destructive, cancel confirm false → no handler call, auto-refresh reload after advancing FakeTimeProvider 5 s, active-only, idle footer, own chip);
  Browse index menu actions.
- [ ] Red → implement → green → full gate → commit `feat(postgres): Activity page and index actions`.

---

# Phase 5 — Maintenance and local stack

### Task 15: Maintenance operations, handlers, page

**Files:** Create `Client/PostgresOperations.Maintenance.cs`, `Maintenance/GetMaintenanceQueryHandler.cs`,
`Maintenance/RunMaintenanceCommandHandler.cs`, `Pages/Maintenance.razor`; tests `Client/OperationsMaintenanceTests.cs` (Docker),
`Maintenance/MaintenanceHandlerTests.cs`, `Pages/MaintenancePageTests.cs`.

SQL (read txn, current db):
```sql
SELECT current_setting('autovacuum') = 'on', current_setting('autovacuum_max_workers')::int,
       current_setting('autovacuum_freeze_max_age')::bigint, (SELECT max(age(datfrozenxid)) FROM pg_database)::bigint,
       EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple');
SELECT p.pid, p.relid::regclass::text, a.xact_start,
       CASE WHEN p.heap_blks_total > 0 THEN 100.0 * p.heap_blks_scanned / p.heap_blks_total END
  FROM pg_stat_progress_vacuum p LEFT JOIN pg_stat_activity a ON a.pid = p.pid WHERE p.datname = current_database();
SELECT s.relid, s.schemaname, s.relname, pg_total_relation_size(s.relid), s.n_live_tup, s.n_dead_tup,
       coalesce(100.0 * s.n_dead_tup / nullif(s.n_live_tup + s.n_dead_tup, 0), 0),
       s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze
  FROM pg_stat_user_tables s ORDER BY 7 DESC, 4 DESC;
```
Bloat when pgstattuple present: for the top 25 rows, `SELECT (pgstattuple_approx(@oid)).dead_tuple_len + (pgstattuple_approx(@oid)).approx_free_space` — run per table inside the same read txn; errors on a table → null for it. `AutovacuumRunning` = relid in progress list.
`RunMaintenanceAsync`: Mode check (refused message mentions Maintenance); resolve `relkind IN ('r','m','p')` by oid (missing → refused "That table no longer exists."); autocommit; `SET statement_timeout` (session) to settings value for Vacuum/Analyze, `'1h'` for VacuumFull; `VACUUM (VERBOSE false) {q}` / `ANALYZE {q}` / `VACUUM FULL {q}`; returns qualified name.

```csharp
GetMaintenanceQueryHandler.HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) → PluginResult<PgMaintenance>
public sealed record MaintenanceCommand(Guid ConnectionId, string ConnectionName, string? Database, uint TableOid, PgMaintenanceKind Kind);
RunMaintenanceCommandHandler(ops, connections, audit, logger).HandleAsync(MaintenanceCommand, ct) → PluginResult<string>
   // action pg.maintenance.vacuum / .analyze / .vacuum_full; risk Mutating, Mutating, Destructive; target "{conn}/{db}/{qualified}" (qualified from result, or "oid N" on failure)
```
Flagged = `DeadPercent > 30 || (BloatBytes / TotalBytes > 0.20)`.

Page `/p/postgres/maintenance` (mockup 1f): header chips `pg-autovacuum-chip` "autovacuum on · N workers · K running"; buttons `pg-analyze-flagged` "ANALYZE K flagged" / `pg-vacuum-flagged` "VACUUM K flagged" (plain confirm `ConfirmAsync("VACUUM", "K flagged tables", false, K)`, run sequentially, progress text, reload after);
tiles: Flagged tables (`pg-tile-flagged`, caption "> 30% dead tuples or > 20% bloat est."), Reclaimable (est.) (`pg-tile-reclaimable`, sum of bloat or "—" with "install pgstattuple for bloat estimates"; caption "plain VACUUM frees for reuse, not to disk"), Autovacuum running (`pg-tile-autovacuum`: table, "started N ago · pid P · X% of heap scanned" or "idle"), Oldest xid age (`pg-tile-xid`: compact count, caption "wraparound at 2.1B · freeze_max_age {compact}", warning when > freeze max age);
MudTable `pg-maintenance-table` rows `pg-maintenance-row` (top 25 then `pg-maintenance-more` "N more tables under 1% dead · show all"), dead % chip warning when > 30, "running" chip for autovacuum, relative times via `PgFormat.Ago` ("—" null); row buttons `pg-vacuum` / `pg-analyze` (Variant.Outlined, Mutating, plain confirm, disabled when connection read-only with tooltip); row menu `pg-maintenance-menu` → `pg-vacuum-full` (error) → `ConfirmationRequest("VACUUM FULL", qualified, isProd, Consequence: "Takes an ACCESS EXCLUSIVE lock and rewrites the table — reads and writes wait until it finishes.", Details: [size, dead], AuditAction: "pg.maintenance.vacuum_full", Destructive: true)`.
Footnote "VACUUM and ANALYZE are Mutating: a short confirm, no typing. VACUUM FULL locks the table and lives in the row menu as Destructive. Audited as pg.maintenance.vacuum / .analyze".

- [ ] Tests first: Docker — maintenance reads tables with dead tuples, pgstattuple absent → BloatBytes null and flag false; vacuum reduces n_dead_tup (poll stats), analyze sets last_analyze, vacuum full works, read-only refused, dropped oid refused;
  handlers (audit action/risk per kind, failure audited); page (tiles, flagged count, row buttons call handler with kind, VACUUM FULL sends Destructive request, bulk buttons sequential calls, read-only disables).
- [ ] Red → implement → green → full gate → commit `feat(postgres): Maintenance page`.

### Task 16: Docker stack, demo connections, docs

**Files:** Create `docker/postgres/init/01-app.sql`, `02-analytics.sql`, `03-billing.sql`, `docker/postgres/blocker.sh`;
modify `docker-compose.yml` (profile `postgres`: services `postgres`, `postgres-blocker`; volume `postgres-data`; app service gets no new env),
`docker/sqlite/demo-connections.json` (+ `pg-local` ReadWrite tags `["prod"]`, `pg-local-ro` ReadOnly tags `["demo"]`, Host `postgres`, Port 5432, Database `app`, Username `sbconsole`, Password `sbconsole`),
`.env.example` (`POSTGRES_PORT=5433`, `POSTGRES_PASSWORD=sbconsole`), `docker/README.md` (PostgreSQL section), `docs/design.md` (§18), `CLAUDE.md` (plugin list + compose line).

- `postgres` service: `image: postgres:16`, container `nervecenter-postgres`, profile postgres, env `POSTGRES_USER=sbconsole`, `POSTGRES_PASSWORD=${POSTGRES_PASSWORD:-sbconsole}`, `POSTGRES_DB=app`, port `${POSTGRES_PORT:-5433}:5432`, volume `postgres-data:/var/lib/postgresql/data`, `./docker/postgres/init:/docker-entrypoint-initdb.d:ro`, healthcheck `pg_isready -U sbconsole -d app`, command `postgres -c shared_preload_libraries=pg_stat_statements -c track_io_timing=on`.
- `01-app.sql`: `CREATE EXTENSION pgstattuple;` tables customers (20k), products (2k), orders (200k, jsonb metadata, status mix incl. 'pending'), order_items (400k), payments (150k), sessions (50k), events (300k); indexes incl. `idx_orders_customer_created`, partial `idx_orders_status_pending`, gin `idx_orders_metadata_gin`, unused `idx_orders_legacy_ref (legacy_ref)`, `idx_orders_currency`; schema `audit` (1 table) and `reporting` (view `v_order_totals`, matview `mv_daily`), function `order_total(bigint)`; `ALTER TABLE sessions SET (autovacuum_enabled = false); ALTER TABLE order_items SET (autovacuum_enabled = false);` then `DELETE FROM sessions WHERE id % 5 < 2; DELETE FROM order_items WHERE id % 5 < 2;` `ANALYZE;` (all via `generate_series`).
- `02-analytics.sql`: `CREATE DATABASE analytics;` `\c analytics` small `daily_orders` table. `03-billing.sql`: `CREATE DATABASE billing;` `\c billing` `invoices` table.
- `postgres-blocker` service: `image: postgres:16`, profile postgres, depends_on postgres healthy, `PGPASSWORD`, `entrypoint: ["sh", "/blocker.sh"]`, mounts blocker.sh. Script loops: starts `psql … -c "BEGIN; UPDATE orders SET status='paid' WHERE id=1; SELECT pg_sleep(900);"` — no: to be *idle* in transaction, use `psql` fed from a FIFO/heredoc that runs `BEGIN; UPDATE … ;` then `\! sleep 900` (psql's shell escape keeps the session idle in transaction), and two background `psql -c "UPDATE orders SET status='cancelled' WHERE id=1"` and `-c "SELECT * FROM orders WHERE id=1 FOR UPDATE"` sessions with `application_name` set via `PGAPPNAME` (`checkout-api`, `fulfilment-worker`); `wait`; loop.
- design.md §18: decisions worth keeping (read-only guard reasoning and its limit, audited reads, server-not-database connection model, stale snapshot, confirmation overload, Testcontainers exception, `application_name` own-row detection and refusal, OID-not-name for maintenance targets).

- [ ] Verify: `docker compose --profile postgres up -d`, wait healthy, `docker compose exec postgres psql -U sbconsole -d app -c "select count(*) from orders"` → 200000; `psql … -c "select pid,state from pg_stat_activity where application_name='checkout-api'"` shows idle in transaction + waiting rows. Run the app (`dotnet run --project src/SbConsole.Web --launch-profile http` with `SBC_DEMO_CONNECTIONS` pointing at a local copy with Host `localhost`, Port 5433) and check each page in the browser.
- [ ] Add a `DemoConnectionsFileTests` case if that test enumerates kinds (check `tests/SbConsole.Web.Tests/DemoConnectionsStartupTests.cs` and wherever `DemoConnectionsFileTests` lives; pin the pg secrets parse via `PostgresConnectionSettings.Parse`).
- [ ] Full gate → commit `feat(postgres): docker demo stack, demo connections, docs`.

---

## Self-review notes

- Spec §3–§15 each map to tasks: §3.1 → T1/T2, §3.2–3.3 → T3/T9, §4 → T4–T6, §5 → T7/T8/T14, §6 → T9–T11, §7 → T12/T14, §8 → T15,
  §9 → T6, §10 → T13, §11 → T5, §12 → every task, §14 → T16.
- Index REINDEX/Drop moved from Phase 2 to Task 14 because they need Task 13's confirmation overload.
