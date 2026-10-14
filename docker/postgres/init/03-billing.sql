\set ON_ERROR_STOP on
CREATE DATABASE billing;
\c billing
CREATE TABLE invoices (
    id bigserial PRIMARY KEY,
    customer_ref text NOT NULL,
    amount_cents bigint NOT NULL,
    issued_at timestamptz NOT NULL DEFAULT now(),
    paid boolean NOT NULL DEFAULT false
);
INSERT INTO invoices (customer_ref, amount_cents, issued_at, paid)
SELECT 'C-' || (g % 500), 1000 + (g * 97) % 50000, now() - (g % 120) * interval '1 day', g % 4 <> 0
FROM generate_series(1, 2000) g;
