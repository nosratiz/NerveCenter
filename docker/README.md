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
| `kafka-init` | (one-shot)               | Seeds sample topics, messages and consumer groups       |
| `kafka-ui`   | http://localhost:8080    | Kafbat Kafka UI, for inspecting the broker directly     |

Seeded topics (partitions, messages): `orders` (3, 300), `payments` (3, 180), `notifications`
(1, 4), `clickstream` (6, 12,500), and the dead-letter topics `orders-dlq` (1, 3) and
`payments-dlq` (1, 1). The DLQ topics use the `-dlq` suffix because that is what the plugin's
Dead-letter page looks for. Messages are only produced into topics the seed creates, so re-runs
don't grow the backlog; `docker compose down -v` resets it.

Seeded consumer groups: `billing-service` and `notification-worker` (caught up, lag 0),
`fulfillment-service` (lag 75 on `orders`), `dlq-reprocessor` (lag 3 on `orders-dlq`) and
`analytics-etl` (lag 12,500 on `clickstream`, over the plugin's 10,000 threshold, so it appears in
the Dashboard's Needs-attention).

Auto-create is **off**, so the plugin's "Create topic" is meaningful and a mistyped topic name
fails loudly.

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

The one-shot `servicebus-init` service (Python, because the emulator only accepts sends over AMQP)
then fills them with sample messages:

| Entity                  | Active | Dead-lettered | Scheduled |
|-------------------------|--------|---------------|-----------|
| `orders`                | 30     | 4             | 0         |
| `payments`              | 12     | 1             | 3 (15 min, 1 h, 4 h out) |
| `events` / `all-events` | 18     | 2             | 0         |
| `events` / `order-events` | 8    | 0             | 0         |

It skips itself when `orders` already has messages. To re-seed, drain the queues or run
`docker compose --profile servicebus down -v`.

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

## Redis (opt-in)

```bash
docker compose --profile redis up -d
```

| Service             | Host address                     |
|---------------------|-----------------------------------|
| `redis`             | `localhost:6379`                 |
| `redis-init`        | (one-shot)                       |
| `redis-pubsub-demo` | (background publisher, no port)  |

Redis 7, password-protected (`--requirepass`), standalone, `maxmemory 16mb` / `allkeys-lru` (so
the Overview page's memory tile has a real ceiling). `redis-init` seeds data shaped like the
plugin's mockup ([`docs/superpowers/specs/redis-mockup/`](../docs/superpowers/specs/redis-mockup/)):

- **Keys** — ~140 `session:u:*` hashes (~38% with no TTL; `session:u:77120` carries the mockup's
  fields, including a masked `csrf` and a large `recentViews`), `session:u:77190:csrf` (string),
  `session:u:77203:roles` (set), 40 `cart:u:*` hashes and 60 `rate:*` counters (all with TTLs),
  `config:pricing` (JSON string), `jobs:email:retry` (400-entry list), `lb:weekly` (sorted set),
  `flags:beta` (64-member set).
- **Streams** — `orders:stream` (240 entries) with three consumer groups: `fulfilment` (caught
  up), `billing` (lag ~140; `billing-2a41` is stalled holding 40 pending entries, two of them
  delivered 6×) and `analytics` (slightly behind). `events:orders` has one small group;
  `logs:app` has none, for the "no groups yet" state.
- **Overview** — the slow log is pre-filled with the mockup's commands, including a `KEYS`
  issued from outside SbConsole.

`redis-pubsub-demo` runs continuously, every 5s: it publishes a heartbeat to `notifications:live`
(and every third beat to `orders:events`) — Pub/Sub has no history, so this is what gives the
Pub/Sub page's live tail something to show — and appends an `orders:stream` entry that every
healthy consumer takes turns reading and acking, keeping "last ID" and idle times live while
`billing-2a41` alone stays stalled. Stop it with `docker compose stop redis-pubsub-demo` if the noise is unwanted.

Re-running `docker compose up -d` re-seeds idempotently (see
[`redis/init-keys.sh`](redis/init-keys.sh)). To start from scratch, recreate the containers *and* their
anonymous data volume (`--force-recreate` alone keeps Redis's `/data`, so the seed would skip the
streams it finds still there):
`docker compose --profile redis up -d --force-recreate --renew-anon-volumes --no-deps redis redis-init redis-pubsub-demo`.

### Connect the app to it (Redis)

In SbConsole, **Connections → Add**, kind *Redis*: Topology Standalone, Host:port
`localhost:6379`, Password `sbconsole`, TLS off. The equivalent raw secret is:

```
standalone|0|localhost:6379,password=sbconsole
```

Note: `User`/`Password` in this secret are percent-encoded (`RedisConnectionSettings`, since
`ConfigurationOptions` has no escape syntax for a comma inside a value) — if you're hand-editing a
secret via the API rather than through the connection form, a literal `%` sequence in the password
(e.g. `%2C`) will be decoded, so escape any `%` in a hand-written password as `%25`.

From the containerised app (if running SbConsole in a container), use `redis:6379` instead of
`localhost:6379` — reached by its service name on the compose network, the same way the Kafka
plugin reaches `kafka:29092`.

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
`SBC_HOST_PORT`, `LOCALSTACK_PORT`, `RABBITMQ_AMQP_PORT`, `RABBITMQ_MANAGEMENT_PORT`,
`REDIS_PORT`, `REDIS_PASSWORD`, `POSTGRES_PORT`, `POSTGRES_PASSWORD`. Note that changing `KAFKA_HOST_PORT` also changes what the
broker advertises to host clients, so the secret you paste into the app must use the same port.

## Reset

```bash
docker compose --profile servicebus --profile aws --profile rabbitmq --profile redis --profile postgres --profile sqlite --profile app down -v
```

`-v` drops the named volumes (Kafka log segments, SQL Server data, PostgreSQL data, the containerised app's
SQLite db). Re-running `docker compose up -d` re-seeds the sample topics.

## SQLite

SQLite is a file, not a server, so there's no broker container — just a one-shot seed
(`docker/sqlite/seed.py`) that writes, into one directory:

| File | Journal | What it demos |
|---|---|---|
| `reports.db` | WAL | ~35 MB file shaped like the plugin mockups: tenants / users / sessions / events with JSON props and unix-ms timestamps from ~13 months ago up to the moment the seed ran, a WITHOUT ROWID `event_props` table, `reports` / `report_runs` (`ON DELETE CASCADE`), two views, a trigger, `ANALYZE` stats and some free pages (Overview, WAL checkpoint). Tenant `umbrella` is churned (last activity ~60 days before seeding), so the retention script below has rows to dry-run and cascades to show. |
| `shop.db` | DELETE | ~6 MB shop: 6 000 customers, 22 000 orders, ~57 000 order items, 12 000 reviews, 400 products. `price_cents` integers, JSON `attributes` / review `meta`, `CHECK`s on order status / quantity / rating, `order_items` → `orders` **ON DELETE CASCADE** and → `products` **ON DELETE RESTRICT** (the Drop dialog shows both), a partial index (`idx_orders_pending … WHERE status = 'pending'`), the view `v_revenue_by_month`, an `updated_at` trigger, a WITHOUT ROWID `product_tags`, `ANALYZE` stats. |
| `corrupt.db` | DELETE | Tiny and deliberately broken: the `idx_items_name` index b-tree is damaged (integrity check fails with "row N missing from index…"; **REINDEX** fixes it) and `item_notes` has 3 orphan rows (foreign-key check violations). Demos the Overview's failed-integrity strip, REINDEX, **Recover into new file** and the dashboard's problem counts. |
| `reference.db` | DELETE | Small lookup data — countries (ISO alpha-2/3, numeric), currencies, a `country_currencies` join with FKs — connected **Read-only** to show the read-only UI state. |
| `new.db` | — | A 0-byte file — SQLite opens it as an empty database (the Tables page's empty state, mockup 1g). |
| `backups/` | | World-writable directory for the connections' **Enable backups to** (Backups page). |

The seed is idempotent: each file is created only when missing. `--force` deletes and rebuilds
every database file (never `backups/`) — the seed verifies that `corrupt.db` really fails its
integrity check and that `REINDEX` on a copy repairs it.

**Demo connections are created automatically.** With `SBC_DEMO_CONNECTIONS` pointing at a JSON
file, the app creates each listed connection at startup **if no connection with that name exists**
— it never updates or deletes one, so your edits survive restarts and a connection you delete comes
back on the next start (remove it from the file to stop that). They go through the same path as the
Connections form: secret encrypted, `connection.create` audit row with actor `system`. Unset = off;
an unreadable file is logged and skipped. Demo/dev only — never put production secrets in a plain
JSON file. The compose app mounts `docker/sqlite/demo-connections.json` (read-only) at
`/demo/connections.json`:

| Name | File path | Mode | Enable backups to | Tags |
|---|---|---|---|---|
| `sqlite-reports` | `/sqlite/reports.db` | Read-write | `/sqlite/backups` | `prod` |
| `sqlite-shop` | `/sqlite/shop.db` | Read-write | `/sqlite/backups` | `staging` |
| `sqlite-corrupt` | `/sqlite/corrupt.db` | Read-write | `/sqlite/backups` | `demo` |
| `sqlite-reference` | `/sqlite/reference.db` | Read-only | — | `demo` |
| `sqlite-new` | `/sqlite/new.db` | Read-write | — | `demo` |

`reports` and `shop` share `backups/`; backup files are prefixed with the database's base name and
each connection's history only lists its own records, so they don't mix.

- **Containerised app:** `docker compose --profile sqlite --profile app up -d --build`. Everything
  lands in the `sqlite-demo` volume, mounted at `/sqlite`; the app gets `SBC_SQLITE_ROOTS=/sqlite`
  and `SBC_DEMO_CONNECTIONS=/demo/connections.json`.
  Reset the files with `docker compose --profile sqlite run --rm sqlite-init python -I /seed.py --force /sqlite`
  (then restart the app if it has a file open). Resetting files doesn't touch the saved
  connections; to recreate those too, drop the app's volume (`docker compose --profile app down -v`
  removes `sbconsole-data` along with the others) and start again.
- **`dotnet run`:** `python3 -I docker/sqlite/seed.py ./.sqlite-demo` (add `--force` to rebuild).
  In `launchSettings.json` set `SBC_SQLITE_ROOTS` to the absolute path of `.sqlite-demo`. For the
  demo connections, copy `docker/sqlite/demo-connections.json` somewhere outside the repo (e.g.
  `.sqlite-demo/demo-connections.json`), replace every `/sqlite` in it with that absolute path, and
  set `SBC_DEMO_CONNECTIONS` to the copy. Without it, add connections by hand with the paths above
  (`<that path>/…` instead of `/sqlite/…`).

`sqlite-reports` with the `prod` tag exercises the typed confirmations (write mode, Run in
transaction, Drop table, Prune, Restore). A retention dry run to paste into the Query page in
write mode (it cascades into `event_props`):

```sql
DELETE FROM events
WHERE ts < (strftime('%s','now','-90 days')*1000)
  AND tenant_id IN (SELECT id FROM tenants WHERE status='churned');
```

**New database.** Every SQLite page's connection menu (and the "No SQLite connections yet" state)
has **+ New database…**: pick a folder — the `SBC_SQLITE_ROOTS` roots, so `/sqlite` in compose —
an optional sub-folder that already exists, a file name (`.db` is appended), WAL or DELETE, and a
template (Empty, a small Starter schema of users/projects/tasks, or your own SQL script, which gets
the Query page's refusals: no ATTACH, VACUUM or transaction control). It also saves the connection
(default name `sqlite-<file>`, Read-write by default, tags, and `<folder>/backups` as the backup
directory when that folder exists — in compose `/sqlite/backups` does) and opens its Overview. It
never overwrites a file (also refused if a `-wal`/`-shm`/`-journal` leftover is there), never
creates a directory, and a failed template or connection save deletes the new file again. Audited
as `sqlite.database.create`.

**Backups.** `BackupDir` ("Enable backups to") must pass the same path policy as the file: it has
to be *inside* an `SBC_SQLITE_ROOTS` root (a root itself is refused) and may be the live file's own
directory; it must already exist and be writable by the app's user — the seed's `backups/` is.
Copies are `VACUUM INTO` files named `<base>-<yyyyMMdd-HHmmss>.db`; a restore keeps the replaced
file next to the live one as `<file>.pre-restore-<yyyyMMdd-HHmmss>` (in `/sqlite`, also writable).
The daily schedule runs inside the app (a background service), so nothing is backed up while it is
down; growth samples for the Overview's "Growth — 30 days" card accumulate the same way (at most
one an hour), so a fresh install shows "collecting" for its first hour. Download streams the file
through the browser session — fine at demo sizes; fetch multi-GB copies from the volume directly.

`SBC_SQLITE_ROOTS` is fail-closed: unset means no SQLite connection can open anything. Paths are
resolved (`..` collapsed, every symlink followed) before the check, and SbConsole's own database and
its `-wal`/`-shm`/`-journal` files are always refused. Known limitations: the bundled SQLite has no
`dbstat`, so the Overview ranks the largest tables by `sqlite_stat1` row estimates instead of bytes;
a *symlink race*: the path check is check-then-open, and while opening the resolved path defeats
symlinks that already existed at check time, someone with write access under a root can still race
a new one in; and a *hard link* to SbConsole's own database placed under a root is not detected, because the policy
compares resolved paths, not file identity (creating one needs write access to both locations).
Roots must never contain SbConsole's own data directory (the folder of `SBC_DB_PATH`, which also
holds the data-protection keys).

## PostgreSQL (opt-in)

```bash
docker compose --profile postgres up -d
```

| Service            | Host address     | What it is                                                              |
|--------------------|------------------|-------------------------------------------------------------------------|
| `postgres`         | `localhost:5433` | PostgreSQL 16, user `sbconsole` / password `sbconsole`, seeded on first start |
| `postgres-blocker` | (long-running)   | Keeps a lock-contention scenario alive for the Activity page            |

The host port is 5433 so it doesn't clash with a Postgres already on 5432 (`POSTGRES_PORT`; the
password is `POSTGRES_PASSWORD`). The server runs with `pg_stat_statements` preloaded and
`track_io_timing=on`. The seed runs once, when the `postgres-data` volume is first created
(`docker/postgres/init/*.sql`, about 35 s on a laptop; the healthcheck only turns healthy when it
is done):

| Database    | Contents |
|-------------|----------|
| `app`       | `customers` (20k), `products` (2k), `orders` (200k, jsonb `metadata`, a few `pending`), `order_items` (400k), `payments` (150k), `sessions` (50k), `events` (300k); schemas `audit` and `reporting` (view `v_order_totals`, materialized view `mv_daily`); function `order_total(bigint)`; extensions `pgstattuple` and `pg_stat_statements` |
| `analytics` | one small table, `daily_orders` |
| `billing`   | one small table, `invoices` |

What it is built to show:

- **Maintenance:** autovacuum is switched off on `sessions` and `order_items`, then 40 % of their
  rows are deleted, so 40 % of each table is dead tuples.
- **Browse / Overview:** `idx_orders_legacy_ref` is never read (the "unused index" problem);
  `idx_orders_status_pending` is a partial index; `idx_orders_metadata_gin` a GIN index.
- **Activity:** `postgres-blocker` holds a transaction open with a row lock on `orders.id = 1`
  (`checkout-api`, idle in transaction) and starts two waiters on the same row (`checkout-api`,
  `fulfilment-worker`), so the page shows a blocking chain. The cycle restarts every 15 minutes
  and the holder's transaction is rolled back, which resolves the chain for a moment. Terminating
  the holder in the UI works; the next cycle makes a new one.

Demo connections: `docker/sqlite/demo-connections.json` is the one file for every plugin, so it
also holds `pg-local` (read-write, tag `prod`, which exercises the typed confirmations) and
`pg-local-ro` (read-only, tag `demo`), both `Host=postgres`, port 5432, database `app`, which is
how the containerised app (`--profile app`, same compose network) reaches the server. They are
created at startup if missing, like the SQLite ones.

For `dotnet run` on the host, copy `docker/sqlite/demo-connections.json` somewhere outside the
repo, change `Host=postgres` to `Host=localhost` and `Port=5432` to `Port=5433` in the two
`postgres` entries (and the `/sqlite` paths as described above, or delete the sqlite entries),
and point `SBC_DEMO_CONNECTIONS` at the copy. Or create the connection by hand on the Connections
page with Host `localhost`, Port `5433`, Database `app`, user/password `sbconsole`.

Reset only this data: `docker compose --profile postgres down` then
`docker volume rm nervecenter_postgres-data` (the next `up` re-seeds). `docker compose
--profile postgres exec postgres psql -U sbconsole -d app` opens a shell on the server.

The `sbconsole` user is a superuser, so every page works in the demo. The plugin also works as a
plain role, with less visibility: without `pg_monitor`, other users' queries and `pg_stat_activity`
rows are hidden and Activity says so.
