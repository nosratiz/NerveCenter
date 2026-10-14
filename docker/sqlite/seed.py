"""Builds the SQLite plugin's demo files (docs/superpowers/specs/2026-10-13-sqlite-plugin-design.md §8, §11.9).

Usage:  python3 -I docker/sqlite/seed.py [--force] [target-dir]      (default: /sqlite)

Creates, each only when missing (a re-run leaves existing files alone; --force deletes and rebuilds
every database file below -- backups/ is never emptied):

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
  <target-dir>/shop.db     (~5-10 MB, journal mode DELETE -- contrasts with reports.db's WAL). A small
      shop: categories, products (price_cents, JSON attributes), customers, orders (CHECK on status,
      unix-ms placed_at), order_items (order FK ON DELETE CASCADE, product FK ON DELETE RESTRICT, so the
      Drop dialog shows both consequences), reviews (rating CHECK 1..5, JSON meta), a WITHOUT ROWID
      product_tags table, a partial index, a view, an updated_at trigger. ANALYZE runs last.
  <target-dir>/corrupt.db  a small DELETE-journal database whose index b-tree is deliberately damaged
      (integrity_check fails; REINDEX fixes it) and which holds a few orphan rows that break its FKs
      (foreign_key_check reports them) -- for the Overview's failed-integrity strip, REINDEX, "Recover
      into new file" and the dashboard's problem counts. The script verifies both properties.
  <target-dir>/reference.db  small lookup data (countries, currencies, country_currencies), ANALYZEd;
      meant to be connected in Read-only mode.
  <target-dir>/backups/    world-writable, for the connections' "Enable backups to" (spec §11.7).
"""
import json
import os
import random
import shutil
import sqlite3
import sys
import tempfile
import time

ARGS = [a for a in sys.argv[1:] if a != "--force"]
FORCE = "--force" in sys.argv[1:]
TARGET = ARGS[0] if ARGS else "/sqlite"
PATH = os.path.join(TARGET, "reports.db")
EMPTY_PATH = os.path.join(TARGET, "new.db")
SHOP_PATH = os.path.join(TARGET, "shop.db")
CORRUPT_PATH = os.path.join(TARGET, "corrupt.db")
REFERENCE_PATH = os.path.join(TARGET, "reference.db")
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
        for path in (PATH, EMPTY_PATH, SHOP_PATH, CORRUPT_PATH, REFERENCE_PATH):
            remove_db(path)
    seed_reports()
    seed_empty()
    seed_shop()
    seed_corrupt()
    seed_reference()
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


# --------------------------------------------------------------------------------------------------
# shop.db
# --------------------------------------------------------------------------------------------------
SHOP_SCHEMA = """
CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, parent_id INTEGER REFERENCES categories(id));
CREATE TABLE products(id INTEGER PRIMARY KEY,
                      category_id INTEGER NOT NULL REFERENCES categories(id),
                      sku TEXT NOT NULL UNIQUE,
                      name TEXT NOT NULL,
                      price_cents INTEGER NOT NULL CHECK (price_cents >= 0),
                      attributes TEXT,
                      active INTEGER NOT NULL DEFAULT 1,
                      updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')));
CREATE TABLE customers(id INTEGER PRIMARY KEY,
                       email TEXT NOT NULL UNIQUE,
                       full_name TEXT NOT NULL,
                       country TEXT NOT NULL,
                       created_at TEXT NOT NULL);
CREATE TABLE orders(id INTEGER PRIMARY KEY,
                    customer_id INTEGER NOT NULL REFERENCES customers(id),
                    status TEXT NOT NULL CHECK (status IN ('pending', 'paid', 'shipped', 'delivered', 'cancelled', 'refunded')),
                    placed_at INTEGER NOT NULL,
                    total_cents INTEGER NOT NULL DEFAULT 0);
CREATE TABLE order_items(id INTEGER PRIMARY KEY,
                         order_id INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
                         product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
                         qty INTEGER NOT NULL CHECK (qty > 0),
                         unit_price_cents INTEGER NOT NULL);
CREATE TABLE reviews(id INTEGER PRIMARY KEY,
                     product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE CASCADE,
                     customer_id INTEGER REFERENCES customers(id) ON DELETE SET NULL,
                     rating INTEGER NOT NULL CHECK (rating BETWEEN 1 AND 5),
                     body TEXT,
                     meta TEXT,
                     created_at INTEGER NOT NULL);
CREATE TABLE product_tags(product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE CASCADE,
                          tag TEXT NOT NULL,
                          PRIMARY KEY(product_id, tag)) WITHOUT ROWID;
CREATE INDEX idx_orders_customer ON orders(customer_id, placed_at);
CREATE INDEX idx_orders_pending ON orders(placed_at) WHERE status = 'pending';
CREATE INDEX idx_order_items_order ON order_items(order_id);
CREATE INDEX idx_order_items_product ON order_items(product_id);
CREATE INDEX idx_reviews_product ON reviews(product_id, rating);
CREATE VIEW v_revenue_by_month AS
  SELECT strftime('%Y-%m', o.placed_at / 1000, 'unixepoch') AS month,
         COUNT(DISTINCT o.id) AS orders,
         SUM(oi.qty * oi.unit_price_cents) / 100.0 AS revenue
  FROM orders o JOIN order_items oi ON oi.order_id = o.id
  WHERE o.status NOT IN ('cancelled', 'refunded')
  GROUP BY month;
CREATE TRIGGER trg_products_updated_at AFTER UPDATE ON products
  FOR EACH ROW WHEN NEW.updated_at = OLD.updated_at
BEGIN
  UPDATE products SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = NEW.id;
END;
"""
SHOP_CATEGORIES = {
    "Electronics": ["Headphones", "Keyboards", "Monitors", "Cables"],
    "Home": ["Kitchen", "Lighting", "Storage"],
    "Outdoors": ["Camping", "Cycling", "Running"],
    "Books": ["Fiction", "Technical", "Cookbooks"],
}
SHOP_ADJECTIVES = ["Compact", "Pro", "Classic", "Ultra", "Eco", "Travel", "Studio", "Everyday", "Rugged", "Lite"]
SHOP_NOUNS = {
    "Headphones": "Headphones", "Keyboards": "Keyboard", "Monitors": "Monitor", "Cables": "Cable",
    "Kitchen": "Pan", "Lighting": "Lamp", "Storage": "Box", "Camping": "Tent", "Cycling": "Helmet",
    "Running": "Shoes", "Fiction": "Novel", "Technical": "Handbook", "Cookbooks": "Cookbook",
}
SHOP_COLOURS = ["black", "white", "grey", "blue", "green", "red", "sand"]
SHOP_TAGS = ["bestseller", "new", "sale", "eco", "gift", "limited", "bundle", "clearance"]
SHOP_COUNTRIES = ["GB", "GB", "GB", "DE", "FR", "NL", "IE", "SE", "ES", "IT", "US", "US", "CA", "AU"]
FIRST_NAMES = ["Alex", "Sam", "Jordan", "Priya", "Chen", "Fatima", "Lukas", "Sofia", "Omar", "Mia",
               "Noah", "Aisha", "Ben", "Elena", "Kofi", "Hana", "Leo", "Zara", "Ivan", "Nina"]
LAST_NAMES = ["Smith", "Patel", "Müller", "Garcia", "Okafor", "Kim", "Rossi", "Novak", "Jensen", "Silva",
              "Brown", "Khan", "Dubois", "Tanaka", "Murphy", "Costa", "Nowak", "Haddad", "Larsen", "Walsh"]
REVIEW_OPENERS = ["Really pleased with this.", "Does the job.", "Not what I expected.", "Five stars from me.",
                  "Arrived quickly and well packed.", "Disappointed, sadly.", "Great value for the price.",
                  "Second one I have bought.", "Mixed feelings.", "Exactly as described."]
REVIEW_DETAILS = ["The build quality feels solid", "Setup took a few minutes", "The colour is slightly different from the photos",
                  "Battery life is better than advertised", "It is a little heavier than I hoped",
                  "Customer service sorted a small issue fast", "Instructions could be clearer",
                  "My partner uses it every day now", "It replaced an older model that broke", "Would recommend to friends"]
SHOP_CUSTOMERS = 6_000
SHOP_PRODUCTS = 400
SHOP_ORDERS = 22_000
SHOP_REVIEWS = 12_000


def seed_shop() -> None:
    if os.path.exists(SHOP_PATH):
        print(f"{SHOP_PATH} already exists; leaving it alone (--force rebuilds it).")
        return

    rnd = random.Random(7)
    db = sqlite3.connect(SHOP_PATH)
    db.execute("PRAGMA journal_mode = DELETE")
    db.executescript(SHOP_SCHEMA)

    categories, leaves, next_id = [], [], 1
    for parent, children in SHOP_CATEGORIES.items():
        parent_id = next_id
        categories.append((parent_id, parent, None))
        next_id += 1
        for child in children:
            categories.append((next_id, child, parent_id))
            leaves.append((next_id, child))
            next_id += 1
    db.executemany("INSERT INTO categories(id, name, parent_id) VALUES (?, ?, ?)", categories)

    products, tags, prices = [], [], {}
    for p in range(1, SHOP_PRODUCTS + 1):
        cat_id, cat_name = rnd.choice(leaves)
        price = rnd.choice([499, 999, 1499, 1999, 2499, 3999, 4999, 7999, 12999, 24999]) + rnd.randint(0, 99) * 100
        prices[p] = price
        attrs = None if rnd.random() < 0.05 else json.dumps(
            {"colour": rnd.choice(SHOP_COLOURS), "weight_g": rnd.randint(50, 5000),
             "dimensions_cm": [rnd.randint(5, 80), rnd.randint(5, 80), rnd.randint(1, 40)]})
        updated = time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime((NOW_MS - rnd.randint(0, 200) * DAY_MS) / 1000))
        products.append((p, cat_id, f"SKU-{p:05d}", f"{rnd.choice(SHOP_ADJECTIVES)} {SHOP_NOUNS[cat_name]} {p}",
                         price, attrs, 0 if rnd.random() < 0.04 else 1, updated))
        for tag in rnd.sample(SHOP_TAGS, rnd.randint(0, 3)):
            tags.append((p, tag))
    db.executemany("INSERT INTO products(id, category_id, sku, name, price_cents, attributes, active, updated_at) "
                   "VALUES (?, ?, ?, ?, ?, ?, ?, ?)", products)
    db.executemany("INSERT INTO product_tags(product_id, tag) VALUES (?, ?)", tags)

    shop_start_ms = NOW_MS - 730 * DAY_MS
    customers = []
    for c in range(1, SHOP_CUSTOMERS + 1):
        first, last = rnd.choice(FIRST_NAMES), rnd.choice(LAST_NAMES)
        created = shop_start_ms + (c - 1) * (NOW_MS - shop_start_ms) // SHOP_CUSTOMERS
        customers.append((c, f"{first.lower()}.{last.lower()}.{c}@example.test", f"{first} {last}",
                          rnd.choice(SHOP_COUNTRIES), time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(created / 1000))))
    db.executemany("INSERT INTO customers(id, email, full_name, country, created_at) VALUES (?, ?, ?, ?, ?)", customers)

    orders, items = [], []
    for o in range(1, SHOP_ORDERS + 1):
        placed = shop_start_ms + (o - 1) * (NOW_MS - shop_start_ms) // SHOP_ORDERS + rnd.randint(0, 3_600_000)
        age_days = (NOW_MS - placed) / DAY_MS
        if age_days < 2:
            status = rnd.choices(["pending", "paid"], weights=[70, 30])[0]
        elif age_days < 10:
            status = rnd.choices(["pending", "paid", "shipped", "cancelled"], weights=[10, 30, 55, 5])[0]
        else:
            status = rnd.choices(["delivered", "cancelled", "refunded", "pending"], weights=[90, 5, 4, 1])[0]
        # A customer orders only after signing up.
        newest_customer = max(1, (placed - shop_start_ms) * SHOP_CUSTOMERS // (NOW_MS - shop_start_ms))
        total = 0
        for _ in range(rnd.choices([1, 2, 3, 4, 5, 6], weights=[30, 25, 20, 12, 8, 5])[0]):
            product = rnd.randint(1, SHOP_PRODUCTS)
            qty = rnd.choices([1, 2, 3, 5], weights=[75, 15, 7, 3])[0]
            items.append((o, product, qty, prices[product]))
            total += qty * prices[product]
        orders.append((o, rnd.randint(1, newest_customer), status, placed, total))
    db.executemany("INSERT INTO orders(id, customer_id, status, placed_at, total_cents) VALUES (?, ?, ?, ?, ?)", orders)
    db.executemany("INSERT INTO order_items(order_id, product_id, qty, unit_price_cents) VALUES (?, ?, ?, ?)", items)

    reviews = []
    for r in range(1, SHOP_REVIEWS + 1):
        rating = rnd.choices([1, 2, 3, 4, 5], weights=[6, 7, 14, 33, 40])[0]
        body = None if rnd.random() < 0.08 else " ".join(
            [rnd.choice(REVIEW_OPENERS)] + [rnd.choice(REVIEW_DETAILS) + "." for _ in range(rnd.randint(1, 4))])
        meta = None if rnd.random() < 0.15 else json.dumps(
            {"verified": rnd.random() < 0.8, "helpful": rnd.randint(0, 120), "source": rnd.choice(["web", "app", "email"])})
        reviews.append((r, rnd.randint(1, SHOP_PRODUCTS), None if rnd.random() < 0.03 else rnd.randint(1, SHOP_CUSTOMERS),
                        rating, body, meta, shop_start_ms + rnd.randint(0, NOW_MS - shop_start_ms)))
    db.executemany("INSERT INTO reviews(id, product_id, customer_id, rating, body, meta, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)", reviews)
    db.commit()
    db.execute("ANALYZE")
    db.commit()
    db.close()

    os.chmod(SHOP_PATH, 0o666)
    print(f"Wrote {SHOP_PATH} ({os.path.getsize(SHOP_PATH) / 1024 / 1024:.1f} MB; {SHOP_CUSTOMERS} customers, "
          f"{SHOP_ORDERS} orders, {len(items)} order_items, {SHOP_REVIEWS} reviews, {SHOP_PRODUCTS} products).")


# --------------------------------------------------------------------------------------------------
# corrupt.db
# --------------------------------------------------------------------------------------------------
CORRUPT_SCHEMA = """
CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL);
CREATE INDEX idx_items_name ON items(name);
CREATE TABLE item_notes(id INTEGER PRIMARY KEY,
                        item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                        note TEXT NOT NULL);
"""
CORRUPT_ITEMS = 120
CORRUPT_ORPHANS = [(9001, 9999, "orphan: item was hard-deleted"), (9002, 10_000, "orphan: imported from legacy"),
                   (9003, 10_001, "orphan: typo in item id")]


def check_integrity(path: str) -> list:
    db = sqlite3.connect(path)
    try:
        return [row[0] for row in db.execute("PRAGMA integrity_check")]
    finally:
        db.close()


def seed_corrupt() -> None:
    if os.path.exists(CORRUPT_PATH):
        print(f"{CORRUPT_PATH} already exists; leaving it alone (--force rebuilds it).")
        return

    rnd = random.Random(13)
    db = sqlite3.connect(CORRUPT_PATH)
    db.execute("PRAGMA journal_mode = DELETE")
    db.executescript(CORRUPT_SCHEMA)
    db.executemany("INSERT INTO items(id, name, qty) VALUES (?, ?, ?)",
                   [(i, f"item-{i:04d}", rnd.randint(0, 50)) for i in range(1, CORRUPT_ITEMS + 1)])
    db.executemany("INSERT INTO item_notes(item_id, note) VALUES (?, ?)",
                   [(rnd.randint(1, CORRUPT_ITEMS), f"note {n}") for n in range(1, 61)])
    # Python's sqlite3 leaves foreign_keys OFF, so these orphans go in unchallenged.
    db.executemany("INSERT INTO item_notes(id, item_id, note) VALUES (?, ?, ?)", CORRUPT_ORPHANS)
    db.commit()
    page_size = db.execute("PRAGMA page_size").fetchone()[0]
    root = db.execute("SELECT rootpage FROM sqlite_schema WHERE name = 'idx_items_name'").fetchone()[0]
    db.close()

    # Damage only the index's key bytes, never the page header or cell pointers: rewriting
    # "item-" to "ITEM-" in a handful of cells leaves the b-tree walkable (so REINDEX can clear and
    # rebuild it) but makes those entries both out of order and missing for their table rows.
    with open(CORRUPT_PATH, "r+b") as f:
        f.seek((root - 1) * page_size)
        page = bytearray(f.read(page_size))
        if page[0] != 0x0A:
            raise SystemExit(f"corrupt.db: expected idx_items_name root page {root} to be an index leaf (0x0A), got {page[0]:#x}")
        offsets, at = [], 0
        while (at := page.find(b"item-", at + 1)) != -1:
            offsets.append(at)
        for at in offsets[10:16]:
            page[at:at + 5] = b"ITEM-"
        f.seek((root - 1) * page_size)
        f.write(page)

    problems = check_integrity(CORRUPT_PATH)
    if problems == ["ok"]:
        raise SystemExit("corrupt.db: integrity_check still says ok after damaging the index")
    with tempfile.TemporaryDirectory() as scratch:
        copy = os.path.join(scratch, "corrupt-copy.db")
        shutil.copyfile(CORRUPT_PATH, copy)
        fix = sqlite3.connect(copy)
        fix.execute("REINDEX")
        fix.commit()
        fix.close()
        if check_integrity(copy) != ["ok"]:
            raise SystemExit("corrupt.db: REINDEX on a copy did not make integrity_check ok")

    os.chmod(CORRUPT_PATH, 0o666)
    print(f"Wrote {CORRUPT_PATH} ({os.path.getsize(CORRUPT_PATH) / 1024:.0f} KB; integrity_check reports "
          f"{len(problems)} problem(s) that REINDEX fixes, {len(CORRUPT_ORPHANS)} FK-violating orphan rows).")


# --------------------------------------------------------------------------------------------------
# reference.db
# --------------------------------------------------------------------------------------------------
REFERENCE_SCHEMA = """
CREATE TABLE currencies(code TEXT PRIMARY KEY, numeric_code TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
                        minor_units INTEGER NOT NULL CHECK (minor_units BETWEEN 0 AND 4)) WITHOUT ROWID;
CREATE TABLE countries(alpha2 TEXT PRIMARY KEY, alpha3 TEXT NOT NULL UNIQUE, numeric_code TEXT NOT NULL UNIQUE,
                       name TEXT NOT NULL, region TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE country_currencies(country TEXT NOT NULL REFERENCES countries(alpha2) ON DELETE CASCADE,
                                currency TEXT NOT NULL REFERENCES currencies(code) ON DELETE RESTRICT,
                                is_primary INTEGER NOT NULL DEFAULT 1,
                                PRIMARY KEY(country, currency));
CREATE INDEX idx_country_currencies_currency ON country_currencies(currency);
CREATE INDEX idx_countries_region ON countries(region);
"""
CURRENCIES = [
    ("AUD", "036", "Australian dollar", 2), ("BRL", "986", "Brazilian real", 2), ("CAD", "124", "Canadian dollar", 2),
    ("CHF", "756", "Swiss franc", 2), ("CNY", "156", "Renminbi", 2), ("CZK", "203", "Czech koruna", 2),
    ("DKK", "208", "Danish krone", 2), ("EGP", "818", "Egyptian pound", 2), ("EUR", "978", "Euro", 2),
    ("GBP", "826", "Pound sterling", 2), ("HKD", "344", "Hong Kong dollar", 2), ("HUF", "348", "Hungarian forint", 2),
    ("INR", "356", "Indian rupee", 2), ("JPY", "392", "Japanese yen", 0), ("KES", "404", "Kenyan shilling", 2),
    ("KRW", "410", "South Korean won", 0), ("KWD", "414", "Kuwaiti dinar", 3), ("MXN", "484", "Mexican peso", 2),
    ("NGN", "566", "Nigerian naira", 2), ("NOK", "578", "Norwegian krone", 2), ("NZD", "554", "New Zealand dollar", 2),
    ("PLN", "985", "Polish zloty", 2), ("SAR", "682", "Saudi riyal", 2), ("SEK", "752", "Swedish krona", 2),
    ("SGD", "702", "Singapore dollar", 2), ("TRY", "949", "Turkish lira", 2), ("USD", "840", "United States dollar", 2),
    ("ZAR", "710", "South African rand", 2), ("AED", "784", "UAE dirham", 2), ("ARS", "032", "Argentine peso", 2),
]
# (alpha2, alpha3, numeric, name, region, [currencies -- first is primary])
COUNTRIES = [
    ("AR", "ARG", "032", "Argentina", "Americas", ["ARS"]), ("AT", "AUT", "040", "Austria", "Europe", ["EUR"]),
    ("AU", "AUS", "036", "Australia", "Oceania", ["AUD"]), ("BE", "BEL", "056", "Belgium", "Europe", ["EUR"]),
    ("BR", "BRA", "076", "Brazil", "Americas", ["BRL"]), ("CA", "CAN", "124", "Canada", "Americas", ["CAD"]),
    ("CH", "CHE", "756", "Switzerland", "Europe", ["CHF"]), ("CN", "CHN", "156", "China", "Asia", ["CNY"]),
    ("CZ", "CZE", "203", "Czechia", "Europe", ["CZK"]), ("DE", "DEU", "276", "Germany", "Europe", ["EUR"]),
    ("DK", "DNK", "208", "Denmark", "Europe", ["DKK"]), ("EG", "EGY", "818", "Egypt", "Africa", ["EGP"]),
    ("ES", "ESP", "724", "Spain", "Europe", ["EUR"]), ("FI", "FIN", "246", "Finland", "Europe", ["EUR"]),
    ("FR", "FRA", "250", "France", "Europe", ["EUR"]), ("GB", "GBR", "826", "United Kingdom", "Europe", ["GBP"]),
    ("GR", "GRC", "300", "Greece", "Europe", ["EUR"]), ("HK", "HKG", "344", "Hong Kong", "Asia", ["HKD"]),
    ("HU", "HUN", "348", "Hungary", "Europe", ["HUF"]), ("IE", "IRL", "372", "Ireland", "Europe", ["EUR"]),
    ("IN", "IND", "356", "India", "Asia", ["INR"]), ("IT", "ITA", "380", "Italy", "Europe", ["EUR"]),
    ("JP", "JPN", "392", "Japan", "Asia", ["JPY"]), ("KE", "KEN", "404", "Kenya", "Africa", ["KES"]),
    ("KR", "KOR", "410", "South Korea", "Asia", ["KRW"]), ("KW", "KWT", "414", "Kuwait", "Asia", ["KWD"]),
    ("LI", "LIE", "438", "Liechtenstein", "Europe", ["CHF"]), ("MX", "MEX", "484", "Mexico", "Americas", ["MXN"]),
    ("NG", "NGA", "566", "Nigeria", "Africa", ["NGN"]), ("NL", "NLD", "528", "Netherlands", "Europe", ["EUR"]),
    ("NO", "NOR", "578", "Norway", "Europe", ["NOK"]), ("NZ", "NZL", "554", "New Zealand", "Oceania", ["NZD"]),
    ("PL", "POL", "616", "Poland", "Europe", ["PLN"]), ("PT", "PRT", "620", "Portugal", "Europe", ["EUR"]),
    ("SA", "SAU", "682", "Saudi Arabia", "Asia", ["SAR"]), ("SE", "SWE", "752", "Sweden", "Europe", ["SEK"]),
    ("SG", "SGP", "702", "Singapore", "Asia", ["SGD"]), ("TR", "TUR", "792", "Türkiye", "Asia", ["TRY"]),
    ("AE", "ARE", "784", "United Arab Emirates", "Asia", ["AED"]), ("US", "USA", "840", "United States", "Americas", ["USD"]),
    ("ZA", "ZAF", "710", "South Africa", "Africa", ["ZAR"]), ("EC", "ECU", "218", "Ecuador", "Americas", ["USD"]),
    ("PA", "PAN", "591", "Panama", "Americas", ["USD"]), ("ZW", "ZWE", "716", "Zimbabwe", "Africa", ["USD", "ZAR"]),
    ("ME", "MNE", "499", "Montenegro", "Europe", ["EUR"]), ("KI", "KIR", "296", "Kiribati", "Oceania", ["AUD"]),
]


def seed_reference() -> None:
    if os.path.exists(REFERENCE_PATH):
        print(f"{REFERENCE_PATH} already exists; leaving it alone (--force rebuilds it).")
        return

    db = sqlite3.connect(REFERENCE_PATH)
    db.execute("PRAGMA journal_mode = DELETE")
    db.executescript(REFERENCE_SCHEMA)
    db.executemany("INSERT INTO currencies(code, numeric_code, name, minor_units) VALUES (?, ?, ?, ?)", CURRENCIES)
    db.executemany("INSERT INTO countries(alpha2, alpha3, numeric_code, name, region) VALUES (?, ?, ?, ?, ?)",
                   [c[:5] for c in COUNTRIES])
    links = [(c[0], cur, 1 if i == 0 else 0) for c in COUNTRIES for i, cur in enumerate(c[5])]
    db.executemany("INSERT INTO country_currencies(country, currency, is_primary) VALUES (?, ?, ?)", links)
    db.commit()
    db.execute("ANALYZE")
    db.commit()
    db.close()

    os.chmod(REFERENCE_PATH, 0o666)
    print(f"Wrote {REFERENCE_PATH} ({len(COUNTRIES)} countries, {len(CURRENCIES)} currencies, {len(links)} links).")


if __name__ == "__main__":
    main()
