"""Builds the SQLite plugin's demo database (docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §8).

Usage:  python3 -I docker/sqlite/seed.py [target-dir]      (default: /sqlite)

Creates <target-dir>/reports.db (~35 MB) once; a re-run leaves an existing file alone. Shape follows
mockup 1b: tenants, users, sessions, events with JSON props and unix-ms timestamps, a WITHOUT ROWID
event_props table with an FK to events, two indexes on events, two views, a trigger and a
migrations table. ANALYZE runs last so sqlite_stat1 row estimates exist, and the oldest 20% of
events are deleted first (a contiguous id range, so whole pages empty out) so the Overview has
free pages to show. Journal mode is left as WAL.
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

    # The app container runs as a non-root user and SQLite needs to create -shm next to a WAL file
    # even for a read-only connection.
    os.chmod(TARGET, 0o777)
    os.chmod(PATH, 0o666)
    print(f"Wrote {PATH} ({os.path.getsize(PATH) / 1024 / 1024:.0f} MB).")


if __name__ == "__main__":
    main()
