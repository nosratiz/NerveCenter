# SQLite plugin — Design

Status: approved, 2026-10-13.

## 1. Context

SbConsole's sixth plugin. Like Redis (`2026-09-28-redis-plugin-design.md`) it
targets something that is not a broker — and unlike every plugin so far, not a
server either. A SQLite "connection" is a **file on the SbConsole host**, so
the plugin is organised around what that implies (mockup
`SbConsole SQLite.dc.html`, screens 1a–1g):

1. A **file** with a size, free pages, a journal mode and a last-modified time
   (Overview, 1a).
2. A **schema** browsed table by table, with row counts that are honest about
   being estimates on big tables (Tables, 1b).
3. A **query runner** that is read-only by default and makes you flip a switch
   to write (Query, 1c/1d).
4. **Backups** — `VACUUM INTO`, schedule, history, verify, restore (1e).

There is no namespace/vhost/database picker: the connection *is* the file, so
the page picker shows the file path and its mode.

The whole surface ships in three phases, each its own plan:

- **Phase 1** (this spec's detailed scope, `docs/superpowers/plans/2026-10-13-sqlite-plugin.md`):
  plugin scaffold, connection form + Test, host path policy, read-only
  Overview (1a minus history-backed tiles), read-only Tables browser (1b) with
  exact count and Export CSV, dashboard integration, local dev seed.
- **Phase 2**: Query runner (1c) with inline `EXPLAIN QUERY PLAN`, write mode
  (1d) with prod typed-confirm, dry-run count, single transaction and audit;
  table Rename / Drop / Create (1f dialogs).
- **Phase 3**: Backups (1e) — `VACUUM INTO`, schedule, history, verify,
  retention/prune, open-read-only, restore; the 30-day growth sampler and
  "Last backup" tile on the Overview; `REINDEX` / `.recover` actions on the
  corrupt state (1g).

## 2. Decisions

**Driver: `Microsoft.Data.Sqlite`**, referenced directly by the plugin project
(plugins may take their own client package, as Redis takes
`StackExchange.Redis`). Core already pulls it in through
`Microsoft.EntityFrameworkCore.Sqlite`, so the native `e_sqlite3` bundle and
version are already pinned in the solution; the plugin pins the same
`Microsoft.Data.Sqlite` version. Read-only is a real connection flag
(`Mode=ReadOnly`), not a regex on SQL.

Raw `SQLitePCLRaw` (for `sqlite3_db_status`) was rejected: the only thing it
would buy is the mockup's "page cache hit %" stat, which is dropped instead.

**Path allow-list via a new SDK seam, `IHostPathPolicy`.** A SQLite
connection is a server-side path, so anyone who can save a connection could
otherwise open any file the container user can read — including SbConsole's
own database (`SBC_DB_PATH`, which holds the encrypted secrets). The policy
lives in Core, not the plugin, so the plugin never learns host env var names
and Core alone knows where its own database is. It **fails closed**: with no
roots configured, no SQLite connection opens.

**Phase 1 is strictly read-only.** Every connection opens with
`Mode=ReadOnly` regardless of the stored mode; the stored `Mode` exists so
phase 2 needs no secret-format migration.

**Extensions are never loaded.** The mockup's "Load extensions" field is
dropped permanently — `load_extension` means running native code from a
user-supplied path.

## 3. SDK addition — `IHostPathPolicy`

```csharp
namespace SbConsole.Sdk;

public interface IHostPathPolicy
{
    PathCheckResult Check(string path);
}

public sealed record PathCheckResult(bool Allowed, string? ResolvedPath, string? Reason);
```

Core implementation `HostPathPolicy` (`ISingletonService`):

1. Reads allowed roots from `SBC_SQLITE_ROOTS` (`;`-separated). Empty or unset
   → every path refused with Reason
   `"No SQLite roots are configured — set SBC_SQLITE_ROOTS."`.
2. Resolves the candidate to a full path (`Path.GetFullPath`, so `..` is
   collapsed), then resolves symlinks on the file **and every parent
   directory** (`FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`
   walked component by component). Roots are resolved the same way.
3. Allowed only if the resolved path is under a resolved root by
   **separator-aware** prefix match (`/data2/x` is not under `/data`).
   Comparison is ordinal (case-sensitive) — the host is Linux in production.
4. Always refuses SbConsole's own database file and its `-wal` / `-shm` /
   `-journal` siblings (resolved the same way), even if they sit under a root.
5. Returns `ResolvedPath`; callers open **that** path, not the original, to
   narrow the check-then-open window.

Documented in `launchSettings.json.example`, `.env.example`,
`docker/README.md`, and a dated `docs/design.md` section.

## 4. Plugin shape

```
src/Plugins/SbConsole.Plugins.Sqlite/
  SqlitePlugin.cs                Id "sqlite", Monogram "SQ", ConnectionKind "sqlite",
                                 nav: Overview (/p/sqlite/overview), Tables (/p/sqlite/tables)
  HandlerRunner.cs, PluginResult.cs   same pattern as Redis
  Client/
    ISqliteOperations.cs         thin wrapper every page/handler goes through
    SqliteOperations.cs          Microsoft.Data.Sqlite implementation (ISingletonService)
    SqliteConnectionSettings.cs  secret parse/format
    SqliteConnectionFields.razor custom connection form (1f)
    FriendlySqliteError.cs       SqliteException → friendly text (§7)
    (result records: SqliteOverview, TableSize, SchemaObject, TableDetail,
     ColumnInfo, IndexInfo, ForeignKeyInfo, ColumnNote, RowPage, IntegrityResult)
  Overview/  GetOverviewQueryHandler, GetTableSizesQueryHandler,
             RunIntegrityCheckCommandHandler
  Tables/    ListSchemaQueryHandler, GetTableDetailQueryHandler,
             GetColumnNotesQueryHandler, CountRowsQueryHandler,
             PreviewRowsQueryHandler, ExportTableCsvCommandHandler
  Pages/     Overview.razor, Tables.razor
  Components/ SqlitePageHeader (connection picker showing file path + mode),
              SqliteStatTile
tests/SbConsole.Plugins.Sqlite.Tests/
```

All handlers use the `IScopedService` marker; `SqliteOperations` uses
`ISingletonService`; `ConfigureServices` calls `AddMarkedServices` on its own
assembly. Theming only through `--mud-palette-*` tokens.

### 4.1 Secret format and connection form

House `key=value;` convention (as Kafka / RabbitMQ / AWS):

```
Path=/data/reports.db;Mode=ReadOnly;BusyTimeoutMs=5000;StatementTimeoutS=30
```

- Defaults: `Mode=ReadOnly`, `BusyTimeoutMs=5000`, `StatementTimeoutS=30`.
  `Path` is required. Unknown keys are ignored; malformed numbers →
  `FormatException`.
- Nothing in it is credential-shaped, but it is stored through the normal
  encrypted secret path so the host needs no special case.
- `GetConnectionSummary` → chips `File` (basename only) and `Mode`; never
  throws (unparsable → empty).
- `SqliteConnectionFields.razor`: File path, Default mode (Read-write option
  shown disabled with "available with write mode (phase 2)"), Busy timeout,
  Statement timeout, plus the hint "Path is resolved inside the SbConsole
  container — mount the volume there." No backup-target field until phase 3.

### 4.2 Opening a connection

Every `SqliteOperations` call:

1. Parses the secret, runs `IHostPathPolicy.Check(Path)`; refusal →
   `SqlitePathRefusedException(reason)` (mapped in §7).
2. Opens `Data Source=<ResolvedPath>;Mode=ReadOnly;Pooling=False` — read-only
   mode never creates a missing file.
3. Sets `PRAGMA busy_timeout = <BusyTimeoutMs>`.
4. Runs each command under a `CancellationTokenSource` linked to the caller's
   token with `CancelAfter(StatementTimeoutS)`. Microsoft.Data.Sqlite wires
   cancellation to `sqlite3_interrupt`, so a long scan actually stops. A
   cancellation caused by the timeout (not the caller) becomes
   `SqliteStatementTimeoutException`.

`TestConnectionAsync` reports `ConnectionCheck` rows: "Path allowed",
"File opened" (with SQLite version, size, journal mode), "Schema readable"
(table count). `Identity` = `"SQLite <version> · <size> · <journal_mode>"`.

## 5. Overview page (1a)

Route `/p/sqlite/overview`. Header: connection picker + `path · version ·
journal mode`, "PRAGMA Ns ago", **Refresh**.

**`GetOverviewQueryHandler`** (one connection, read-only PRAGMAs) →
`SqliteOverview`:

| Source | Fields |
|---|---|
| `FileInfo` on resolved path | file size, last modified; `-wal` size if present |
| PRAGMAs | `page_size`, `page_count`, `freelist_count`, `journal_mode`, `synchronous`, `wal_autocheckpoint`, `cache_size`, `busy_timeout`, `foreign_keys` |
| `sqlite_version()` | version |

Tiles:

- **File** (wide): size + `-wal` size; page size × page count; free pages
  drawn inside the size bar, with "N% free pages (X MB) — VACUUM would reclaim
  them" (informational, no button in phase 1); last modified.
- **Journal**: journal mode, `synchronous`, checkpoint every N pages.
- **Page cache**: `cache_size` rendered as pages and bytes (negative
  `cache_size` means KiB, per SQLite). No hit ratio.
- **Settings strip**: `busy_timeout`, `foreign_keys`.

**`GetTableSizesQueryHandler`** — loaded separately so the tiles never wait
on it: `SELECT name, SUM(pgsize) FROM dbstat GROUP BY name` (full b-tree walk;
runs under the statement timeout). Joined to `sqlite_schema` (table vs index)
and `sqlite_stat1` (row count ≈). Panel shows the top 4 objects + "N more",
header "dbstat · N tables, M indexes". If `dbstat` is absent ("no such
table: dbstat") the panel says "dbstat isn't compiled into this SQLite
build" — verify during implementation whether the bundled `e_sqlite3` has it.

**Integrity strip** — `RunIntegrityCheckCommandHandler`, on demand only
(**Run now**), `ActionRisk.Safe`, audited as `sqlite.integrity.check`. Runs
`PRAGMA integrity_check` and `PRAGMA foreign_key_check` under the statement
timeout. Result (`ok`/errors, first 20 lines, FK violation count, ran-at,
duration) stored per connection in `IPluginStore` under
`integrity:<connectionId>`, so the strip shows "ok · ran 09:12 in 41 s ·
foreign_key_check 0 violations" across visits, or "never run". On failure it
renders the 1g error list (no REINDEX / `.recover` in phase 1).

**Dropped from 1a in phase 1:** 30-day growth and Last backup (phase 3, need a
sampler/scheduler); page-cache hit % and "N readers · 1 writer" (SQLite does
not expose them through this driver — permanently dropped).

## 6. Tables page (1b)

Route `/p/sqlite/tables`, optional `?table=<name>`.

**Left list — `ListSchemaQueryHandler`**: `sqlite_schema` grouped into Tables
/ Views (indexes and triggers counted in the summary line "N tables · M
indexes · V views · T triggers · from sqlite_schema"). Internal `sqlite_%`
objects excluded. Row counts from `sqlite_stat1` (first integer of `stat` for
the table's rows) marked **≈**; no stats → "—". Sizes from the same `dbstat`
query as §5 when available. Client-side filter box. **Never** runs `COUNT(*)`
by default; footnote explains why.

**Detail header**: name, "N columns · M indexes · WITHOUT ROWID yes/no · rows
≈", **count exactly**, **Export CSV**. (Query / Rename / Drop / Create table
buttons are not rendered until phase 2.)

**`GetTableDetailQueryHandler`** → tabs:

- **Columns**: `pragma_table_xinfo` (name, type, notnull, default, pk;
  hidden/generated flagged).
- **Indexes**: `pragma_index_list` + `pragma_index_info` (name, unique,
  origin, partial, columns).
- **Foreign keys**: `pragma_foreign_key_list` (from → table.column, on
  update/delete).
- **DDL**: `sqlite_schema.sql`, monospace.
- **Data**: preview grid (below).

**Notes column — `GetColumnNotesQueryHandler`**, a separate call so the
columns render first. Schema-derived notes: "alias of rowid" (INTEGER PRIMARY
KEY on a rowid table), "→ table.col · ON DELETE X", "indexed · idx_name".
Sample-derived notes (sample = first 1,000 rows by rowid, or by PK for WITHOUT
ROWID; under the statement timeout), each labelled "sampled":
"JSON in N% of sampled rows" (TEXT columns, `json_valid`), "N distinct ≈",
"unix ms?" for INTEGER values all within 2000-01-01..2100-01-01 as epoch-ms.
Values are never altered.

**Data tab — `PreviewRowsQueryHandler`**: `SELECT * FROM "<name>" ORDER BY
rowid DESC LIMIT 50` (PK order for WITHOUT ROWID; no ORDER BY for views). The
name must exist in `sqlite_schema` and is quoted as an identifier (`"` doubled)
— user input is never interpolated otherwise. BLOB → "BLOB · N bytes"; text
truncated to 200 chars; NULL shown distinctly. Shows the generated SQL as a
caption. "Open in Query →" hidden until phase 2.

**Count exactly — `CountRowsQueryHandler`**: `SELECT COUNT(*)` under the
statement timeout, with a Cancel button wired to the page's token.

**Export CSV — `ExportTableCsvCommandHandler`**, `ActionRisk.Safe`, audited as
`sqlite.table.export` (data leaves the server). RFC 4180 quoting; BLOBs as
base64. Delivery mechanism is decided in planning: preferred is a streamed
download (hard cap 1,000,000 rows) via a plugin endpoint behind the UI's
cookie auth; **if the host has no plugin-endpoint seam, fall back to a
100,000-row cap delivered via `IJSRuntime`** rather than adding a new SDK seam
in phase 1.

## 7. Errors

`FriendlySqliteError` maps by `SqliteException.SqliteErrorCode` (primary
code), and every handler routes through it and the shared `FriendlyError`
helper — raw exception text never reaches the UI or DB; full exception logged
server-side.

| Cause | Message | UI actions |
|---|---|---|
| `SQLITE_BUSY` (5) | "Database is locked — still busy after N ms. Another writer holds the lock." | Retry, Connection settings |
| `SQLITE_CORRUPT` (11), `SQLITE_NOTADB` (26) | "The file is not a valid or healthy SQLite database." | — |
| `SQLITE_CANTOPEN` (14) | "File not found, or not readable by the SbConsole container user." | Connection settings |
| `SQLITE_READONLY` (8) | "This connection is read-only." | — |
| `SqlitePathRefusedException` | the policy's Reason | Connection settings |
| `SqliteStatementTimeoutException` | "Statement exceeded the N s statement timeout." | Retry |

The 1g "holder is most likely <process>" hint is dropped — not knowable from
SQLite.

## 8. Host integration

- `Program.cs`: `AddSbConsolePlugin<SqlitePlugin>()`.
- `HostPathPolicy` registered by marker; `ProgramDiRegistrationTests` covers it.
- `Contribution => new(PageCount: 2, ActionCount: 2)` (integrity check, CSV export).
- `GetDashboardMetricsAsync`: `"Databases" = 1` per connection (sums correctly).
- `GetDashboardProblemsAsync`: Warning "Integrity check failed" when the stored
  last result (§5) is not ok; Info "N% free pages — VACUUM would reclaim X"
  when `freelist_count / page_count ≥ 20%`. Links to `/p/sqlite/overview`.
- No nav badges; no oldest-dead-letter (SDK defaults).
- `docs/design.md`: dated section on the path-policy seam, fail-closed
  rationale, and the temp-file test exception (§9).

**Local dev stack:** `docker/sqlite/seed.py` builds a ~50 MB demo
`reports.db` (tenants, sessions, events with JSON `props`, event_props with an
FK, an index on `events.ts`, a view, `ANALYZE` run so `sqlite_stat1` exists).
Mounted at `/data` for the `app` profile with `SBC_SQLITE_ROOTS=/data`.
Documented in `docker/README.md`.

## 9. Testing

TDD per task, gate `dotnet build -warnaserror` + `dotnet test`.

- **Plugin handlers** (xUnit + FluentAssertions + NSubstitute): substitute
  `ISqliteOperations`, `IConnectionProvider`, `IAuditScope`, `IPluginStore`.
  Cover success/failure paths, audit rows for the two commands, stored
  integrity result, dashboard problem thresholds, settings parse/format
  round-trip and defaults, summary chips never throwing.
- **`SqliteOperations`** — the one deliberate exception to "no real engine in
  plugin tests": tests run against a **temp-file SQLite database created per
  test** (no Docker, no network). This is the only honest way to verify PRAGMA
  parsing, `dbstat` / `sqlite_stat1` handling, notes sampling, identifier
  quoting, `Mode=ReadOnly` (a write fails with `SQLITE_READONLY`), missing file
  not being created, and timeout → interrupt. `IHostPathPolicy` substituted to
  allow the temp dir.
- **Core `HostPathPolicy`**: inside/outside root, `/data2` vs `/data` prefix
  trap, `..` traversal, symlinked file and symlinked parent escaping a root,
  own DB + `-wal`/`-shm`/`-journal` refused, no roots configured → refused.
- **bUnit**: Overview tiles; dbstat-unavailable fallback; integrity ok /
  failed / never-run strip; Tables list grouping, ≈ markers, filter; detail
  tabs; preview grid rendering of NULL/BLOB/long text; connection form
  round-trip with the disabled Read-write option.

## 10. Phase 1 non-goals

Query runner, EXPLAIN, write mode, Rename/Drop/Create table, VACUUM, backups,
growth history, Last backup tile, REINDEX/`.recover`, loading extensions,
creating new database files.
