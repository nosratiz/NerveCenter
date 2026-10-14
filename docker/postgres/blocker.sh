#!/bin/sh
# Keeps a lock-contention scenario alive for the Activity page: one session idle in transaction
# holding a row lock (checkout-api) and two sessions waiting on it (checkout-api,
# fulfilment-worker). Restarts the cycle every 15 minutes. Never exits on error.
export PGHOST="${PGHOST:-postgres}" PGUSER="${PGUSER:-sbconsole}" PGDATABASE="${PGDATABASE:-app}"

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
  wait 2>/dev/null
  sleep 2
done
