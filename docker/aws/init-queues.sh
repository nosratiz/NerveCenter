#!/usr/bin/env sh
# Seeds LocalStack with sample SQS queues so the AWS plugin's Queues page has something to show.
# Runs once from the `aws-init` compose service; safe to re-run (--if-not-exists isn't a real SQS
# flag, so this script just ignores "already exists" instead).
set -eu

ENDPOINT="${ENDPOINT_URL:-http://localstack:4566}"

create_queue() {
  name="$1"
  aws --endpoint-url "$ENDPOINT" sqs create-queue --queue-name "$name" >/dev/null 2>&1 || true
}

echo "==> creating sample queues on $ENDPOINT"
create_queue order-events
create_queue order-events-dlq
create_queue payments

# Wait for queues to be available before attaching redrive policy
i=0
while [ $i -lt 10 ]; do
  if aws --endpoint-url "$ENDPOINT" sqs get-queue-url --queue-name order-events >/dev/null 2>&1; then
    break
  fi
  i=$((i + 1))
  sleep 1
done

echo "==> attaching order-events' redrive policy to order-events-dlq"
ORDER_QUEUE_URL=$(aws --endpoint-url "$ENDPOINT" sqs get-queue-url --queue-name order-events --query 'QueueUrl' --output text)
DLQ_URL=$(aws --endpoint-url "$ENDPOINT" sqs get-queue-url --queue-name order-events-dlq --query 'QueueUrl' --output text)
DLQ_ARN=$(aws --endpoint-url "$ENDPOINT" sqs get-queue-attributes \
  --queue-url "$DLQ_URL" --attribute-names QueueArn \
  --query 'Attributes.QueueArn' --output text)
aws --endpoint-url "$ENDPOINT" sqs set-queue-attributes \
  --cli-input-json "{\"QueueUrl\":\"$ORDER_QUEUE_URL\",\"Attributes\":{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"$DLQ_ARN\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\"}}"

echo "==> done"
aws --endpoint-url "$ENDPOINT" sqs list-queues
