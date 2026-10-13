# SQLite plugin — Design

Status: approved, 2026-10-13. Revised during planning the same day (plugin
initialization hook, dbstat absent from the bundled build, raw interrupt for
timeouts, JS-stream CSV download, problem severities, `/sqlite` demo mount).

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

**Plugins get one post-build initialization hook.** The host constructs each
plugin with `new()` (`AddSbConsolePlugin<TPlugin>`), so the plugin-instance
hooks (`TestConnectionAsync`, `GetDashboardProblemsAsync`, …) have no DI
access — the existing plugins `new` up their operations class, which works
only because none of them need a host service. SQLite needs
`IHostPathPolicy` there, so the SDK gains an optional default-no-op
`IPlugin.Initialize(IServiceProvider services)`, called once for every plugin
right after `app.Build()` (via `PluginRegistry.InitializeAll`). `SqlitePlugin`
resolves its `ISqliteOperations` there; until then its hooks fail closed
("SQLite plugin is not initialized").

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

1. Reads allowed roots from `BootstrapOptions.SqliteRoots`, which
   `BootstrapOptions.FromEnvironment` fills from `SBC_SQLITE_ROOTS`
   (`;`-separated, blanks dropped) — BootstrapOptions stays the only env reader. Empty or unset
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
   token with `CancelAfter(StatementTimeoutS)`, whose registration calls
   `SQLitePCL.raw.sqlite3_interrupt(connection.Handle)` directly. **Verified
   2026-10-13 against Microsoft.Data.Sqlite 10.0.12:** passing a token to
   `ExecuteReaderAsync`, or `SqliteCommand.Cancel()`, does *not* stop a
   running statement; the raw interrupt does (SQLITE_INTERRUPT, code 9, in
   ~300 ms, connection still usable). An interrupt caused by the timeout
   becomes `SqliteStatementTimeoutException`; one caused by the caller's token
   becomes `OperationCanceledException`. An interrupt that fires while no
   statement is running is lost, so `Run` also installs
   `sqlite3_progress_handler` (every 1000 VM instructions; aborts when the
   linked token is cancelled) and checks the token before running the work.
   Both abort with SQLITE_INTERRUPT (code 9).

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
| PRAGMAs | `page_size`, `page_count`, `freelist_count`, `journal_mode`, `synchronous`, `wal_autocheckpoint`, `cache_size`, `busy_timeout` |
| `sqlite_version()` | version |

Tiles:

- **File** (wide): size + `-wal` size; page size × page count; free pages
  drawn inside the size bar, with "N% free pages (X MB) — VACUUM would reclaim
  them" (informational, no button in phase 1); last modified.
- **Journal**: journal mode, `synchronous`, checkpoint every N pages.
- **Page cache**: `cache_size` rendered as pages and bytes (negative
  `cache_size` means KiB, per SQLite). No hit ratio.
- **Settings strip**: `busy_timeout` is read from the connection; `foreign_keys` is static copy
  ("per-connection · on for this console's connections"), because it is a per-connection setting
  that is always ON for this console's connections, so reading it back would add nothing. The
  `foreign_keys` PRAGMA is not read.

**`GetTableSizesQueryHandler`** — loaded separately so the tiles never wait
on it: `SELECT name, SUM(pgsize) FROM dbstat GROUP BY name` (full b-tree walk;
runs under the statement timeout). Joined to `sqlite_schema` (table vs index)
and `sqlite_stat1` (row count ≈). Panel shows the top 4 objects + "N more",
header "dbstat · N tables, M indexes". **Verified 2026-10-13: the bundled
`e_sqlite3` (SQLite 3.53.3) is built without `dbstat`**, so the fallback is
the normal case: on "no such table: dbstat" the panel becomes **Largest
tables** ranked by `sqlite_stat1` rows ≈ (tables without stats listed last
with "—"), with the sub-caption "sizes need dbstat, which this SQLite build
doesn't include". Sizes appear automatically if a future build has it.

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
query as §5 when available; the Size column is omitted otherwise. Client-side filter box. **Never** runs `COUNT(*)`
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
base64. **Decided in planning:** the host has no plugin-endpoint seam, so
the CSV is built server-side (hard cap 100,000 rows or 50 MB, `CsvExport.MaxBytes`,
whichever comes first, written into a `MemoryStream`; the footer and audit detail
say which cap truncated it) and handed to the browser through a `DotNetStreamReference`
and a tiny plugin JS module (`wwwroot/sqlite.js`, `downloadFromStream`), no
new SDK seam.

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
- `GetDashboardProblemsAsync` (`PluginDashboardProblem.Severity` only supports
  "Error" / "Warning"): Error "Integrity check failed" when the stored last
  result (§5) is not ok; Warning "N% free pages — VACUUM would reclaim X" when
  `freelist_count / page_count ≥ 20%`; Warning "Foreign key violations" when
  the stored integrity check found any. Links to `/p/sqlite/overview`.
- No nav badges; no oldest-dead-letter (SDK defaults).
- `docs/design.md`: dated section on the path-policy seam, fail-closed
  rationale, and the temp-file test exception (§9).

**Local dev stack:** `docker/sqlite/seed.py` builds a ~34 MB demo
`reports.db` (tenants, sessions, events with JSON `props`, event_props with an
FK, an index on `events.ts`, a view, `ANALYZE` run so `sqlite_stat1` exists).
Mounted at `/sqlite` for the `app` profile with
`SBC_SQLITE_ROOTS=/sqlite` — deliberately **not** `/data`, which holds
SbConsole's own database and data-protection keys.
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

## Implementation notes (2026-10-13)

- **Statement timeout** needs the progress handler as well as `sqlite3_interrupt` (§4.2).
- **foreign_keys** is ON by default for Microsoft.Data.Sqlite / bundled `e_sqlite3`
  connections, unlike the SQLite CLI; the UI copy reads "per-connection · on for this
  console's connections". The seed script uses Python's `sqlite3` (off by default), so it
  enables FKs before its cascading delete.
- **CSV export** cap is 100,000 rows or 50 MB, whichever comes first (§6).
- **Dashboard problems** gained a Warning "Foreign key violations" (§8 listed two).
- **Connection form** shows field-level validation errors (empty path, `;` in the path,
  timeouts below 1) instead of silently keeping the last good secret.
- **Known limitation:** the path check is check-then-open. Opening the *resolved* path removes
  symlinks that existed at check time, but someone who can write under a root can still race a new
  symlink in between the check and the open.
- **Operational rule:** roots must never contain SbConsole's own data directory (the folder of
  `SBC_DB_PATH`, which also holds the data-protection keys).
- **Hardening:** every connection also runs `PRAGMA trusted_schema = OFF`, so a hostile schema in
  an untrusted file can't run SQL functions from views, triggers or defaults.
- **Known limitation:** a hard link to SbConsole's own database placed under a root is not
  detected (the policy compares resolved paths, not file identity); it needs write access
  to both locations.
- The demo file is ~34 MB, not 50 MB, and the compose volume is writable (SQLite needs to
  create `-shm` beside a WAL file even for read-only connections).

## 11. Phases 2 and 3 — design (2026-10-13)

Approved scope: everything in §1 phases 2 and 3, built on `feat/sqlite-plugin` after phase 1.
Verified the same day against Microsoft.Data.Sqlite 10.0.12 / bundled e_sqlite3 (throwaway probe):
`VACUUM INTO @param` works on a `Mode=ReadOnly` connection; `sqlite3_limit(SQLITE_LIMIT_ATTACHED, 0)`
makes `ATTACH`, `VACUUM INTO` and plain `VACUUM` fail with "too many attached databases"; a
`DELETE` with `ON DELETE CASCADE` children (rowid and WITHOUT ROWID) gives `changes()` = direct
rows only and a `total_changes()` delta that includes every cascaded row; `sqlite3_complete`
treats a trigger body's inner `;` correctly; `SqliteConnection.BackupDatabase` (online backup API)
copies a file into a live read-write connection.

### 11.1 Connection guard (applies to every user-SQL connection)

- **ATTACH is impossible from user SQL.** Every connection that runs user-supplied SQL (Query
  runner, write mode, Create table) sets `SQLITE_LIMIT_ATTACHED = 0` right after open. Without it,
  `ATTACH '/any/file'` or `VACUUM INTO '/any/path'` would bypass `IHostPathPolicy` entirely (VACUUM
  INTO even works read-only). Plugin-owned connections that need VACUUM INTO (backups) don't set
  the limit, and they never run user SQL.
- **Write mode needs `Mode=ReadWrite` on the saved connection.** The connection form's Read-write
  option is now enabled. Write-capable operations take an explicit `writable` flag; the handler
  re-reads `Mode` from the decrypted secret and refuses with "This connection is read-only — set
  Default mode to Read-write in Connections." if it isn't ReadWrite. Opened as
  `SqliteOpenMode.ReadWrite`, never `ReadWriteCreate` (a missing file is never created).
- Read-only remains the default for everything, including the Query page on a read-write connection.

### 11.2 Statement splitting — `SqliteScript`

User scripts are split into statements by a small tokenizer (single/double/backtick/bracket quotes,
`--` and `/* */` comments) that cuts at a `;` outside quotes/comments **only when
`raw.sqlite3_complete` agrees** the text so far is complete (handles `CREATE TRIGGER … BEGIN …; END;`).
Each statement records its leading keyword (after comments). Refused anywhere in a script, with a
friendly message, before anything runs: `ATTACH`, `DETACH`, `VACUUM` (use Backups), and transaction
control (`BEGIN`, `COMMIT`, `END`, `ROLLBACK`, `SAVEPOINT`, `RELEASE`) — the console owns the
transaction. Empty scripts are refused.

### 11.3 Query page (1c) — `/p/sqlite/query`

Nav item "Query". Header: connection picker, Read-only / Write segmented switch, meta
"opened with mode=ro · statement timeout N s" (write mode: tinted header, "write mode · every
statement runs inside a transaction and is audited"). Query params: `connectionId`, `sql`
(prefill, e.g. from Tables "Query" / "Open in Query →"), `backup` (§11.7 open read-only).

- **Run** (`RunQueryQueryHandler`, query, not audited — same data the Tables preview shows): runs
  every statement of the script on a read-only connection; the grid shows the **last statement that
  returned columns**. Reads up to `limit` rows (100, "Load 100 more" re-runs with +100), keeps
  counting further rows without materialising them up to 100,000 ("100,000+ rows"). Result:
  columns (name + declared type), rows (same cell rendering as the Tables preview: NULL, BLOB · N
  bytes, text truncated to 200 chars, full value as tooltip), total rows (or capped), duration.
  INTEGER columns whose values all look like unix-ms (same range rule as column notes) get a date
  hint under the value; the value is never changed. Ctrl/⌘+Enter runs.
- **Explain** (`ExplainQueryQueryHandler`): `EXPLAIN QUERY PLAN` of the **first** statement only
  (never the rest of the script) on a read-only connection; rendered as an indented tree from
  `id`/`parent`/`detail`, beside the editor. `SCAN` lines on a table are flagged (warning tint).
- **Grid / JSON / Chart** views of the current result: JSON = array of objects; Chart = MudChart
  bar chart of the first text column (labels) vs the first numeric column (≤ 50 rows), hidden when
  the result has no such pair.
- **Export CSV** of the current query (`ExportQueryCsvCommandHandler`, Safe, audited
  `sqlite.query.export`) — re-runs the script read-only with the same 100k rows / 50 MB caps as
  table export, same JS stream download.
- **Saved queries** (left panel): global list in `IPluginStore` under `saved-queries` (JSON array
  of `{Id, Name, Sql, SavedAt}`), Save (name prompt), click to load, delete (×). Not per
  connection — saved SQL is usually portable across copies of the same file.

### 11.4 Write mode (1d)

- **Switching to Write**: disabled (with the reason as tooltip) unless the connection's summary
  Mode is read-write. On a prod connection it calls `IConfirmationService.ConfirmAsync("Enable
  write mode on", name, isProd: true)` (typed confirm from the host). `IsProd` always from the
  server-side `ConnectionInfo` the picker loaded. Switching connection resets to Read-only.
- **Dry run** (`DryRunWriteQueryHandler` — a query: it changes nothing durable and writes no audit
  row): `BEGIN IMMEDIATE`, run every statement, record per statement `changes()` and the
  `total_changes()` delta, then **always `ROLLBACK`**. Report: per-statement direct rows, rows
  changed by triggers / FK cascades (total − direct), duration, and the note "freed pages stay in
  the file until VACUUM". This is more honest than a `COUNT(*)` rewrite: it is the real statement,
  works for any DML/DDL, and resolves cascades; the cost (it does the work twice) is stated in the
  UI. Busy/timeout errors map as usual.
- **Run in transaction** (`RunWriteCommandHandler`, `ActionRisk.Destructive`, audited
  `sqlite.query.write`): requires a dry run of the *same* script text first (the button is
  disabled until then; the page tracks the dry-run's SQL); on prod confirms via
  `ConfirmAsync("Run write script on", name, true, count: totalRowsFromDryRun)`. Executes inside
  one `BEGIN IMMEDIATE … COMMIT`; any error → `ROLLBACK` and nothing is kept. Audit detail:
  `sha256:<first 12 hex> · <direct> rows + <cascaded> cascaded · <duration> · <first 200 chars of SQL>`.
  Result panel: committed, rows per statement, cascaded rows, duration, `-wal` size and free-page
  % after; actions **Checkpoint WAL** (`CheckpointWalCommandHandler`, Mutating, audited
  `sqlite.wal.checkpoint`, `PRAGMA wal_checkpoint(TRUNCATE)`, shows busy/log/checkpointed) and
  **Back to read-only**.
- The statement timeout applies; an interrupted write rolls back.

### 11.5 Tables page DDL (1b, 1f, 1g)

Header buttons (only for a read-write connection; otherwise rendered disabled with the reason):
**Query** (→ Query page prefilled `SELECT * FROM "<t>" LIMIT 100`), **Rename**, **Drop**, and
**+ Create table…** above the list. Data tab gets **Open in Query →** with the preview SQL.

- **Create** (`CreateTableCommandHandler`, Mutating, audited `sqlite.table.create`): dialog with a
  DDL editor prefilled with a template; the script must be exactly one `CREATE TABLE` statement
  (§11.2 splitter). Runs in a transaction on a writable, attach-limited connection. Selects the
  new table afterwards.
- **Rename** (`RenameTableCommandHandler`, Mutating, audited `sqlite.table.rename`): new-name
  field; `ALTER TABLE "<old>" RENAME TO "<new>"` with both quoted; the old name must exist in
  `sqlite_schema` as a table; new name non-empty, ≤ 128 chars, not `sqlite_`-prefixed, not taken.
  Prod → `ConfirmAsync("Rename table", old, true)`.
- **Drop** (`GetDropImpactQueryHandler` + `DropTableCommandHandler`, Destructive, audited
  `sqlite.table.drop`): the dialog shows rows ≈ (stat1, else "unknown"), index count, and every
  table whose foreign keys reference it with its ON DELETE action — and what will happen given
  that this console's connections run with `foreign_keys = ON`: CASCADE → "rows there are deleted
  too"; SET NULL/SET DEFAULT → "their reference is cleared"; NO ACTION/RESTRICT → "the drop fails
  if any rows reference it". Checkbox **Take a backup first** (when the connection has a backup
  directory; runs §11.7 Back up now and aborts the drop if it fails). Then
  `ConfirmAsync("Drop table", name, isProd)` (typed for prod). `DROP TABLE "<t>"` in a transaction.
- **Empty file** (1g): no tables → "No tables in <path>. The file is N bytes — SQLite creates
  pages on first write." with Create table… and **Run script** (→ Query page) when writable.

### 11.6 Corrupt state (1g) — REINDEX and recover

On a failed integrity check the Overview strip offers, for read-write connections:
**REINDEX** (`ReindexCommandHandler`, Mutating, audited `sqlite.reindex`, runs `REINDEX` then a
fresh integrity check) and **Recover into new file** (`RecoverCommandHandler`, Mutating, audited
`sqlite.recover`; needs a backup directory). The bundled build has no `.recover`
(`sqlite3_recover` is a CLI extension), so this is a best-effort salvage: create
`<backupdir>/<base>-recovered-<yyyyMMdd-HHmmss>.db`, copy every table's DDL then its rows one by
one, skipping rows/tables that error, then indexes/views/triggers; report tables copied, rows
copied, tables failed. The live file is never modified. Prod → typed confirm for REINDEX.

### 11.7 Backups (1e) — `/p/sqlite/backups`

- **Backup directory** is a new optional secret key `BackupDir=` ("Enable backups to" in the
  connection form). It must pass `IHostPathPolicy` like the file itself (so it lives under
  `SBC_SQLITE_ROOTS` — strictly inside a root: the policy refuses a root itself) and may be the
  live file's own directory; files are named `<base>-<yyyyMMdd-HHmmss>.db`. No backup dir → the Backups page explains how to enable it.
- **Back up now** (`RunBackupCommandHandler`, Safe, audited `sqlite.backup.create`, optional
  note): `VACUUM INTO @path` on a read-only, **not** attach-limited plugin-owned connection, under
  a 30-minute timeout (not the statement timeout). Then verify (below). History record stored
  even on failure (with the friendly error), so the table shows failed rows.
- **Verify** (`VerifyBackupCommandHandler`, Safe, audited `sqlite.backup.verify`; also run after
  every backup): opens the copy read-only → `integrity_check`, SHA-256 of the file, table count,
  total rows ≈ (stat1), schema fingerprint (sorted `type|name|sql` of `sqlite_schema`). Detail
  panel compares with the live file: size delta, rows ≈ delta, schema identical / N objects differ.
- **History** in `IPluginStore` `backups:<connectionId>`: JSON array of
  `{Id, FileName, TakenAt, Bytes, DurationMs, Trigger ("manual"|"schedule"), Actor, Note,
  Status ("ok"|"failed"), Error, Verify ("ok"|"failed"|"unverified"), VerifyDetail, Sha256,
  Tables, RowsApprox, SchemaFingerprint, VerifiedAt}`, newest first, capped at 200 records.
- **Schedule…** (`SaveBackupPolicyCommandHandler`, Mutating, audited `sqlite.backup.policy`):
  per connection in `backup-policy:<connectionId>`: `{Enabled, DailyAtUtc ("HH:mm"), KeepDaily 7,
  KeepWeekly 4, KeepMonthly 3}`.
- **Scheduler** `SqliteBackupScheduler : BackgroundService` (hand-registered with
  `AddHostedService` in the plugin's `ConfigureServices`), ticks every minute (`TimeProvider`),
  public `TickAsync(now)` for tests. For each SQLite connection with a backup dir and an enabled
  policy: if today's slot `DailyAtUtc` has passed and there's no scheduled record at/after it,
  take a backup (trigger "schedule", actor "system"), verify, then prune. Errors are recorded in
  history and logged; a failed tick never stops later ticks. The same tick samples **growth**
  (§11.8) at most once an hour per connection.
- **Background audit**: `EfAuditScope` falls back to actor `system` when there is no
  authentication state (background scope) instead of throwing.
- **Retention / Prune** (`PruneBackupsCommandHandler`, Destructive, audited
  `sqlite.backup.prune`): grandfather-father-son over successful backups: keep the newest per UTC
  day for the last KeepDaily days, newest per ISO week for KeepWeekly weeks, newest per month for
  KeepMonthly months; failed records older than KeepDaily days are dropped from history; the newest
  successful backup is always kept. The page shows "next prune removes N". Only files named in the
  history, inside the backup dir (re-checked through `IHostPathPolicy`), are deleted.
  Manual prune confirms via `ConfirmAsync("Delete backups of", name, isProd, count)`; the
  scheduler prunes without confirmation (the policy is the consent).
- **Download** (`DownloadBackupCommandHandler`, Safe, audited `sqlite.backup.download`): streams
  the file through the existing JS stream download (fine for the demo sizes; very large files are
  better fetched from the volume directly — stated in the UI over 1 GB).
- **Open read-only** → `/p/sqlite/query?connectionId=…&backup=<id>`: the Query page runs
  read-only queries against that backup file instead of the live file (banner says so; write mode
  unavailable). No temporary host connection is created — the SDK has no seam for that.
- **Restore…** (`RestoreBackupCommandHandler`, Destructive, audited `sqlite.backup.restore`;
  needs Mode=ReadWrite): dialog states the live path, the backup, its age ("you will lose N hours of
  writes ≈ R rows" from the rows ≈ delta) and that the current file is kept as
  `<file>.pre-restore-<yyyyMMdd-HHmmss>`; typed confirm via `ConfirmAsync("Restore backup over",
  name, isProd)`. Steps: verify the backup (abort unless ok) → `VACUUM INTO` the pre-restore copy
  next to the live file (policy-checked) → online backup API from the backup (read-only) into the
  live file (read-write connection, busy timeout applies; writers are blocked for the copy, other
  connections see the new content, WAL handled by SQLite) → integrity check of the live file.
- **Failure banner**: when the newest record failed, show its error; if the failure is
  `SQLITE_FULL` (13, "disk is full") and prune would remove files, offer **Prune N and retry**.
  `FriendlySqliteError` maps 13 to "The disk or volume is full."
- Header meta: "target <dir> · N kept · <total size>". Tiles: Last successful (age, policy,
  missed slots in the last 7 days), Method (VACUUM INTO · online · compacts on the way out),
  Verified (ok / total), Retention (`7d · 4w · 3m` · next prune removes N).
- **Nav badge** on Backups: hours since the last successful backup when a policy is enabled and
  it is older than 26 h (`GetNavBadgeAsync`); none otherwise.

### 11.8 Overview additions (1a)

- **Last backup** tile: age of the newest successful backup, "policy: daily HH:mm" or "no
  schedule", **Back up now →** (links to Backups). "backups not enabled" when no backup dir.
- **Growth — 30 days** card: file-size samples in `growth:<connectionId>` (`{At, Bytes}` JSON
  array, ≤ 1 per hour, 30-day retention), taken by the scheduler tick and by every Overview load.
  Shows first → last, delta and per-day rate, an inline SVG sparkline (palette tokens only), and
  "volume <total> — ~N days at this rate" from `DriveInfo` of the file's volume when growth is
  positive. Fewer than 2 samples or < 1 h of span → "collecting — first sample at …".
- **Dashboard problems** add: Warning "Last backup failed" (newest record failed), Warning
  "Backup overdue" (policy enabled, newest success older than 26 h).

### 11.9 Contribution and docs

`Contribution => new(PageCount: 4, ActionCount: 16)`. Nav: Overview, Tables, Query, Backups.
`docs/design.md` dated section; `docker/README.md` and `.env.example` document `BackupDir`
(`/sqlite/backups` in compose). The seed additionally creates a churned tenant's old events (so the
mockup's retention DELETE has rows to dry-run), a `reports` / `report_runs` pair, and an empty
`new.db` beside `reports.db` for the 1g empty state; `backups/` directory created writable.

### 11.10 Phase 2/3 non-goals

Per-table growth attribution, page-cache hit %, holder-process hints, loading extensions,
creating database files from the UI (other than backups / recovered copies), temporary host
connections for backups, cross-backup comparison.
