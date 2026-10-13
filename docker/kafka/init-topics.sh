#!/usr/bin/env bash
# Seeds the local Kafka broker with sample topics, messages and consumer groups. Runs once from
# the `kafka-init` compose service; safe to re-run: messages are only produced into a topic this
# run created, so re-runs don't keep growing the backlog, and consumer-group offsets are reset
# to the same positions every time.
set -euo pipefail

BOOTSTRAP="${BOOTSTRAP:-kafka:29092}"
BIN=/opt/kafka/bin

# Topics this run created (vs. found already present) -- only these get seeded messages.
declare -A CREATED=()

create_topic() {
  local name="$1" partitions="$2"
  if "$BIN/kafka-topics.sh" --bootstrap-server "$BOOTSTRAP" --list | grep -qx "$name"; then
    echo "    $name already exists, not re-seeding it"
    return
  fi
  "$BIN/kafka-topics.sh" --bootstrap-server "$BOOTSTRAP" --create \
    --topic "$name" --partitions "$partitions" --replication-factor 1
  CREATED[$name]=1
}

# produce TOPIC < lines   (line format: header1:value1,header2:value2#key|value)
produce() {
  local topic="$1"
  if [[ -z "${CREATED[$topic]:-}" ]]; then
    cat >/dev/null
    return
  fi
  "$BIN/kafka-console-producer.sh" --bootstrap-server "$BOOTSTRAP" --topic "$topic" \
    --property parse.key=true --property key.separator='|' --property parse.headers=true --property headers.delimiter='#' --property headers.separator=',' --property headers.key.separator=':'
}

# set_group_offsets GROUP TOPIC RESET_ARGS...   (the group must have no active members)
set_group_offsets() {
  local group="$1" topic="$2"
  shift 2
  "$BIN/kafka-consumer-groups.sh" --bootstrap-server "$BOOTSTRAP" --group "$group" --topic "$topic" \
    --reset-offsets "$@" --execute >/dev/null
}

echo "==> creating sample topics on $BOOTSTRAP"
create_topic orders          3
create_topic payments        3
create_topic notifications   1
create_topic clickstream     6
# "-dlq" suffix, not ".dlq": that is the convention the Kafka plugin's Dead-letter page keys on
# (ConfluentKafkaOperations.IsDlqTopic).
create_topic orders-dlq      1
create_topic payments-dlq    1

echo "==> producing sample messages"
customers=(ada grace linus margaret alan barbara ken dennis)
currencies=(EUR EUR EUR USD GBP)
statuses=(captured captured captured pending failed)

produce orders < <(
  for i in $(seq 1001 1300); do
    c=${customers[$((i % ${#customers[@]}))]}
    ccy=${currencies[$((i % ${#currencies[@]}))]}
    printf 'source:seed,content-type:application/json,event-type:OrderPlaced#order-%s|{"orderId":"%s","customer":"%s","total":%d.%02d,"currency":"%s","items":%d}\n' \
      "$i" "$i" "$c" $(( (i * 37) % 400 + 5 )) $(( i % 100 )) "$ccy" $(( i % 5 + 1 ))
  done
)

produce payments < <(
  for i in $(seq 5001 5180); do
    s=${statuses[$((i % ${#statuses[@]}))]}
    reason=""
    [[ "$s" == failed ]] && reason=',"reason":"insufficient_funds"'
    printf 'source:seed,content-type:application/json,event-type:PaymentUpdated#pay-%s|{"paymentId":"%s","orderId":"%s","status":"%s"%s}\n' \
      "$i" "$i" $(( i - 4000 )) "$s" "$reason"
  done
)

produce notifications <<'MSG'
source:seed,content-type:text/plain#notif-1|Welcome to NerveCenter local dev
source:seed,content-type:text/plain#notif-2|Kafka broker is up
source:seed,content-type:application/json#notif-3|{"to":"ada@example.com","template":"order-confirmed","orderId":"1001"}
source:seed,content-type:application/json#notif-4|{"to":"grace@example.com","template":"payment-failed","orderId":"1004"}
MSG

# Enough volume that the analytics-etl group below crosses the plugin's 10,000-message lag
# threshold (KafkaPlugin.LagProblemThreshold) and shows up in the Dashboard's Needs-attention.
pages=(/ /products /products/42 /cart /checkout /account /search)
produce clickstream < <(
  for i in $(seq 1 12500); do
    printf 'source:seed,content-type:application/json#session-%d|{"sessionId":"s-%d","page":"%s","ts":%d}\n' \
      $(( i % 400 )) $(( i % 400 )) "${pages[$((i % ${#pages[@]}))]}" $(( 1790000000 + i ))
  done
)

produce orders-dlq <<'MSG'
source:seed,content-type:application/json,dlq-reason:schema_mismatch,dlq-original-offset:17#order-0999|{"orderId":"0999","customer":null}
source:seed,content-type:application/json,dlq-reason:deserialization_error,dlq-original-offset:42#order-0998|{"orderId":"0998","total":"12,50"}
source:seed,content-type:application/json,dlq-reason:max_retries_exceeded,dlq-original-offset:88#order-0997|{"orderId":"0997","customer":"ken","total":18.00,"currency":"XXX"}
MSG

produce payments-dlq <<'MSG'
source:seed,content-type:application/json,dlq-reason:gateway_timeout#pay-4999|{"paymentId":"4999","orderId":"0999","status":"pending"}
MSG

echo "==> setting consumer-group offsets"
# billing-service: fully caught up (lag 0).
set_group_offsets billing-service payments --to-latest
# notification-worker: fully caught up (lag 0).
set_group_offsets notification-worker notifications --to-latest
# fulfillment-service: a little behind on orders (25 per partition).
set_group_offsets fulfillment-service orders --to-latest
set_group_offsets fulfillment-service orders --shift-by -25
# analytics-etl: never consumed clickstream -- high lag, flagged as a problem.
set_group_offsets analytics-etl clickstream --to-earliest
# dlq-reprocessor: hasn't looked at the orders DLQ yet.
set_group_offsets dlq-reprocessor orders-dlq --to-earliest

echo "==> done"
"$BIN/kafka-topics.sh" --bootstrap-server "$BOOTSTRAP" --list
"$BIN/kafka-consumer-groups.sh" --bootstrap-server "$BOOTSTRAP" --list
