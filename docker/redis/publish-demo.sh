#!/bin/sh
# Live traffic for the Redis plugin's demo stack, every 5s:
#   - Pub/Sub has no history (a subscriber only sees messages published while subscribed), so
#     this publishes a heartbeat to notifications:live and, every third beat, orders:events.
#   - orders:stream gets a new entry, and the healthy consumers (fulfil-1, billing-7c9f) read and
#     ack it -- so "last id Ns ago" and their idle times stay fresh while billing-2a41 stays stalled.
set -eu

R="redis-cli -h redis -a $REDIS_PASSWORD --no-auth-warning"

# Reads one new entry for <group> <consumer> and acks it. XREADGROUP's raw reply is
# stream name, entry id, then field/value lines -- the id is the second line.
consume_one() {
  id=$($R XREADGROUP GROUP "$1" "$2" COUNT 1 STREAMS orders:stream '>' | sed -n 2p)
  if [ -n "$id" ]; then $R XACK orders:stream "$1" "$id" >/dev/null; fi
}

echo "Publishing demo Pub/Sub + stream traffic every 5s..."

i=0
while true; do
  i=$((i + 1))
  $R PUBLISH notifications:live "{\"seq\":$i,\"text\":\"heartbeat $i\",\"at\":\"$(date -u +%FT%TZ)\"}" >/dev/null
  if [ $((i % 3)) -eq 0 ]; then
    $R PUBLISH orders:events "{\"seq\":$i,\"orderId\":$((1000 + i)),\"status\":\"created\"}" >/dev/null
  fi

  $R XADD orders:stream MAXLEN '~' 5000 '*' orderRef "UK-2026-$((449000 + i))" event order.created amount "$((20 + i % 300)).40" currency GBP >/dev/null
  consume_one fulfilment fulfil-1
  consume_one billing billing-7c9f

  sleep 5
done
