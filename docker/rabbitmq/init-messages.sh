#!/usr/bin/env sh
# Seeds the RabbitMQ broker with sample messages so the RabbitMQ plugin's Queues, queue detail and
# Get messages pages have something to show. Runs once from the `rabbitmq-init` compose service.
# Topology (exchanges, queues, bindings, policies, shovels) comes from definitions.json at broker
# boot; this script only publishes. Re-runs add more messages, which is harmless.
set -eu

API="${RABBIT_API:-http://rabbitmq:15672/api}"
AUTH="${RABBIT_USER:-sbconsole}:${RABBIT_PASSWORD:-sbconsole}"
VHOST="%2Forders"

# publish EXCHANGE ROUTING_KEY MESSAGE_ID PAYLOAD [EXPIRATION_MS]
publish() {
  expiration=""
  if [ -n "${5:-}" ]; then expiration=",\"expiration\":\"$5\""; fi
  curl -fsS -u "$AUTH" -H 'content-type: application/json' \
    -X POST "$API/exchanges/$VHOST/$1/publish" \
    -d "{\"properties\":{\"delivery_mode\":2,\"content_type\":\"application/json\",\"message_id\":\"$3\",\"app_id\":\"seed\"$expiration},\"routing_key\":\"$2\",\"payload\":$(printf '%s' "$4" | sed 's/"/\\"/g; s/^/"/; s/$/"/'),\"payload_encoding\":\"string\"}" \
    >/dev/null
}

echo "==> order events (routed to order-events.q and audit.sink)"
for i in 1 2 3 4 5 6; do
  publish order-events order.uk.created "ord_4190$i" "{\"orderId\":\"ord_4190$i\",\"region\":\"uk\",\"total\":149.00}"
done
publish order-events order.eu.amended ord_41907 '{"orderId":"ord_41907","region":"eu","change":"address"}'

echo "==> payments that expire straight into payments-dlq (via billing.retry.dlx)"
for id in pay_8814c2 pay_8814bf pay_881288 pay_880ea4; do
  publish billing.direct payment.capture "$id" "{\"paymentId\":\"$id\",\"orderId\":\"ord_41908\",\"amount\":{\"value\":149.00,\"ccy\":\"GBP\"},\"method\":\"card\"}" 1
done
publish billing.direct refund.issue pay_880f19 '{"paymentId":"pay_880f19","refund":true}' 1

echo "==> notifications, shipments, invoices"
publish notify.fanout "" ntf_001 '{"to":"customer@example.com","template":"order-confirmed"}'
publish shipment.updates shipment.uk.dispatched shp_001 '{"shipmentId":"shp_001","status":"dispatched"}'
curl -fsS -u "$AUTH" -H 'content-type: application/json' -X POST "$API/exchanges/$VHOST/invoice.headers/publish" \
  -d '{"properties":{"delivery_mode":2,"headers":{"type":"issued"},"content_type":"application/json"},"routing_key":"","payload":"{\"invoiceId\":\"inv_001\"}","payload_encoding":"string"}' >/dev/null

echo "==> retry backlog (billing.retry has no consumers)"
for i in 1 2 3; do
  publish "amq.default" billing.retry "retry_00$i" "{\"attempt\":$i}"
done

echo "==> done"
