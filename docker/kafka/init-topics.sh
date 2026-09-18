#!/usr/bin/env bash
# Seeds the local Kafka broker with sample topics and messages. Runs once from the
# `kafka-init` compose service; safe to re-run.
set -euo pipefail

BOOTSTRAP="${BOOTSTRAP:-kafka:29092}"
BIN=/opt/kafka/bin

create_topic() {
  local name="$1" partitions="$2"
  "$BIN/kafka-topics.sh" --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
    --topic "$name" --partitions "$partitions" --replication-factor 1
}

produce() {
  local topic="$1"
  "$BIN/kafka-console-producer.sh" --bootstrap-server "$BOOTSTRAP" --topic "$topic" \
    --property parse.key=true --property key.separator='|' --property parse.headers=true --property headers.delimiter='#' --property headers.separator=',' --property headers.key.separator=':'
}

echo "==> creating sample topics on $BOOTSTRAP"
create_topic orders          3
create_topic payments        3
create_topic notifications   1
create_topic orders.dlq      1

echo "==> producing sample messages"
# format: header1:value1,header2:value2#key|value
produce orders <<'MSG'
source:seed,content-type:application/json#order-1001|{"orderId":"1001","customer":"ada","total":42.50,"currency":"EUR"}
source:seed,content-type:application/json#order-1002|{"orderId":"1002","customer":"grace","total":19.99,"currency":"EUR"}
source:seed,content-type:application/json#order-1003|{"orderId":"1003","customer":"linus","total":250.00,"currency":"USD"}
source:seed,content-type:application/json#order-1004|{"orderId":"1004","customer":"ada","total":7.25,"currency":"EUR"}
source:seed,content-type:application/json#order-1005|{"orderId":"1005","customer":"margaret","total":99.00,"currency":"GBP"}
MSG

produce payments <<'MSG'
source:seed,content-type:application/json#pay-5001|{"paymentId":"5001","orderId":"1001","status":"captured"}
source:seed,content-type:application/json#pay-5002|{"paymentId":"5002","orderId":"1002","status":"pending"}
source:seed,content-type:application/json#pay-5003|{"paymentId":"5003","orderId":"1003","status":"failed","reason":"insufficient_funds"}
MSG

produce notifications <<'MSG'
source:seed,content-type:text/plain#notif-1|Welcome to NerveCenter local dev
source:seed,content-type:text/plain#notif-2|Kafka broker is up
MSG

produce orders.dlq <<'MSG'
source:seed,content-type:application/json,dlq-reason:schema_mismatch#order-0999|{"orderId":"0999","customer":null}
MSG

echo "==> done"
"$BIN/kafka-topics.sh" --bootstrap-server "$BOOTSTRAP" --list
