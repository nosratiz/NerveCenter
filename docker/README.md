# Local development stack

Everything SbConsole talks to, in containers. The app itself normally runs on the host
(`dotnet run --project src/SbConsole.Web --launch-profile http`, or the `sbconsole-web`
entry in `.claude/launch.json`); this stack provides the brokers.

## Quick start

```bash
docker compose up -d
```

That starts:

| Service      | Host address             | What it is                                              |
|--------------|--------------------------|---------------------------------------------------------|
| `kafka`      | `localhost:9092`         | Apache Kafka 4.1, single-node KRaft, plaintext, no auth |
| `kafka-init` | (one-shot)               | Creates sample topics and drops a few messages in them  |
| `kafka-ui`   | http://localhost:8080    | Kafbat Kafka UI, for inspecting the broker directly     |

Seeded topics: `orders` (3 partitions, 5 messages), `payments` (3, 3), `notifications` (1, 2),
`orders.dlq` (1, 1). Auto-create is **off**, so the plugin's "Create topic" is meaningful and a
mistyped topic name fails loudly.

### Connect the app to it

In SbConsole, **Connections → Add**, kind *Apache Kafka*, secret:

```
bootstrap.servers=localhost:9092
```

That is the whole secret for the local broker: no `security.protocol`, no SASL keys. The
`Test` button should go green immediately, and the Topics page lists the four seeded topics.

## Azure Service Bus emulator (opt-in)

```bash
docker compose --profile servicebus up -d
```

Adds SQL Server 2022 (the emulator's store) and the official emulator. Both images are
Microsoft-licensed and the compose file sets `ACCEPT_EULA=Y` for them; read those licences
before relying on this in anything but local development. SQL Server ships amd64-only, so on
Apple Silicon it runs under Rosetta emulation: allow 30-60 s for the first healthy start.

| Service               | Host address       |
|-----------------------|--------------------|
| `sqlserver`           | `localhost:1433` (not published; internal only) |
| `servicebus-emulator` | `localhost:5672` (AMQP), `localhost:5300` (HTTP) |

Entities are declared up front in [`servicebus/Config.json`](servicebus/Config.json): queues
`orders` and `payments`; topic `events` with subscriptions `all-events` and `order-events`
(the latter has a correlation rule on `Label = order`). Edit that file and
`docker compose --profile servicebus restart servicebus-emulator` to change them.

Connection string for the app (kind *Azure Service Bus*):

```
Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;
```

`SAS_KEY_VALUE` is literal: the emulator accepts exactly that key. Do **not** add a port to
that endpoint, and see the note below for why the management API is published on port 80.

Verified against this stack: connection test, list/create/delete queue, list topics, list
subscriptions, list rules, send, peek, and the dead-letter overview all succeed.

**One emulator limitation, and it is not a bug in this app.** Queue and subscription message
counts always render as 0 against the emulator even when messages are there, while Peek returns
them correctly. The cause is in the emulator's management API, not our code: its queue
description responses carry `<MessageCount>0</MessageCount>` and no `<CountDetails>` element at
all, which is the element the Azure SDK populates `ActiveMessageCount`, `DeadLetterMessageCount`
and `ScheduledMessageCount` from. Adding `?enrich=true` changes nothing. The plugin already
calls the correct API (`GetQueuesRuntimePropertiesAsync`, not `GetQueuesAsync`), so counts are
real against an actual Service Bus namespace.

Check it yourself:

```bash
curl -s 'http://localhost/$Resources/Queues' | grep -c CountDetails
```

Practical consequence: count-driven UI (the dead-letter overview, the wallboard backlog, the
queue sparklines) cannot be exercised against the emulator. Use Kafka locally for that work --
its partition watermarks are real, so the Kafka topic message counts are too.

### Why the management API is on port 80

The Azure SDK derives two endpoints from one connection string, and they disagree about ports:

| Client                             | Protocol | Port it uses                       |
|------------------------------------|----------|------------------------------------|
| `ServiceBusClient`                 | AMQP     | the endpoint's port, else **5672** |
| `ServiceBusAdministrationClient`   | HTTP     | the endpoint's port, else **80**   |

So an endpoint naming a port breaks one client or the other: `sb://localhost:5300` gives
working management pages and AMQP timeouts, while `sb://localhost` gives working send/peek and
management calls refused on port 80. Publishing the emulator's 5300 as host port 80 is what
lets a single unported endpoint drive both. If port 80 is taken on your machine, move it with
`SERVICEBUS_HTTP_PORT` and accept that the plugin's management pages will fail.

## RabbitMQ (opt-in)

```bash
docker compose --profile rabbitmq up -d
```

| Service         | Host address                                                  |
|-----------------|---------------------------------------------------------------|
| `rabbitmq`      | `localhost:5673` (AMQP), http://localhost:15672 (management UI/API) |
| `rabbitmq-init` | (one-shot)                                                    |

RabbitMQ 3.13 with the `management`, `shovel` and `shovel_management` plugins. AMQP is published
on **5673**, not 5672, because the Service Bus emulator already claims 5672 on the host (move it
with `RABBITMQ_AMQP_PORT`).

Topology is declared in [`rabbitmq/definitions.json`](rabbitmq/definitions.json) and imported on
every boot. It is the one the RabbitMQ plugin's mockup is drawn from, in vhost `/orders`:
exchanges `order-events` (topic), `notify.fanout`, `billing.direct`, `billing.retry.dlx` (the
dead-letter exchange), `shipment.updates` (topic), `invoice.headers` (headers), `audit.fanout`,
and `legacy.import` (deliberately unbound); queues including `order-events.q` and
`billing.payments.q` (both dead-lettering through `billing.retry.dlx` into `payments-dlq`),
`billing.retry` (TTL 30 s), and quorum `audit.sink`; three policies (`orders-limits`, `dlq-ttl`,
`retry-shortttl`); and two dynamic shovels — `legacy-migrate` (healthy) and `billing-retry-loop`
(pointed at an unreachable destination, so it never leaves `starting`). `rabbitmq-init`
publishes sample messages, including five payments that expire straight into `payments-dlq` with
an `x-death` history.

Users: `sbconsole` / `sbconsole` (tags `management`, `monitoring`, `policymaker`; full
permissions on `/orders` only) and `admin` / `admin` (administrator). With a definitions file
configured RabbitMQ does not create the default `guest` user.

### Connect the app to it (RabbitMQ)

In SbConsole, **Connections → Add**, kind *RabbitMQ*: Host `localhost`, AMQP port `5673`,
Management URL `http://localhost:15672`, Virtual host `/orders`, Username/Password `sbconsole`,
TLS off. The equivalent raw secret (values percent-encoded) is:

```
host=localhost;amqpPort=5673;managementUrl=http%3A%2F%2Flocalhost%3A15672;vhost=%2Forders;username=sbconsole;password=sbconsole;tls=false
```

To see Test connection's split result, stop just the management listener's reachability — e.g.
point Management URL at `http://localhost:15999`: AMQP passes, the management check fails, and
the connection still saves.

## LocalStack — AWS SQS/SNS emulator (opt-in)

```bash
docker compose --profile aws up -d
```

| Service      | Host address          |
|--------------|------------------------|
| `localstack` | `localhost:4566`       |
| `aws-init`   | (one-shot)             |

Enabled services: `SERVICES=sqs,sns,sts,cloudwatch,logs`. STS backs **Test connection** (it calls
`GetCallerIdentity` first); CloudWatch and CloudWatch Logs back the SNS delivery-failure count, the
oldest-dead-letter tile and the delivery-logs tab. LocalStack publishes no SQS metrics and writes no
SNS delivery-status logs, so the tile and the logs tab stay empty locally. After changing
`SERVICES`, recreate just the emulator: `docker compose --profile aws up -d --force-recreate --no-deps localstack`.

Seeded queues: `order-events`, `order-events-dlq` (redrive policy already attached, max receives
5), `payments`. No topics are seeded yet — `docker/aws/init-queues.sh` predates the SNS Topics
feature and only creates queues; create a topic from the Topics page itself to try it against
LocalStack.

### Connect the app to it (LocalStack)

In SbConsole, **Connections → Add**, kind *AWS SQS/SNS*, secret from the host:

```
mode=access-keys;region=us-east-1;accessKeyId=test;secretAccessKey=test;endpoint=http://localhost:4566;pathStyle=true
```

`test`/`test` is LocalStack's own convention — it accepts any non-empty access key/secret pair
under the community edition. `us-east-1` matches the queues seeded above (LocalStack's default
region unless overridden).

From the containerised app (if running SbConsole in a container), use:
```
mode=access-keys;region=us-east-1;accessKeyId=test;secretAccessKey=test;endpoint=http://localstack:4566;pathStyle=true
```

### Least-privilege IAM policy (AWS)

For production use, the IAM principal SbConsole uses must have the following permissions. LocalStack
accepts any credentials during local development, so this policy is for reference and for real AWS
deployments.

**SQS permissions** (Queues, Messages, Redrive):
- `sqs:ListQueues` — list queues
- `sqs:GetQueueAttributes` — fetch queue metadata (counts, configuration, redrive policy)
- `sqs:CreateQueue` — create queue
- `sqs:DeleteQueue` — delete queue
- `sqs:PurgeQueue` — purge queue
- `sqs:ReceiveMessage` — receive (get) messages
- `sqs:DeleteMessage` — delete received message
- `sqs:ChangeMessageVisibility` — release message (reset visibility timeout)
- `sqs:SendMessage` — send message
- `sqs:StartMessageMoveTask` — initiate DLQ redrive task
- `sqs:ListMessageMoveTasks` — show redrive-task progress on a DLQ's detail page
- `sqs:CancelMessageMoveTask` — cancel a running redrive task
- `sqs:ListQueueTags` — show a queue's tags on its detail page
- `sqs:ListDeadLetterSourceQueues` — find a DLQ's source queues (queue detail page, and Receive's "Move to source")

**SNS permissions** (Topics, Subscriptions, Publish):
- `sns:ListTopics` — list topics
- `sns:GetTopicAttributes` — fetch topic metadata (subscription counts, encryption, configuration)
- `sns:CreateTopic` — create topic
- `sns:DeleteTopic` — delete topic
- `sns:ListSubscriptionsByTopic` — list subscriptions on a topic
- `sns:ListSubscriptions` — list every subscription in the account, filtered client-side to find the SNS topics a queue is subscribed to (queue detail page)
- `sns:GetSubscriptionAttributes` — fetch subscription metadata (status, filter policy)
- `sns:SetSubscriptionAttributes` — set, change, or clear a subscription's filter policy and scope
- `sns:Subscribe` — subscribe to topic
- `sns:Unsubscribe` — unsubscribe from topic
- `sns:Publish` — publish message to topic

The read-only additions above (`sqs:ListMessageMoveTasks`, `sqs:ListQueueTags`,
`sqs:ListDeadLetterSourceQueues`, `sns:ListSubscriptions`) degrade to an inline "unavailable" note
on the page that uses them when denied, rather than failing the page.

**CloudWatch permissions** (Delivery metrics):
- `cloudwatch:GetMetricStatistics` — fetch topic delivery-failure count over last 24 hours
  and each dead-letter queue's `ApproximateAgeOfOldestMessage` (wallboard "Oldest message" tile)

**CloudWatch Logs permissions** (Topic detail's Delivery logs tab):
- `logs:FilterLogEvents` — read SNS delivery-status logs from the topic's `sns/{region}/{account-id}/{topic-name}`
  and `sns/{region}/{account-id}/{topic-name}/Failure` log groups. Can be scoped to
  `arn:aws:logs:*:*:log-group:sns/*`. When denied, only the Delivery logs tab shows an inline warning.
  The logs exist only for topics with delivery status logging enabled (the per-protocol
  `<Protocol>SuccessFeedbackRoleArn` / `<Protocol>FailureFeedbackRoleArn` topic attributes).

Example IAM policy (JSON):
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "sqs:ListQueues",
        "sqs:GetQueueAttributes",
        "sqs:CreateQueue",
        "sqs:DeleteQueue",
        "sqs:PurgeQueue",
        "sqs:ReceiveMessage",
        "sqs:DeleteMessage",
        "sqs:ChangeMessageVisibility",
        "sqs:SendMessage",
        "sqs:StartMessageMoveTask",
        "sqs:ListMessageMoveTasks",
        "sqs:CancelMessageMoveTask",
        "sqs:ListQueueTags",
        "sqs:ListDeadLetterSourceQueues",
        "sns:ListTopics",
        "sns:GetTopicAttributes",
        "sns:CreateTopic",
        "sns:DeleteTopic",
        "sns:ListSubscriptionsByTopic",
        "sns:ListSubscriptions",
        "sns:GetSubscriptionAttributes",
        "sns:SetSubscriptionAttributes",
        "sns:Subscribe",
        "sns:Unsubscribe",
        "sns:Publish",
        "cloudwatch:GetMetricStatistics"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "logs:FilterLogEvents"
      ],
      "Resource": "arn:aws:logs:*:*:log-group:sns/*"
    }
  ]
}
```

Scope this to specific queue/topic ARNs as appropriate for your deployment. SbConsole does not
support resource-level ACLs beyond AWS's own permission system — all queues and topics the
connection can reach are accessible.

## SbConsole in a container (opt-in)

```bash
cp .env.example .env         # then fill in SBC_DATA_KEY / SBC_ADMIN_PASSWORD / SBC_API_KEY
docker compose --profile app up -d --build
```

Builds [`../Dockerfile`](../Dockerfile) (multi-stage, .NET 10 SDK → ASP.NET runtime, runs as
the non-root `app` user) and serves it on http://localhost:5249. SQLite database and
data-protection keys live in the `sbconsole-data` volume at `/data`.

Connections added from inside the container need different addresses than the host uses:

| Broker           | From the host                | From the containerised app        |
|------------------|------------------------------|-----------------------------------|
| Kafka            | `bootstrap.servers=localhost:9092` | `bootstrap.servers=kafka:29092` |
| Service Bus      | `Endpoint=sb://localhost;...`      | `Endpoint=sb://host.docker.internal;...` |

Kafka is reached by its service name because the broker advertises an internal listener on
`kafka:29092`. The emulator is reached back through the host instead, because its own container
answers management calls on 5300 while the SDK's admin client insists on port 80 (above) --
the host publishes the right ports, the container does not.

## Ports and overrides

Every published port can be moved through `.env` (see [`.env.example`](../.env.example)):
`KAFKA_HOST_PORT`, `KAFKA_UI_PORT`, `SERVICEBUS_AMQP_PORT`, `SERVICEBUS_HTTP_PORT`,
`SBC_HOST_PORT`, `LOCALSTACK_PORT`, `RABBITMQ_AMQP_PORT`, `RABBITMQ_MANAGEMENT_PORT`. Note that changing `KAFKA_HOST_PORT` also changes what the
broker advertises to host clients, so the secret you paste into the app must use the same port.

## Reset

```bash
docker compose --profile servicebus --profile aws --profile rabbitmq --profile app down -v
```

`-v` drops the named volumes (Kafka log segments, SQL Server data, the containerised app's
SQLite db). Re-running `docker compose up -d` re-seeds the sample topics.
