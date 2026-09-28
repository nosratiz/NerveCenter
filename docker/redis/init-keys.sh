#!/bin/sh
# Seeds Redis with sample data shaped like the Redis plugin's mockup
# (docs/superpowers/specs/redis-mockup/), so every page -- Overview, Keys, Streams, Pub/Sub --
# has something realistic to show without hand-seeding.
#
# Runs once from the `redis-init` compose service. Idempotent: HSET/SADD/ZADD/SET overwrite in
# place; the append-only structures (the retry list, the streams) are gated on EXISTS/LLEN so a
# second `docker compose up` doesn't grow them.
set -eu

R="redis-cli -h redis -a $REDIS_PASSWORD --no-auth-warning"
NOW_S=$(date +%s)

# Multi-command blocks are generated with awk and piped into one redis-cli session. Values are
# single-quoted, which redis-cli takes literally -- the JSON below never contains a single quote.
pipe() { $R >/dev/null; }

echo "==> sessions (session:u:*): the mockup's featured keys plus ~130 more, ~38% without a TTL"
RECENT=$(awk 'BEGIN { printf "["; for (i = 0; i < 1206; i++) printf "%s\"sku_%d\"", (i ? "," : ""), 4410 + i * 7; printf "]" }')
$R HSET session:u:77120 userId 77120 tenant acme-uk createdAt 2026-08-02T07:14:11Z \
  lastSeen "$(date -u +%FT%TZ)" prefs '{"theme":"dark","locale":"en-GB","tz":"Europe/London"}' \
  recentViews "$RECENT" csrf 9f2c41d07a8e4b6c93d5e1f0a7b2c8d4e6f1a3b5c7d9e0f2a4b6c8d0e2f4a6b8 mfa true >/dev/null
$R HSET session:u:77121 userId 77121 tenant acme-uk lastSeen "$(date -u +%FT%TZ)" mfa false >/dev/null
$R EXPIRE session:u:77121 1740 >/dev/null
$R HSET session:u:77184 userId 77184 tenant northwind lastSeen "$(date -u +%FT%TZ)" mfa true >/dev/null
$R HSET session:u:77190 userId 77190 tenant globex lastSeen "$(date -u +%FT%TZ)" mfa false >/dev/null
$R EXPIRE session:u:77190 7440 >/dev/null
$R SET session:u:77190:csrf 4c1e9a7f2b8d6e3a0f5c9b1d7e4a2c8f EX 7440 >/dev/null
$R HSET session:u:77203 userId 77203 tenant initech lastSeen "$(date -u +%FT%TZ)" mfa true >/dev/null
$R EXPIRE session:u:77203 660 >/dev/null
$R SADD session:u:77203:roles admin billing support >/dev/null
$R EXPIRE session:u:77203:roles 660 >/dev/null
$R HSET session:u:77219 userId 77219 tenant umbrella lastSeen "$(date -u +%FT%TZ)" mfa true >/dev/null
awk -v now="$(date -u +%FT%TZ)" 'BEGIN {
  split("acme-uk northwind globex initech umbrella hooli", t, " ")
  for (n = 77300; n < 77432; n++) {
    printf "HSET session:u:%d userId %d tenant %s lastSeen %s mfa %s\n", n, n, t[n % 6 + 1], now, (n % 2 ? "true" : "false")
    if (n % 8 >= 3) printf "EXPIRE session:u:%d %d\n", n, 300 + (n * 97) % 14000
  }
}' | pipe

echo "==> carts (cart:u:*) and rate limits (rate:*), all with a TTL"
awk 'BEGIN {
  for (n = 77100; n < 77140; n++) {
    printf "HSET cart:u:%d sku_%d %d sku_%d %d updatedBy web\n", n, 4000 + n % 97, 1 + n % 3, 9000 + n % 53, 1 + n % 2
    printf "EXPIRE cart:u:%d 86400\n", n
  }
  for (n = 1; n <= 60; n++) printf "SET rate:api:10.4.8.%d %d EX %d\n", n, n * 13 % 400, 60 + n * 55
}' | pipe

echo "==> config:pricing -- a JSON string with no TTL"
$R SET config:pricing '{"currency":"GBP","vatRate":0.2,"tiers":[{"min":0,"pct":0},{"min":500,"pct":5},{"min":2000,"pct":8}],"updatedBy":"pricing-svc"}' >/dev/null

echo "==> retry job list (jobs:email:retry), read head-to-tail"
if [ "$($R LLEN jobs:email:retry)" = "0" ]; then
  awk 'BEGIN {
    split("a k r j m p s t", who, " ")
    for (i = 0; i < 400; i++) printf "RPUSH jobs:email:retry '"'"'{\"to\":\"%s@acme.example\",\"try\":%d}'"'"'\n", who[i % 8 + 1], 1 + (i * 7) % 5
  }' | pipe
fi

echo "==> weekly leaderboard (lb:weekly), ranked sorted set"
$R ZADD lb:weekly 9812 team-orion 8044 team-lyra 6970 team-vela 5402 team-ara 3733 team-grus 2210 team-pavo 1180 team-lupus >/dev/null

echo "==> beta feature flag membership (flags:beta), 64-member set"
$R SADD flags:beta acme-uk northwind globex initech umbrella hooli stark-eu wayne-uk soylent >/dev/null
awk 'BEGIN { for (i = 1; i <= 55; i++) printf "SADD flags:beta tenant-%02d\n", i }' | pipe

echo "==> orders:stream -- 240 entries over the last 4h, three consumer groups, one stalled consumer"
# Explicit ids (one per minute, ending a minute ago) so the acks below can name exact entries.
if [ "$($R EXISTS orders:stream)" = "0" ]; then
  TS0=$(( (NOW_S - 14460) * 1000 ))
  awk -v ts="$TS0" 'BEGIN {
    split("order.created order.paid order.shipped", ev, " ")
    for (i = 0; i < 240; i++)
      printf "XADD orders:stream %.0f-0 orderRef UK-2026-%d event %s amount %.2f currency GBP\n", ts + i * 60000, 448120 + i, ev[i % 3 + 1], 12 + (i * 37) % 480 + 0.4
    print "XGROUP CREATE orders:stream fulfilment 0"
    print "XGROUP CREATE orders:stream billing 0"
    print "XGROUP CREATE orders:stream analytics 0"
  }' | pipe

  # ack <group> <from> <to>: acknowledge entries from..to (inclusive) by their known ids.
  ack() { awk -v ts="$TS0" -v g="$1" -v a="$2" -v b="$3" 'BEGIN { printf "XACK orders:stream %s", g; for (i = a; i <= b; i++) printf " %.0f-0", ts + i * 60000; print "" }' | pipe; }

  # fulfilment: three consumers split everything, each still holding its last 6 -> pending 18, lag 0.
  for c in 1 2 3; do $R XREADGROUP GROUP fulfilment "fulfil-$c" COUNT 80 STREAMS orders:stream '>' >/dev/null; done
  ack fulfilment 0 73; ack fulfilment 80 153; ack fulfilment 160 233

  # billing: billing-2a41 took 40 entries and never acked (the stalled consumer); billing-7c9f is
  # healthy. The first two of 2a41's entries are redelivered 5 more times -> "delivered 6x".
  $R XREADGROUP GROUP billing billing-2a41 COUNT 40 STREAMS orders:stream '>' >/dev/null
  $R XREADGROUP GROUP billing billing-7c9f COUNT 60 STREAMS orders:stream '>' >/dev/null
  ack billing 40 95
  for _ in 1 2 3 4 5; do $R XREADGROUP GROUP billing billing-2a41 COUNT 2 STREAMS orders:stream 0 >/dev/null; done

  # analytics: two consumers, a little behind -> pending 8, lag 8.
  $R XREADGROUP GROUP analytics analytics-1 COUNT 116 STREAMS orders:stream '>' >/dev/null
  $R XREADGROUP GROUP analytics analytics-2 COUNT 116 STREAMS orders:stream '>' >/dev/null
  ack analytics 0 111; ack analytics 116 227
fi

echo "==> app log stream (logs:app), no consumer group -- exercises the 'no groups yet' state"
if [ "$($R EXISTS logs:app)" = "0" ]; then
  $R XADD logs:app '*' level info msg "worker started" >/dev/null
  $R XADD logs:app '*' level warn msg "slow query 812ms" >/dev/null
  $R XADD logs:app '*' level info msg "job completed" >/dev/null
fi

echo "==> slow log: the commands the mockup's Overview shows, including a KEYS from outside SbConsole"
# Threshold 0 for just these commands, so they land in SLOWLOG however fast they run here.
printf '%s\n' \
  "CONFIG SET slowlog-log-slower-than 0" \
  "SLOWLOG RESET" \
  "SMEMBERS flags:beta" \
  "XRANGE orders:stream - + COUNT 100" \
  "ZRANGE lb:weekly 0 -1" \
  "HGETALL cart:u:77120" \
  "KEYS session:*" \
  "CONFIG SET slowlog-log-slower-than 10000" | pipe

echo "Redis sample data seeded."
