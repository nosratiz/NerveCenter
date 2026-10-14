# PostgreSQL plugin — Design

Status: approved, 2026-10-14.

## 1. Context

SbConsole's seventh plugin, built from mockup `SbConsole PostgreSQL.dc.html` (screens 1a–1g):

1. **Overview** (1a, 1g) — five KPI tiles, a worst-first "Needs attention" list, the databases
   table, and an unreachable state that keeps the last good values (stale, dimmed).
2. **Browse** (1b) — schema tree with `reltuples` badges; per-object tabs Data / Columns / Indexes /
   Constraints / DDL. Reads only, except the Indexes row menu (REINDEX, Drop).
3. **Query** (1c) — read-only by default, Write toggle with a PROD banner, row cap, timeout,
   Grid / JSON / EXPLAIN, friendly SQLSTATE errors with the failing line marked, per-connection
   history.
4. **Activity** (1d, 1e) — `pg_stat_activity` with the blocking chain indented under its blocker,
   Cancel (Mutating) inline, Terminate (Destructive) in the row menu behind a typed `pid N` confirm
   on PROD.
5. **Maintenance** (1f) — dead tuples / bloat per table, autovacuum progress, xid age, VACUUM /
   ANALYZE (Mutating), VACUUM FULL (Destructive).

All five pages are in scope for one spec and one plan, built in ordered phases (§13).

Unlike SQLite, a connection is a **server**, not a database: the header carries a database picker
(`pg_database`) and Browse / Query / Maintenance re-open against the chosen database.

## 2. Decisions

- **Driver: Npgsql 10.0.3**, referenced by the plugin only (plugins may take their own client
  package, as Redis takes StackExchange.Redis). Core keeps EF Core + SQLite; nothing here touches the
  host DB.
- **Operations wrapper**: `IPostgresOperations` (`ISingletonService`) is the only thing that talks to
  Npgsql. Handlers and pages depend on the interface; unit tests substitute it.
- **Testing exception, like SQLite**: `PostgresOperations` is tested against a real `postgres:16`
  Testcontainer (Testcontainers.PostgreSql 4.16.0) — the only honest way to test catalog SQL,
  read-only transactions, timeouts, cancel/terminate and blocking detection. Those tests skip
  themselves when Docker is unavailable (`[DockerFact]`). First Testcontainers use in the repo.
- **Every user-SQL run is audited**, read or write (`pg.query.read` / `pg.query.write`), as the
  mockup states: free-form SQL against shared servers is the kind of thing an audit log exists for.
  So Query "Run" is a command handler. Metrics/catalog reads (Overview, Browse, Activity list,
  Maintenance list, completion) are queries and are not audited.
- **`application_name=sbconsole`** on every connection the plugin opens, so Activity can mark its own
  row "this console" and exclude it from blocker/idle problems.
- **One additive SDK change**: a detailed `IConfirmationService` overload (§10).

## 3. Plugin shape

```
src/Plugins/SbConsole.Plugins.Postgres/
  PostgresPlugin.cs            Id/Kind "postgres", Monogram "PG", 5 nav items, dashboard hooks
  HandlerRunner.cs, PluginResult.cs   (same pattern as SQLite)
  Client/   IPostgresOperations, PostgresOperations.*.cs (partials per area), models,
            PostgresConnectionSettings, PostgresConnectionFields.razor, PgSqlLexer,
            FriendlyPostgresError, PgFormat, CsvExport
  Overview/ Browse/ Query/ Activity/ Maintenance/   handlers + stores
  Components/  PgConnectionPicker, PgDatabasePicker, PgPageHeader, PgStatTile, NullChip
  Pages/       Overview, Browse, Query, Activity, Maintenance (.razor)
  wwwroot/     postgres.css, postgres.js (CSV download, Ctrl+Enter)
tests/SbConsole.Plugins.Postgres.Tests/
```

Routes: `/p/postgres/{overview|browse|query|activity|maintenance}?connectionId=…&db=…`.
`Contribution => new(PageCount: 5, ActionCount: 11)`.

### 3.1 Secret format and connection form

The secret is an Npgsql connection string plus two SbConsole keys:

```
Host=db.internal;Port=5432;Database=postgres;Username=app_ro;Password=…;SSL Mode=Prefer;Timeout=5;Mode=ReadOnly;StatementTimeoutS=30
```

`PostgresConnectionSettings.Parse(secret)` reads `Mode` (`ReadOnly` default | `ReadWrite`) and
`StatementTimeoutS` (default 30, ≥ 1), removes them, validates the rest through
`NpgsqlConnectionStringBuilder` (Host required, port 1–65535), and exposes
`BuildConnectionString(database?, applicationName)` which forces `Application Name=sbconsole`,
`Include Error Detail=false` (keeps parameter values out of exception text), `Pooling=true`, and
the `Database` override for the picker. `ToSecretString()` round-trips.

`PostgresConnectionFields.razor` (the three-parameter contract from `IPlugin`): Host, Port,
Database, Username, Password (masked), SSL mode select (Disable/Prefer/Require/VerifyFull),
Connect timeout s, Statement timeout s, Mode radio. Field-level errors as in SQLite's form.

Summary chips: `Server = host:port`, `Database = db`, `Mode = read-only|read-write`. Never the user.

`TestConnectionAsync`: open, `SELECT version(), pg_is_in_recovery()`; ok message
`PostgreSQL 16.3 · primary` / `· replica`.

### 3.2 Opening connections and the read-only guard

- Pool per connection string (Npgsql's own pooling). Every command gets `CommandTimeout` =
  statement timeout + 5 s as a client-side backstop; the real limit is `SET LOCAL statement_timeout`.
- **Read transaction**: `BEGIN READ ONLY; SET LOCAL statement_timeout = '<n>s'; <work>; ROLLBACK`.
  Used for every catalog/metric read and for Query in read mode.
- **User SQL in read mode** must be exactly one statement per `PgSqlLexer` and must not be
  transaction/session control (`BEGIN START COMMIT END ROLLBACK ABORT SAVEPOINT RELEASE PREPARE
  SET RESET DISCARD LISTEN COPY VACUUM CHECKPOINT DO CALL`). Otherwise `COMMIT; DELETE …` would leave
  the read-only transaction. `SET LOCAL` is refused too (users pick the timeout in the toolbar).
- **Write mode** needs `Mode=ReadWrite` in the saved secret (re-read server-side on every run; the
  page toggle is only a convenience). Runs `BEGIN; SET LOCAL statement_timeout; <script>; COMMIT`;
  any error → `ROLLBACK`. Multiple statements allowed; transaction control, `VACUUM`, `COPY`,
  `CREATE/DROP DATABASE`, `CREATE INDEX CONCURRENTLY` refused (none can run in a transaction block).
- Every Activity/Maintenance/Index Mutating or Destructive action also requires `Mode=ReadWrite`.
- **Documented limitation**: a read-only transaction is not a privilege boundary — functions with
  side effects (`pg_terminate_backend`, `dblink`, `pg_reload_conf`) still run inside one. Real
  enforcement is a read-only role; the connection form's help text says so.

### 3.3 `PgSqlLexer`

Splits text into top-level statements and returns each statement's start offset and first keyword.
Understands `'…'` (with `''`), `E'…'` (backslash escapes), `"…"`, `$tag$…$tag$`, `-- …` and nested
`/* … */`. Empty statements (only whitespace/comments) are dropped. Also maps a server `position`
(1-based character offset into the sent text) to line/column for the error marker.

## 4. Overview (1a, 1g) — `/p/postgres/overview`

`GetOverviewQueryHandler` → `IPostgresOperations.GetOverviewAsync(secret)` returning one
`PgOverview`:

| Tile | Source |
|---|---|
| Connections | `count(*)` from `pg_stat_activity` (client backends), `max_connections`; idle / idle-in-txn / waiting counts |
| Cache hit ratio | `sum(blks_hit)/(sum(blks_hit)+sum(blks_read))` over `pg_stat_database`; `shared_buffers` |
| Longest open transaction | max `now() - xact_start` excluding our own backend; pid, user, state — **warning outline** when > 5 min |
| Database size | `sum(pg_database_size(oid))` over connectable DBs; count |
| Replication lag | primary: `pg_stat_replication` max `replay_lag`, replica count, `pg_wal_lsn_diff` retained; replica: `now() - pg_last_xact_replay_timestamp()`; none → "—", "no replicas" |

Header: server version, primary/replica, uptime (`now() - pg_postmaster_start_time()`), "refreshed N s
ago", Refresh.

**Needs attention** (worst first, each links to the fixing page): blocked sessions
(`cardinality(pg_blocking_pids(pid)) > 0`) → Activity; idle in transaction > 5 min → Activity;
tables > 30 % dead tuples (current database) → Maintenance; indexes with `idx_scan = 0` that are
neither unique nor primary (current database) → Browse; stats reset age shown.

**Databases table**: name, size, owner, connections (`numbackends`), hit ratio, commits/s,
deadlocks. Commits/s = delta of `xact_commit` against the previous sample stored in
`IPluginStore` key `overview:<connectionId>` (with its timestamp); first load shows "—".

**Unreachable (1g)**: the store also keeps the last good `PgOverview` (stamped). On failure the page
shows an error `MudAlert` with the friendly sentence (`FriendlyPostgresError`), Retry now / Edit
connection / Test, Details (`MudCollapse`, the capped exception line), and an automatic retry
every 30 s with a countdown. Cached tiles render dimmed with "stale · HH:mm"; never-loaded sections
keep their headers with `MudSkeleton` rows. The picker shows a red dot for that connection.

Writing the snapshot to the plugin's own store is not audited (same rule as SQLite's growth samples).

## 5. Browse (1b) — `/p/postgres/browse`

- **Tree** (`ListSchemaQueryHandler`): schemas (excluding `pg_*`, `information_schema`) → Tables
  (with `reltuples` badges, `-1` → "?"), Views, Materialized views, Functions; counts per group; a
  client-side filter that keeps matching objects and their parents ("3 of 31"). Only the first 50
  tables per schema render, then "N more…".
- **Header**: `schema.name`, kind chip, `rows ≈ · heap size + index size · owner`, "reads only —
  nothing on this page writes" (except the Indexes menu actions).
- **Data tab** (`PreviewRowsQueryHandler`): 25/50/100 rows per page, `LIMIT/OFFSET`. Filter row per
  column: operator (`= <> > >= < <= like ilike is null is not null @>`) + value. SQL is built with
  quoted identifiers and parameters cast to the column's type: `"status" = $1::text`, `@>` only for
  `jsonb`/arrays. Single sort column + direction. "Rows 1–50 of N matching ≈" where N comes from
  `EXPLAIN (FORMAT JSON)` plan rows (the unfiltered case uses `reltuples`). Cells: NULL is a dashed
  outline chip; `json/jsonb` collapse to "{…} N keys" and expand inline; long text truncates at 200
  chars; `bytea` shows `\x…` + length. Values arrive as text (`GetValue` → formatted) — never
  executed.
- **Columns tab**: name, type (`format_type`), nullable, default, PK/FK markers, comment.
- **Indexes tab**: from `pg_stat_user_indexes` + `pg_index` + `pg_get_indexdef`: name, columns /
  predicate, method, size, scans, tuples read, flags (primary, unique, partial, invalid, unused
  since stats reset N d). Header line "From pg_stat_user_indexes · stats reset N days ago · K unused
  · size". Row menu: Copy definition (Safe), REINDEX CONCURRENTLY (Mutating), Drop index…
  (Destructive, `DROP INDEX CONCURRENTLY`, refused for primary/unique-constraint indexes).
- **Constraints tab**: name, type, `pg_get_constraintdef`.
- **DDL tab**: reconstructed `CREATE TABLE` (columns, defaults, not null), then constraints, then
  indexes (`pg_get_indexdef`), then comments; views use `pg_get_viewdef`, functions
  `pg_get_functiondef`. Copy button.
- **Open in Query**: navigates with `SELECT * FROM "schema"."table" LIMIT 100` prefilled.
- **Export CSV** (`ExportTableCsvCommandHandler`, audited `pg.table.export`): current filters/sort,
  capped at 100,000 rows or 50 MB like SQLite, delivered via `DotNetStreamReference` +
  `postgres.js`.

## 6. Query (1c) — `/p/postgres/query`

- Toolbar: `MudToggleGroup` Read-only | Write (Write disabled with a tooltip unless the connection's
  mode is ReadWrite), Rows (100 / 1,000 / 10,000, default 1,000), Timeout (10 / 30 / 60 / 300 s,
  default from connection), Export CSV, Run (Ctrl+Enter).
- **Write on PROD**: switching to Write asks `IConfirmationService` typed connection name; then a
  warning `MudAlert` banner stays until "Back to read-only"; Run is tinted warning.
- `RunQueryCommandHandler`: lexes, enforces §3.2, runs, reads at most `cap` rows (stops reading,
  reports "capped"), returns columns with PG type names (`GetDataTypeName`), rows as display
  strings + raw JSON-able values, row count, affected rows (write), elapsed, statement count.
  Audits `pg.query.read` / `pg.query.write` with `sha256:… · preview(200) · rows · elapsed`, both
  success and failure (`(failed)` with SQLSTATE).
- Result tabs: **Grid** (`#`, column headers with type under the name, first 200 rows then "Show
  all" up to the cap), **JSON** (rows as an array of objects), **EXPLAIN** (`EXPLAIN (FORMAT TEXT)`
  of the editor text in a read-only transaction — `ANALYZE` is not added automatically; the tab
  says so; a user-typed `EXPLAIN ANALYZE` runs as the user's statement, so in read mode it is still
  inside `BEGIN READ ONLY`).
- **Errors** (`FriendlyPostgresError.FromPostgres`): one sentence from SQLSTATE + message fields
  (column/table/constraint names), the server's HINT if any, "Line L, position C" from `position`
  via `PgSqlLexer`, the line marked in the editor (wavy underline on that line), "Nothing was
  changed — the transaction rolled back." for writes, and Details (`SQLSTATE 42703 ·
  undefined_column · 0.02 s · audited as pg.query.write (failed)`). Codes with dedicated sentences:
  42703, 42P01, 42601, 42883, 23505, 23503, 23502, 23514, 25006, 57014, 40P01, 55P03, 42501, 28P01,
  3D000, 53300; connection failures (socket/timeout) → "Can't reach …".
- **History** (`QueryHistoryStore`, `history:<connectionId>` in `IPluginStore`): newest first, entries
  older than 90 days dropped on write, capped at 500. Entry: text (first 4,000 chars), mode, rows,
  elapsed, SQLSTATE on error, actor, timestamp, audit hash. Click loads the text. Writes show the
  actor and "audit row" link to `/audit?search=<hash>`.
- **Export CSV** (`ExportQueryCsvCommandHandler`, `pg.query.export`): re-runs the read-only query
  with the CSV caps (§5). Write mode cannot export.

## 7. Activity (1d, 1e) — `/p/postgres/activity`

- `GetActivityQueryHandler` → `pg_stat_activity` (`backend_type = 'client backend'`) with
  `pg_blocking_pids(pid)`, ordered by `xact_start NULLS LAST`. Columns: pid, user, application,
  client, state, wait event (`type:event`), query (one line, 120 chars, full in a tooltip), duration
  (`now() - coalesce(xact_start, query_start)`; idle → "—"), "blocks N" chip, "this console"
  chip for `pg_backend_pid()`.
- **Blocking chain**: rows with blockers are indented (`↳`) under their first blocker; a blocker is
  shown at the top level once. Cycles (deadlock in progress) fall back to flat rows.
- Header: "N backends · B blocked · I idle in txn"; Auto-refresh off/5 s/15 s (default 5 s) with
  "next in N s"; Active only switch; idle (`state = 'idle'`) backends hidden behind "… N idle
  backends hidden · show all".
- **Banner**: when the worst blocker is idle in transaction, a warning alert: "pid P has been idle in
  transaction for D and is blocking N sessions on <relation>. Cancelling does nothing to an idle
  backend — terminating it will." + "Jump to blocker ↓". Relation from `pg_locks` of the waiting
  pid (`relation::regclass`) when available.
- Row actions: **Cancel** (inline ghost button, Mutating, `pg_cancel_backend`) — disabled on idle
  and idle-in-txn backends and on our own; plain confirm. Row menu: Copy query, Filter by user,
  Cancel query (mutating), divider, **Terminate backend…** (Destructive, error colour,
  `pg_terminate_backend`) — detailed confirm (§10), typed `pid N` on PROD.
- `CancelBackendCommandHandler` / `TerminateBackendCommandHandler`: look up `IsProd` server-side,
  require ReadWrite, refuse our own pid, re-read the backend's row just before acting and audit
  `pg.backend.cancel` / `pg.backend.terminate` with `pid · user · application · query preview`.
  Result `false` from the function (backend already gone) → friendly "pid N had already ended",
  audited as failed.

## 8. Maintenance (1f) — `/p/postgres/maintenance`

- Header chips: `autovacuum on|off · autovacuum_max_workers · N running`; buttons "ANALYZE K
  flagged" / "VACUUM K flagged" (Mutating, plain confirm with the count; runs sequentially, one
  audit row per table).
- Tiles: Flagged tables (> 30 % dead or > 20 % bloat est.), Reclaimable (est.) (sum of bloat
  estimates; footnote "plain VACUUM frees for reuse, not to disk"), Autovacuum running
  (`pg_stat_progress_vacuum` joined to activity: table, started, pid, % heap scanned), Oldest xid
  age (`max(age(datfrozenxid))`, vs 2^31 and `autovacuum_freeze_max_age`).
- Table: table, size (`pg_total_relation_size`), live, dead, dead %, bloat est., last vacuum
  (`last_vacuum`; the actor is not shown — the audit log has it), last autovacuum (or "running"),
  last analyze; row buttons VACUUM / ANALYZE; row menu: VACUUM FULL… (Destructive, typed on PROD,
  copy says it takes an ACCESS EXCLUSIVE lock and rewrites the table). Shows the top 25 by dead %,
  then "N more tables under 1 % dead · show all".
- **Bloat estimate**: if `pgstattuple` is installed in the current database, `pgstattuple_approx`
  for the flagged + top-25 tables only (it scans); otherwise "—" with a hint "install pgstattuple
  for bloat estimates". Never installs extensions.
- Commands (`VacuumTableCommandHandler`, `AnalyzeTableCommandHandler`,
  `VacuumFullTableCommandHandler`): ReadWrite required, identifier quoted from catalog-validated
  `schema.table` (looked up by OID, never interpolated from client text), run outside a transaction
  with the connection's statement timeout raised to 1 h for VACUUM FULL; audited
  `pg.maintenance.vacuum` / `.analyze` / `.vacuum_full`.
- Index commands from Browse (`ReindexIndexCommandHandler`, `DropIndexCommandHandler`): same OID
  rule; `pg.index.reindex` / `pg.index.drop`.

## 9. Pickers and shared UI

- `PgConnectionPicker`: lists `postgres` connections (`IConnectionProvider.ListAsync`), PROD chip,
  red dot when the last overview load failed (kept in a scoped `PgReachability` service).
  `connectionId` in the query string; first connection by default; empty state links to
  Connections.
- `PgDatabasePicker`: `pg_database` where `datallowconn and not datistemplate`; default from the
  connection's `Database`; `db` in the query string.
- `PgPageHeader`: title, connection name, region/host, PROD chip, right-side slot.
- All colours via `--mud-palette-*` (CLAUDE.md theming rule).

## 10. SDK change — detailed confirmation

```csharp
public sealed record ConfirmationDetail(string Label, string Value);
public sealed record ConfirmationRequest(
    string Verb, string Target, bool IsProd,
    string? Consequence = null,                       // one paragraph above the details
    IReadOnlyList<ConfirmationDetail>? Details = null,
    string? AuditAction = null,                       // "Audited as pg.backend.terminate …"
    bool Destructive = false);                        // error-outlined dialog + error button

public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default);
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct = default) =>
        ConfirmAsync(request.Verb, request.Target, request.IsProd, null, ct);
}
```

`MudConfirmationService` implements the overload; `ConfirmDialog` gains optional `Consequence`,
`Details`, `AuditAction`, `Destructive` parameters (unchanged rendering when null). Typed text is the
target (`pid 48213`) so a pasted number alone doesn't match. Existing callers are untouched.

## 11. Host integration

- `GetDashboardMetricsAsync`: `Servers = 1` per connection.
- `GetDashboardProblemsAsync`: Error "N sessions blocked" (> 0 blocked for > 1 min); Warning "Idle in
  transaction" (> 5 min); Warning "Connections at N %" (≥ 90 %); Warning "Replication lag" (> 30 s);
  Error "Transaction ID wraparound" (xid age > 1.5 B). Unreachable throws (host reports it).
- `GetNavBadgeAsync(ActivityHref)`: blocked-session count (null when 0 or on failure).
- `Program.cs`: `AddSbConsolePlugin<PostgresPlugin>()`; Web references the plugin project.

## 12. Testing

- `PgSqlLexer`, `PostgresConnectionSettings`, `FriendlyPostgresError`, filter SQL builder, CSV,
  history store, blocking-chain tree builder, problem rules: pure unit tests.
- Handlers: substituted `IPostgresOperations`, `IAuditScope`, `IConnectionProvider`,
  `IPluginStore`, `FakeTimeProvider`.
- Pages/components: bUnit with substituted handler dependencies (as SQLite pages are tested).
- `PostgresOperations`: `PostgresFixture` (collection fixture) starts one `postgres:16-alpine`
  container per test run, seeds a schema; `[DockerFact]` skips (reason "Docker not available")
  when `docker info` fails or `SBC_SKIP_DOCKER_TESTS=1`. Covers: overview, schema, rows + every
  filter operator, read-only refusal (`25006`), multi-statement refusal, timeout (`57014`), write
  commit/rollback, blocking detection with two real sessions, cancel, terminate, vacuum/analyze,
  reindex/drop index.
- `ProgramDiRegistrationTests` covers the new handlers.

## 13. Build order (one plan, phased)

1. Scaffold, settings + form + Test, lexer, friendly errors, Testcontainers fixture, Overview
   (incl. unreachable), pickers, dashboard hooks.
2. Browse (tree, tabs, filters, DDL, CSV); index REINDEX/Drop wait for phase 4's confirm overload.
3. Query (read/write, history, EXPLAIN, export).
4. SDK confirmation overload; Activity (chain, cancel, terminate); index actions.
5. Maintenance; docker `postgres` profile + seed + blocker sidecar + demo connections.
6. `docs/design.md` §18, CLAUDE.md, docker/README.md.

Gate per phase: `dotnet build -warnaserror` and `dotnet test` green; commit per task.

## 14. Local stack

Compose profile `postgres`: `postgres:16` (port 5433 on the host to avoid clashing with a local
server), `POSTGRES_PASSWORD` from `.env` with a dev default, init scripts in `docker/postgres/`
creating `app` (orders, order_items, customers, products, payments, sessions, events — scaled down
so the seed finishes in under a minute), `analytics`, `billing`, `CREATE EXTENSION pgstattuple` in
`app`, a deliberately unused `idx_orders_legacy_ref`, and a delete pass that leaves dead tuples
with autovacuum disabled on two tables. `postgres-blocker` sidecar (psql loop): one session `BEGIN;
UPDATE orders … ;` then sleeps idle in transaction; two sessions block on the same row; restarts
every 15 min. Demo connections: `pg-local` (ReadWrite, tags `prod` — to demo the typed flows) and
`pg-local-ro` (ReadOnly). The app container reaches it at `postgres:5432`.

## 15. Non-goals

Creating/dropping databases, roles or extensions; editing rows from the grid; query autocomplete
(the SQLite completion engine is not ported in this round); `EXPLAIN` visual plans; logical
replication slots management; Testcontainers for other plugins.
