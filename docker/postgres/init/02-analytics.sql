\set ON_ERROR_STOP on
CREATE DATABASE analytics;
\c analytics
CREATE TABLE daily_orders (day date PRIMARY KEY, orders integer NOT NULL, revenue_cents bigint NOT NULL);
INSERT INTO daily_orders
SELECT current_date - g, 400 + (g * 17) % 300, (400 + (g * 17) % 300) * 4200
FROM generate_series(0, 364) g;
