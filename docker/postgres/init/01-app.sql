-- Demo database "app" for the SbConsole PostgreSQL plugin. Runs once, on first init of the
-- postgres-data volume (docker-entrypoint-initdb.d), as the sbconsole superuser, in database app.
\set ON_ERROR_STOP on

CREATE EXTENSION IF NOT EXISTS pgstattuple;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

CREATE TABLE customers (
    id          bigserial PRIMARY KEY,
    email       text NOT NULL UNIQUE,
    full_name   text NOT NULL,
    country     char(2) NOT NULL,
    created_at  timestamptz NOT NULL
);
INSERT INTO customers (email, full_name, country, created_at)
SELECT 'user' || g || '@example.com', 'Customer ' || g,
       (ARRAY['US','GB','DE','FR','NL','SE','CA','AU'])[1 + g % 8],
       now() - (g % 700) * interval '1 day'
FROM generate_series(1, 20000) g;

CREATE TABLE products (
    id          bigserial PRIMARY KEY,
    sku         text NOT NULL UNIQUE,
    name        text NOT NULL,
    price_cents integer NOT NULL CHECK (price_cents >= 0),
    active      boolean NOT NULL DEFAULT true
);
INSERT INTO products (sku, name, price_cents, active)
SELECT 'SKU-' || lpad(g::text, 5, '0'), 'Product ' || g, 199 + (g * 37) % 49800, g % 25 <> 0
FROM generate_series(1, 2000) g;

CREATE TABLE orders (
    id          bigserial PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES customers (id),
    status      text NOT NULL,
    currency    char(3) NOT NULL DEFAULT 'USD',
    legacy_ref  text,
    total_cents bigint NOT NULL,
    metadata    jsonb NOT NULL DEFAULT '{}',
    created_at  timestamptz NOT NULL
);
INSERT INTO orders (customer_id, status, currency, legacy_ref, total_cents, metadata, created_at)
SELECT 1 + (g * 7919) % 20000,
       CASE WHEN g % 50 = 0 THEN 'pending' WHEN g % 20 = 1 THEN 'cancelled'
            WHEN g % 10 < 3 THEN 'shipped' ELSE 'paid' END,
       (ARRAY['USD','USD','USD','EUR','GBP'])[1 + g % 5],
       'L-' || g,
       500 + (g * 131) % 90000,
       jsonb_build_object('channel', (ARRAY['web','ios','android','partner'])[1 + g % 4],
                          'coupon', CASE WHEN g % 9 = 0 THEN 'SPRING' END,
                          'gift', g % 33 = 0),
       now() - (g % 400) * interval '1 day' - (g % 86400) * interval '1 second'
FROM generate_series(1, 200000) g;
CREATE INDEX idx_orders_customer_created ON orders (customer_id, created_at DESC);
CREATE INDEX idx_orders_status_pending ON orders (created_at) WHERE status = 'pending';
CREATE INDEX idx_orders_metadata_gin ON orders USING gin (metadata);
CREATE INDEX idx_orders_legacy_ref ON orders (legacy_ref);  -- never queried: shows up as unused
CREATE INDEX idx_orders_currency ON orders (currency);

CREATE TABLE order_items (
    id          bigserial PRIMARY KEY,
    order_id    bigint NOT NULL REFERENCES orders (id),
    product_id  bigint NOT NULL REFERENCES products (id),
    quantity    integer NOT NULL,
    unit_cents  integer NOT NULL
);
INSERT INTO order_items (order_id, product_id, quantity, unit_cents)
SELECT 1 + g % 200000, 1 + (g * 31) % 2000, 1 + g % 4, 199 + (g * 37) % 49800
FROM generate_series(1, 400000) g;
CREATE INDEX idx_order_items_order ON order_items (order_id);

CREATE TABLE payments (
    id          bigserial PRIMARY KEY,
    order_id    bigint NOT NULL REFERENCES orders (id),
    provider    text NOT NULL,
    amount_cents bigint NOT NULL,
    settled_at  timestamptz NOT NULL
);
INSERT INTO payments (order_id, provider, amount_cents, settled_at)
SELECT 1 + (g * 3) % 200000, (ARRAY['stripe','adyen','paypal'])[1 + g % 3],
       500 + (g * 131) % 90000, now() - (g % 400) * interval '1 day'
FROM generate_series(1, 150000) g;
CREATE INDEX idx_payments_order ON payments (order_id);

CREATE TABLE sessions (
    id          bigserial PRIMARY KEY,
    customer_id bigint NOT NULL,
    token       text NOT NULL,
    last_seen   timestamptz NOT NULL
);
INSERT INTO sessions (customer_id, token, last_seen)
SELECT 1 + g % 20000, md5(g::text), now() - (g % 30) * interval '1 day'
FROM generate_series(1, 50000) g;

CREATE TABLE events (
    id          bigserial PRIMARY KEY,
    kind        text NOT NULL,
    payload     jsonb NOT NULL,
    created_at  timestamptz NOT NULL
);
INSERT INTO events (kind, payload, created_at)
SELECT (ARRAY['page_view','add_to_cart','checkout','login'])[1 + g % 4],
       jsonb_build_object('n', g), now() - (g % 90) * interval '1 hour'
FROM generate_series(1, 300000) g;
CREATE INDEX idx_events_created ON events (created_at);

CREATE SCHEMA audit;
CREATE TABLE audit.changes (
    id         bigserial PRIMARY KEY,
    table_name text NOT NULL,
    row_id     bigint NOT NULL,
    action     text NOT NULL,
    changed_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO audit.changes (table_name, row_id, action)
SELECT 'orders', g, (ARRAY['insert','update','delete'])[1 + g % 3] FROM generate_series(1, 1000) g;

CREATE SCHEMA reporting;
CREATE VIEW reporting.v_order_totals AS
    SELECT o.id AS order_id, o.customer_id, o.status,
           coalesce(sum(i.quantity * i.unit_cents), 0) AS items_cents
    FROM orders o LEFT JOIN order_items i ON i.order_id = o.id
    GROUP BY o.id, o.customer_id, o.status;
CREATE MATERIALIZED VIEW reporting.mv_daily AS
    SELECT date_trunc('day', created_at) AS day, count(*) AS orders, sum(total_cents) AS revenue_cents
    FROM orders GROUP BY 1;

CREATE FUNCTION order_total(p_order bigint) RETURNS bigint
    LANGUAGE sql STABLE AS
    $$ SELECT coalesce(sum(quantity * unit_cents), 0)::bigint FROM order_items WHERE order_id = p_order $$;

-- Dead tuples the Maintenance page can show: autovacuum off, then delete 40 %.
ALTER TABLE sessions SET (autovacuum_enabled = false);
ALTER TABLE order_items SET (autovacuum_enabled = false);
DELETE FROM sessions WHERE id % 5 < 2;
DELETE FROM order_items WHERE id % 5 < 2;

ANALYZE;
