-- Showcase data for checking every PostgreSQL plugin feature by hand (docker/README.md
-- "PostgreSQL" → "What to try"). Runs on first init of the postgres-data volume, after 01–03, in
-- database app. Safe to re-run on an existing server:
--   docker compose --profile postgres exec -T postgres psql -U sbconsole -d app -f /docker-entrypoint-initdb.d/04-showcase.sql
\set ON_ERROR_STOP on

DROP SCHEMA IF EXISTS showcase CASCADE;
DROP SCHEMA IF EXISTS wide CASCADE;
CREATE SCHEMA showcase;
COMMENT ON SCHEMA showcase IS 'Edge cases for the SbConsole PostgreSQL plugin';

-- ---------------------------------------------------------------------------------------------
-- Browse → Data: one row per cell kind the grid has to render (NULL chip, jsonb key counts, bytea,
-- arrays, values Npgsql can't map to .NET such as interval months / NaN / BC dates, long text,
-- unicode, HTML that must stay text).
-- ---------------------------------------------------------------------------------------------
CREATE TYPE showcase.mood AS ENUM ('happy', 'sad', 'meh');

CREATE TABLE showcase.type_zoo (
    id            integer PRIMARY KEY,
    label         text NOT NULL,
    note          text,
    doc           jsonb,
    doc_json      json,
    tags          text[],
    scores        integer[],
    blob          bytea,
    amount        numeric,
    price         numeric(10, 2),
    ratio         double precision,
    span          interval,
    born          date,
    seen_at       timestamptz,
    local_at      timestamp,
    uid           uuid,
    addr          inet,
    code          char(5),
    flags         bit(3),
    active        boolean,
    feeling       showcase.mood,
    "MixedCase"   text,
    "we""ird col" text
);
COMMENT ON TABLE showcase.type_zoo IS 'One row per tricky cell kind for the Data tab';
COMMENT ON COLUMN showcase.type_zoo.span IS 'Intervals with months can''t be read as a .NET TimeSpan';
COMMENT ON COLUMN showcase.type_zoo.amount IS 'NaN, Infinity and 1e40 overflow System.Decimal';

INSERT INTO showcase.type_zoo VALUES
 (1, 'plain values', 'hello', '{"a": 1, "b": 2, "c": {"d": [1, 2, 3]}}', '{"x": true}', '{alpha,beta}', '{1,2,3}',
  '\x48656c6c6f', 12.5, 9.99, 0.25, '2 hours 30 minutes', '2024-02-29', '2026-10-14 08:41:02+00', '2026-10-14 08:41:02',
  'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '10.0.4.18', 'ab', B'101', true, 'happy', 'mixed', 'weird'),
 (2, 'all nulls', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
  NULL, NULL, NULL, NULL),
 (3, 'unmappable to .NET', 'interval with months, NaN, 1e40, BC date', '[]', '[]', '{}', '{}', '\x', 'NaN',
  NULL, 'Infinity', '1 year 2 mons 3 days', '0044-03-15 BC', 'infinity', '-infinity', NULL, '::1', NULL, B'000',
  false, 'meh', NULL, NULL),
 (4, 'huge numeric', NULL, NULL, NULL, NULL, NULL, NULL, 1e40, 99999999.99, 1e300, '-5 days', '9999-12-31', NULL,
  NULL, NULL, '2001:db8::1/64', 'zzzzz', B'111', NULL, 'sad', NULL, NULL),
 (5, 'unicode and HTML', 'José — 日本語 — 🐘 <b>not bold</b> <script>alert(1)</script>',
  '{"name": "José", "note": "it''s <b>", "emoji": "🐘"}', '{"html": "<i>x</i>"}', '{"a,b","c\"d"}', '{-1,0,1}',
  '\x00ff00ff', -0.5, 0.01, -1e-10, '1 day', '1970-01-01', '1970-01-01 00:00:00+00', '1970-01-01 00:00:00',
  '00000000-0000-0000-0000-000000000000', '192.168.0.1', 'é', B'010', true, 'happy', 'ÄÖÜ', 'tab	inside'),
 (6, 'long and nested', repeat('long text that the grid truncates at 200 characters. ', 40),
  (SELECT jsonb_object_agg('key' || g, jsonb_build_object('n', g, 'list', jsonb_build_array(g, g * 2))) FROM generate_series(1, 25) g),
  '[1, [2, [3, [4]]]]', (SELECT array_agg('tag' || g) FROM generate_series(1, 30) g),
  (SELECT array_agg(g) FROM generate_series(1, 100) g),
  decode(repeat('deadbeef', 4096), 'hex'), 123456789.123456789, 1234.56, 3.14159, '36 hours',
  '2000-01-01', now(), now()::timestamp, 'f47ac10b-58cc-4372-a567-0e02b2c3d479', '127.0.0.1', 'xy', B'110',
  false, 'meh', repeat('x', 300), NULL);

-- Filter targets: NULL / NOT NULL (half the rows), @> on jsonb and arrays, LIKE / ILIKE, ranges.
CREATE TABLE showcase.filter_me (
    id         bigserial PRIMARY KEY,
    name       text NOT NULL,
    city       text,
    tags       text[] NOT NULL DEFAULT '{}',
    attrs      jsonb NOT NULL DEFAULT '{}',
    score      numeric(6, 2) NOT NULL,
    created_at timestamptz NOT NULL
);
INSERT INTO showcase.filter_me (name, city, tags, attrs, score, created_at)
SELECT 'Item ' || g,
       CASE WHEN g % 2 = 0 THEN NULL ELSE (ARRAY['Berlin', 'berlin', 'Paris', 'São Paulo', 'Zürich'])[1 + g % 5] END,
       CASE g % 3 WHEN 0 THEN ARRAY['red', 'big'] WHEN 1 THEN ARRAY['blue', 'small'] ELSE ARRAY['red', 'small'] END,
       jsonb_build_object('vip', g % 7 = 0, 'tier', (ARRAY['gold', 'silver', 'bronze'])[1 + g % 3], 'n', g),
       (g * 37 % 10000) / 100.0,
       now() - g * interval '37 minutes'
FROM generate_series(1, 5000) g;
CREATE INDEX filter_me_attrs_gin ON showcase.filter_me USING gin (attrs);
CREATE INDEX filter_me_lower_name ON showcase.filter_me (lower(name));        -- expression index
CREATE INDEX filter_me_city_partial ON showcase.filter_me (city) WHERE city IS NOT NULL;

-- Identifiers that need quoting everywhere (tree, DDL, Open in Query, VACUUM target).
CREATE TABLE showcase."Mixed Case Table" (
    "Id"        integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Full Name" text NOT NULL,
    "select"    text            -- a reserved word as a column name
);
INSERT INTO showcase."Mixed Case Table" ("Full Name", "select")
SELECT 'Person ' || g, 'kw' || g FROM generate_series(1, 50) g;

-- ---------------------------------------------------------------------------------------------
-- Browse → Columns / Constraints / DDL: generated + identity columns, every constraint kind,
-- foreign keys with actions, comments, defaults.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE showcase.rooms (
    id   integer PRIMARY KEY,
    name text NOT NULL UNIQUE
);
INSERT INTO showcase.rooms SELECT g, 'Room ' || g FROM generate_series(1, 5) g;

CREATE TABLE showcase.bookings (
    id          bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    room_id     integer NOT NULL REFERENCES showcase.rooms (id) ON DELETE CASCADE,
    guest_email text NOT NULL CHECK (guest_email LIKE '%@%'),
    during      tstzrange NOT NULL,
    nights      integer GENERATED ALWAYS AS (greatest(1, extract(day FROM upper(during) - lower(during))::integer)) STORED,
    price_cents integer NOT NULL DEFAULT 10000 CHECK (price_cents > 0),
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT bookings_guest_room_unique UNIQUE (room_id, guest_email),
    CONSTRAINT bookings_no_overlap EXCLUDE USING gist (during WITH &&)
);
COMMENT ON TABLE showcase.bookings IS 'Exclusion, check, unique, FK cascade, identity and generated columns';
COMMENT ON COLUMN showcase.bookings.nights IS 'Generated column: GENERATED ALWAYS AS (...) STORED';
INSERT INTO showcase.bookings (room_id, guest_email, during)
SELECT 1 + g % 5, 'guest' || g || '@example.com',
       tstzrange(now() + g * interval '3 days', now() + g * interval '3 days' + interval '2 days')
FROM generate_series(1, 200) g;

-- Drop index is refused for indexes that back a constraint (bookings_guest_room_unique, the PK);
-- this one is free to drop and is never used.
CREATE INDEX bookings_created_unused ON showcase.bookings (created_at);

-- ---------------------------------------------------------------------------------------------
-- Partitioned table: only the parent shows in the tree (partitions are hidden); partition-level
-- indexes (relkind 'I') are a known limitation for REINDEX.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE showcase.metrics (
    ts    timestamptz NOT NULL,
    name  text NOT NULL,
    value double precision NOT NULL
) PARTITION BY RANGE (ts);
CREATE TABLE showcase.metrics_past   PARTITION OF showcase.metrics FOR VALUES FROM (MINVALUE) TO (date_trunc('month', now()));
CREATE TABLE showcase.metrics_now    PARTITION OF showcase.metrics FOR VALUES FROM (date_trunc('month', now())) TO (date_trunc('month', now()) + interval '1 month');
CREATE TABLE showcase.metrics_future PARTITION OF showcase.metrics FOR VALUES FROM (date_trunc('month', now()) + interval '1 month') TO (MAXVALUE);
CREATE INDEX metrics_name_ts ON showcase.metrics (name, ts);
INSERT INTO showcase.metrics
SELECT now() - g * interval '10 minutes' + interval '20 days', (ARRAY['cpu', 'mem', 'disk'])[1 + g % 3], random() * 100
FROM generate_series(1, 20000) g;

-- Views, materialized view, function and procedure in the same schema.
CREATE VIEW showcase.v_vip AS
    SELECT id, name, city, attrs ->> 'tier' AS tier FROM showcase.filter_me WHERE (attrs ->> 'vip')::boolean;
CREATE MATERIALIZED VIEW showcase.mv_city_counts AS
    SELECT coalesce(city, '(none)') AS city, count(*) AS n FROM showcase.filter_me GROUP BY 1;
CREATE FUNCTION showcase.slow_echo(p_text text, p_seconds double precision DEFAULT 1)
    RETURNS text LANGUAGE plpgsql AS
    $$ BEGIN PERFORM pg_sleep(p_seconds); RETURN p_text; END $$;
CREATE PROCEDURE showcase.touch_room(p_id integer) LANGUAGE sql AS
    $$ UPDATE showcase.rooms SET name = name WHERE id = p_id $$;

-- ---------------------------------------------------------------------------------------------
-- Maintenance: a small, very bloated table (fast to VACUUM FULL) and one with only stale stats.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE showcase.churn (
    id      integer PRIMARY KEY,
    payload text NOT NULL
) WITH (autovacuum_enabled = false, fillfactor = 100);
INSERT INTO showcase.churn SELECT g, repeat(md5(g::text), 4) FROM generate_series(1, 30000) g;
UPDATE showcase.churn SET payload = payload || 'x';      -- every row now has a dead version
UPDATE showcase.churn SET payload = payload || 'y' WHERE id % 2 = 0;
DELETE FROM showcase.churn WHERE id % 3 = 0;

CREATE TABLE showcase.never_analyzed (id integer, v text) WITH (autovacuum_enabled = false);
INSERT INTO showcase.never_analyzed SELECT g, 'v' || g FROM generate_series(1, 10000) g;

-- ---------------------------------------------------------------------------------------------
-- Browse tree: a schema with more than 50 tables, so it collapses to "N more…" and the filter
-- box has something to narrow.
-- ---------------------------------------------------------------------------------------------
CREATE SCHEMA wide;
DO $$
BEGIN
    FOR i IN 1..75 LOOP
        EXECUTE format('CREATE TABLE wide.%I (id integer PRIMARY KEY, v text)', 't_' || lpad(i::text, 3, '0'));
        EXECUTE format('INSERT INTO wide.%I SELECT g, ''v'' || g FROM generate_series(1, %s) g',
                       't_' || lpad(i::text, 3, '0'), i * 10);
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------------------------
-- A plain, non-superuser role for the "pg-local-limited" demo connection: read-only grants in
-- app, no pg_monitor (so other sessions' queries are hidden on Activity), and no CONNECT on
-- billing (the Overview databases table shows it with an unknown size instead of failing).
-- ---------------------------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_ro') THEN
        CREATE ROLE app_ro LOGIN PASSWORD 'app_ro';
    END IF;
END $$;
GRANT CONNECT ON DATABASE app TO app_ro;
GRANT USAGE ON SCHEMA public, audit, reporting, showcase, wide TO app_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public, audit, reporting, showcase, wide TO app_ro;
REVOKE CONNECT ON DATABASE billing FROM PUBLIC;

ANALYZE showcase.type_zoo, showcase.filter_me, showcase."Mixed Case Table", showcase.rooms,
        showcase.bookings, showcase.metrics, showcase.churn;
