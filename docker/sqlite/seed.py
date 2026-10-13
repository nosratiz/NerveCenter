"""Builds the SQLite plugin's demo files (docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §8, §11.9).

Usage:  python3 -I docker/sqlite/seed.py [--force] [target-dir]      (default: /sqlite)

Creates, each only when missing (a re-run leaves existing files alone; --force deletes and rebuilds
reports.db and new.db -- backups/ is never emptied):

  <target-dir>/reports.db  (~35 MB). Shape follows mockup 1b: tenants, users, sessions, events with
      JSON props and unix-ms timestamps, a WITHOUT ROWID event_props table with an FK to events, two
      indexes on events, two views, a trigger, a migrations table, and a reports / report_runs pair
      (report_runs.report_id ON DELETE CASCADE). Event timestamps run from ~13 months ago to the
      moment the seed runs, so the mockup's retention DELETE (events older than 90 days of churned
      tenants) always has rows to dry-run -- umbrella churned ~60 days ago, so it also keeps a few
      recent rows the DELETE leaves alone. ANALYZE runs last so sqlite_stat1 row estimates exist,
      and the oldest 20% of events are deleted first (a contiguous id range, so whole pages empty
      out) so the Overview has free pages to show. Journal mode is left as WAL.
  <target-dir>/new.db      a 0-byte file: SQLite opens it as an empty database (mockup 1g empty state).
  <target-dir>/backups/    world-writable, for the connections' "Enable backups to" (spec §11.7).
"""
import json
import os
import random
import sqlite3
import sys
import time

ARGS = [a for a in sys.argv[1:] if a != "--force"]
FORCE = "--force" in sys.argv[1:]
TARGET = ARGS[0] if ARGS else "/sqlite"
PATH = os.path.join(TARGET, "reports.db")
EMPTY_PATH = os.path.join(TARGET, "new.db")
BACKUPS = os.path.join(TARGET, "backups")
TENANTS = ["acme-uk", "northwind", "globex", "initech", "umbrella", "hooli", "stark-eu", "wayne-uk"]
EVENT_NAMES = ["page.view", "report.viewed", "export.csv", "login", "search"]
EVENTS = 200_000
SESSIONS = 20_000
USERS = 4_000
REPORTS = 300
REPORT_RUNS = 24_000
DAY_MS = 86_400_000
NOW_MS = int(time.time() * 1000)
START_MS = NOW_MS - 400 * DAY_MS          # events and sessions span ~13 months up to now
CHURNED = "umbrella"
CHURNED_AT_MS = NOW_MS - 60 * DAY_MS      # umbrella's last activity
REPORT_KINDS = ["usage", "billing", "audit", "export"]

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
CREATE TABLE reports(id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL REFERENCES tenants(id),
                     name TEXT NOT NULL, kind TEXT NOT NULL, created_at INTEGER NOT NULL);
CREATE TABLE report_runs(id INTEGER PRIMARY KEY,
                         report_id INTEGER NOT NULL REFERENCES reports(id) ON DELETE CASCADE,
                         started_at INTEGER NOT NULL, duration_ms INTEGER NOT NULL,
                         status TEXT NOT NULL, row_count INTEGER);
CREATE INDEX idx_report_runs_report ON report_runs(report_id, started_at);
"""


def remove_db(path: str) -> None:
    for suffix in ("", "-wal", "-shm", "-journal"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)


def main() -> None:
    os.makedirs(TARGET, exist_ok=True)
    if FORCE:
        remove_db(PATH)
        remove_db(EMPTY_PATH)
    seed_reports()
    seed_empty()
    seed_backups_dir()
    # The app container runs as a non-root user and SQLite needs to create -shm next to a WAL file
    # even for a read-only connection.
    os.chmod(TARGET, 0o777)


def seed_empty() -> None:
    if os.path.exists(EMPTY_PATH):
        print(f"{EMPTY_PATH} already exists; leaving it alone.")
        return
    open(EMPTY_PATH, "wb").close()
    os.chmod(EMPTY_PATH, 0o666)
    print(f"Wrote {EMPTY_PATH} (0 bytes).")


def seed_backups_dir() -> None:
    os.makedirs(BACKUPS, exist_ok=True)
    os.chmod(BACKUPS, 0o777)
    print(f"Backup directory {BACKUPS} is writable.")


def tenant_for(rnd: random.Random, ts: int) -> int:
    """Any tenant, except that the churned one has no activity after CHURNED_AT_MS."""
    churned = TENANTS.index(CHURNED) + 1
    while True:
        tenant = rnd.randint(1, len(TENANTS))
        if tenant != churned or ts <= CHURNED_AT_MS:
            return tenant


def seed_reports() -> None:
    if os.path.exists(PATH):
        print(f"{PATH} already exists; leaving it alone (--force rebuilds it).")
        return

    rnd = random.Random(42)
    db = sqlite3.connect(PATH)
    db.executescript(SCHEMA)
    db.executemany("INSERT INTO tenants(id, name, status) VALUES (?, ?, ?)",
                   [(i + 1, name, "churned" if name == CHURNED else "active") for i, name in enumerate(TENANTS)])
    db.executemany("INSERT INTO users(id, tenant_id, email) VALUES (?, ?, ?)",
                   [(u, rnd.randint(1, len(TENANTS)), f"user{u}@example.test") for u in range(1, USERS + 1)])
    db.executemany("INSERT INTO sessions(id, user_id, started_at) VALUES (?, ?, ?)",
                   [(s, rnd.randint(1, USERS), START_MS + (s - 1) * (NOW_MS - START_MS) // SESSIONS) for s in range(1, SESSIONS + 1)])

    events, props = [], []
    for e in range(1, EVENTS + 1):
        name = rnd.choice(EVENT_NAMES)
        body = None if rnd.random() < 0.01 else json.dumps({"reportId": rnd.randint(1, 9106), "ms": rnd.randint(10, 900)})
        ts = START_MS + e * (NOW_MS - START_MS) // EVENTS
        events.append((e, rnd.randint(1, SESSIONS), tenant_for(rnd, ts), name, ts, body,
                       rnd.choice(["web", "web", "api", "cli"])))
        props.append((e, "path", f"/reports/{rnd.randint(1, 9106)}"))
        props.append((e, "agent", rnd.choice(["firefox", "chrome", "safari"])))
    db.executemany("INSERT INTO events(id, session_id, tenant_id, name, ts, props, source) VALUES (?, ?, ?, ?, ?, ?, ?)", events)
    db.executemany("INSERT INTO event_props(event_id, key, value) VALUES (?, ?, ?)", props)
    db.executemany("INSERT INTO __migrations(version, name, applied_at) VALUES (?, ?, ?)",
                   [(v, f"migration_{v:04d}", "2026-09-25T14:22:00Z") for v in range(1, 39)])

    reports = []
    for r in range(1, REPORTS + 1):
        created = START_MS + rnd.randint(0, 300) * DAY_MS
        reports.append((r, tenant_for(rnd, created), f"{rnd.choice(REPORT_KINDS)} report {r:03d}", rnd.choice(REPORT_KINDS), created))
    db.executemany("INSERT INTO reports(id, tenant_id, name, kind, created_at) VALUES (?, ?, ?, ?, ?)", reports)
    runs = []
    for run in range(1, REPORT_RUNS + 1):
        report = reports[rnd.randint(0, REPORTS - 1)]
        started = rnd.randint(report[4], NOW_MS)
        status = rnd.choices(["ok", "failed", "cancelled"], weights=[92, 6, 2])[0]
        runs.append((run, report[0], started, rnd.randint(80, 45_000), status, rnd.randint(0, 250_000) if status == "ok" else None))
    runs.sort(key=lambda row: row[2])
    db.executemany("INSERT INTO report_runs(report_id, started_at, duration_ms, status, row_count) VALUES (?, ?, ?, ?, ?)",
                   [row[1:] for row in runs])
    db.commit()

    # Delete a contiguous range, not a scatter: deleting every Nth row leaves each page partly full,
    # so SQLite frees nothing. The oldest 20% of events empties whole pages onto the freelist.
    # Cascade the event_props rows too, so the demo file has no FK violations of its own.
    # (Python's sqlite3 leaves foreign_keys OFF by default, unlike SbConsole's connections.)
    db.execute("PRAGMA foreign_keys = ON")
    db.execute(f"DELETE FROM events WHERE id <= {EVENTS // 5}")
    db.commit()
    db.execute("ANALYZE")
    db.execute("PRAGMA journal_mode = WAL")
    db.commit()
    db.close()

    os.chmod(PATH, 0o666)
    print(f"Wrote {PATH} ({os.path.getsize(PATH) / 1024 / 1024:.0f} MB).")


if __name__ == "__main__":
    main()
