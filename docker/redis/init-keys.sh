#!/bin/sh
# Seeds the Redis instance with sample keys across the data types the Redis plugin's mockup
# shows, so the Overview and Keys pages have something to look at without hand-seeding.
# Runs once from the `redis-init` compose service. Idempotent: HSET/SADD/ZADD overwrite the
# same fields/members in place; the one exception is the retry list (RPUSH only appends), so
# that section checks LLEN first rather than growing on every `docker compose up`.
set -eu

R="redis-cli -h redis -a $REDIS_PASSWORD --no-auth-warning"

echo "==> session hashes (session:u:*), one with no TTL on purpose"
$R HSET session:u:77120 userId 77120 tenant acme-uk lastSeen "$(date -u +%FT%TZ)" >/dev/null
# No EXPIRE on this one -- the Overview page's "session:* has 38% no-TTL keys" story needs at
# least one that never expires.

$R HSET session:u:77121 userId 77121 tenant acme-uk lastSeen "$(date -u +%FT%TZ)" >/dev/null
$R EXPIRE session:u:77121 1800 >/dev/null

echo "==> retry job list (jobs:email:retry), read head-to-tail"
if [ "$($R LLEN jobs:email:retry)" = "0" ]; then
  $R RPUSH jobs:email:retry \
    '{"to":"a@acme.example","try":3}' \
    '{"to":"k@acme.example","try":1}' \
    '{"to":"r@acme.example","try":5}' >/dev/null
fi

echo "==> weekly leaderboard (lb:weekly), ranked sorted set"
$R ZADD lb:weekly 9812 team-orion 8044 team-lyra 6970 team-vela 5402 team-ara >/dev/null

echo "==> beta feature flag membership (flags:beta), unordered set"
$R SADD flags:beta acme-uk northwind globex initech umbrella >/dev/null

echo "==> order events stream (events:orders), with a consumer group + pending entries"
# EXISTS-gated (not idempotent-per-call like XADD would need '*' IDs tracked) so re-running
# `docker compose up -d` doesn't keep growing the stream or re-creating the group.
if [ "$($R EXISTS events:orders)" = "0" ]; then
  $R XADD events:orders '*' orderId 1001 status created >/dev/null
  $R XADD events:orders '*' orderId 1002 status created >/dev/null
  $R XADD events:orders '*' orderId 1001 status paid >/dev/null
  $R XADD events:orders '*' orderId 1003 status created >/dev/null
  $R XADD events:orders '*' orderId 1002 status paid >/dev/null
  $R XGROUP CREATE events:orders order-processors 0 >/dev/null
  # Read (but deliberately don't ack) a couple of entries into the PEL, so the Streams page's
  # pending-entries table has real rows to claim/ack/delete against out of the box.
  $R XREADGROUP GROUP order-processors worker-1 COUNT 2 STREAMS events:orders '>' >/dev/null
fi

echo "==> app log stream (logs:app), no consumer group -- exercises the 'no groups yet' state"
if [ "$($R EXISTS logs:app)" = "0" ]; then
  $R XADD logs:app '*' level info msg "worker started" >/dev/null
  $R XADD logs:app '*' level warn msg "slow query 812ms" >/dev/null
  $R XADD logs:app '*' level info msg "job completed" >/dev/null
fi

echo "Redis sample data seeded."
