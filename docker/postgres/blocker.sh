#!/bin/sh
# Keeps live sessions around for the Activity page (docker/README.md "PostgreSQL" → "What to try"):
#   - a lock chain: one session idle in transaction holding a row lock on orders.id = 1
#     (checkout-api) and two sessions waiting on it (checkout-api, fulfilment-worker); the cycle
#     restarts every 15 minutes, so Terminate can be tried again later;
#   - a long-running active query (reporting) that the Cancel button can stop; restarts after
#     it ends or is cancelled;
#   - two idle pooled connections (idle-pool), which Activity hides behind "show all".
# Never exits on error.
export PGHOST="${PGHOST:-postgres}" PGUSER="${PGUSER:-sbconsole}" PGDATABASE="${PGDATABASE:-app}"

long_running() {
  while true; do
    PGAPPNAME=reporting psql -q -X -c "SELECT count(*) FROM orders o, pg_sleep(600) WHERE o.status = 'pending'" >/dev/null 2>&1
    sleep 5
  done
}

idle_pool() {
  while true; do
    # "\! sleep" keeps the connection open and idle (not in a transaction).
    { echo "SELECT 1;"; echo '\! sleep 3600'; } | PGAPPNAME=idle-pool psql -q -X >/dev/null 2>&1
    sleep 5
  done
}

long_running &
idle_pool &
idle_pool &

while true; do
  # Holder: psql reads from a pipe; after UPDATE, "\! sleep" keeps the transaction open and the
  # session idle in transaction.
  {
    echo "BEGIN;"
    echo "UPDATE orders SET status = 'paid' WHERE id = 1;"
    echo '\! sleep 900'
    echo "ROLLBACK;"
  } | PGAPPNAME=checkout-api psql -q -X -v ON_ERROR_STOP=0 &
  holder=$!
  sleep 3
  PGAPPNAME=checkout-api psql -q -X -c "UPDATE orders SET status = 'cancelled' WHERE id = 1" &
  w1=$!
  PGAPPNAME=fulfilment-worker psql -q -X -c "SELECT * FROM orders WHERE id = 1 FOR UPDATE" &
  w2=$!
  wait "$holder" 2>/dev/null
  kill "$w1" "$w2" 2>/dev/null
  wait "$w1" "$w2" 2>/dev/null
  sleep 2
done
