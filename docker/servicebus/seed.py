"""Seeds the Service Bus emulator with sample messages so the plugin's Peek, dead-letter and
scheduled views have something to show. Runs once from the `servicebus-init` compose service.

Entities themselves come from Config.json at emulator boot; this script only sends. It skips
entirely if `orders` already holds messages, so re-runs don't keep growing the backlog. (It checks
with a peek, not the message count: the emulator's management API always reports 0 -- see
docker/README.md.)
"""
import json
import os
import time
from datetime import datetime, timedelta, timezone

from azure.servicebus import ServiceBusClient, ServiceBusMessage

CONN = os.environ.get(
    "SERVICEBUS_CONNECTION_STRING",
    "Endpoint=sb://servicebus-emulator;SharedAccessKeyName=RootManageSharedAccessKey;"
    "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
)
CUSTOMERS = ["ada", "grace", "linus", "margaret", "alan", "barbara"]


def msg(body: dict, subject: str, message_id: str, **props) -> ServiceBusMessage:
    return ServiceBusMessage(
        json.dumps(body),
        content_type="application/json",
        subject=subject,
        message_id=message_id,
        correlation_id=body.get("orderId"),
        application_properties={"source": "seed", **props},
    )


def connect() -> ServiceBusClient:
    # The emulator has no healthcheck and takes a while to come up after SQL Server does.
    for attempt in range(1, 31):
        try:
            client = ServiceBusClient.from_connection_string(CONN)
            with client.get_queue_receiver("orders", max_wait_time=2) as r:
                r.peek_messages(max_message_count=1)
            return client
        except Exception as ex:  # noqa: BLE001 - anything means "not ready yet"
            print(f"    emulator not ready ({type(ex).__name__}), retry {attempt}/30")
            time.sleep(5)
    raise SystemExit("Service Bus emulator never became ready")


def dead_letter(client: ServiceBusClient, receiver, count: int, reason: str, description: str) -> None:
    for m in receiver.receive_messages(max_message_count=count, max_wait_time=5):
        receiver.dead_letter_message(m, reason=reason, error_description=description)


def main() -> None:
    print("==> connecting to the Service Bus emulator")
    client = connect()
    with client:
        with client.get_queue_receiver("orders", max_wait_time=2) as r:
            if r.peek_messages(max_message_count=1):
                print("==> orders already has messages, skipping seed")
                return

        print("==> orders: 4 poison messages -> dead-letter queue")
        with client.get_queue_sender("orders") as s:
            s.send_messages([
                msg({"orderId": "0999", "customer": None}, "order", "order-0999"),
                msg({"orderId": "0998", "total": "12,50"}, "order", "order-0998"),
                msg({"orderId": "0997", "currency": "XXX"}, "order", "order-0997"),
                msg({"orderId": "0996"}, "order", "order-0996"),
            ])
        with client.get_queue_receiver("orders") as r:
            dead_letter(client, r, 2, "SchemaValidationFailed", "Required field 'customer' is missing or null")
            dead_letter(client, r, 2, "MaxDeliveryCountExceeded", "Handler threw InvalidOperationException 3 times")

        print("==> orders: 30 active messages")
        with client.get_queue_sender("orders") as s:
            s.send_messages([
                msg(
                    {"orderId": str(i), "customer": CUSTOMERS[i % len(CUSTOMERS)], "total": round((i * 37) % 400 + 5.99, 2), "currency": "EUR"},
                    "order", f"order-{i}", region=["uk", "eu", "us"][i % 3], priority="high" if i % 7 == 0 else "normal",
                )
                for i in range(1001, 1031)
            ])

        print("==> payments: 12 active + 3 scheduled + 1 dead-lettered")
        now = datetime.now(timezone.utc)
        with client.get_queue_sender("payments") as s:
            s.send_messages(msg({"paymentId": "4999", "orderId": "0999", "status": "pending"}, "payment", "pay-4999"))
        with client.get_queue_receiver("payments") as r:
            dead_letter(client, r, 1, "GatewayTimeout", "Payment gateway did not respond within 30s")
        with client.get_queue_sender("payments") as s:
            s.send_messages([
                msg({"paymentId": str(i), "orderId": str(i - 4000), "status": ["captured", "pending", "failed"][i % 3]}, "payment", f"pay-{i}")
                for i in range(5001, 5013)
            ])
            for n, minutes in enumerate([15, 60, 240], start=1):
                s.schedule_messages(
                    msg({"paymentId": f"retry-{n}", "orderId": str(1000 + n), "status": "retry"}, "payment", f"pay-retry-{n}"),
                    now + timedelta(minutes=minutes),
                )

        print("==> events topic: 20 messages (order-labelled ones also land in order-events)")
        with client.get_topic_sender("events") as s:
            s.send_messages([
                msg({"eventId": f"evt-{i}", "orderId": str(1000 + i), "type": kind}, kind, f"evt-{i}")
                for i, kind in enumerate(["order", "payment", "shipment", "order", "customer"] * 4, start=1)
            ])
        with client.get_subscription_receiver("events", "all-events") as r:
            dead_letter(client, r, 2, "UnknownEventType", "No handler registered for this event type")

    print("==> done")


if __name__ == "__main__":
    main()
