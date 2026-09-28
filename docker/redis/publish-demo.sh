#!/bin/sh
# Redis Pub/Sub has no history: a subscriber only ever sees messages published while it is
# subscribed. Unlike the other data types, there is nothing to seed once at startup -- so this
# script runs continuously (see the `redis-pubsub-demo` compose service), publishing a heartbeat
# every few seconds to a couple of channels, purely so the Pub/Sub page's live tail has something
# to show up without the user needing a second terminal open running `redis-cli publish`.
set -eu

R="redis-cli -h redis -a $REDIS_PASSWORD --no-auth-warning"

echo "Publishing demo Pub/Sub traffic to notifications:live and orders:events every 5s..."

i=0
while true; do
  i=$((i + 1))
  $R PUBLISH notifications:live "{\"seq\":$i,\"text\":\"heartbeat $i\",\"at\":\"$(date -u +%FT%TZ)\"}" >/dev/null
  if [ $((i % 3)) -eq 0 ]; then
    $R PUBLISH orders:events "{\"seq\":$i,\"orderId\":$((1000 + i)),\"status\":\"created\"}" >/dev/null
  fi
  sleep 5
done
