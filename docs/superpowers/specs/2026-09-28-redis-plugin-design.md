# Redis plugin — Design

Status: draft, 2026-09-28.

## 1. Context

SbConsole's fifth plugin, built the same way RabbitMQ's plan proved the
architecture out (`docs/superpowers/specs/2026-09-25-rabbitmq-plugin-design.md`),
but for a fundamentally different kind of target: Redis is not a broker.
There is no queue/topic model, no vhost, and (mostly) no dead-letter
concept. The mockup (`SbConsole Redis.dc.html`, screens 1a–1g) builds the
plugin around four things Redis actually is:

1. A **keyspace** browsed with `SCAN` (never `KEYS`) — the browser shows scan
   progress rather than pretending it knows the total up front.
2. **Type-aware values** — each of the six data types (string, hash, list,
   set, sorted set, stream) gets its own editor built around the command the
   type is actually used through, not a generic "blob" view.
3. **Streams** as the messaging surface — the pending-entries list (PEL) is
   Redis's closest thing to a dead-letter queue, so it gets the
   peek/claim/ack/delete treatment RabbitMQ/Kafka give their DLQs.
4. **Server health** — memory pressure and evictions are the numbers that
   page someone at 3am, so they anchor the Overview page.

Database index (`SELECT n`) sits in the page picker beside the connection,
the same role vhost plays for RabbitMQ and namespace plays for Service Bus.

This spec covers the full plugin surface (mockup screens 1a–1g). Per
`docs/design.md` §8's testing note and the "Scope Check" convention in
`superpowers:writing-plans`, it ships as **three plans**, mirroring how
RabbitMQ shipped (`rabbitmq-plugin.md` then `rabbitmq-phase2.md`) — each
phase is independently working, testable software:

- **Phase 1** (`docs/superpowers/plans/2026-09-28-redis-plugin.md`): plugin
  scaffold, connection handling (Standalone + Sentinel topologies), and a
  read-only Overview page (memory, hit ratio, evictions, memory-by-prefix,
  slow log), plus the local dev stack. **Correction (post-implementation):**
  the Keys browser (all five non-stream type editors, TTL/rename/delete/
  delete-by-pattern) described in this section's original draft did **not**
  ship in phase 1 — it turned out to be substantial enough on its own to
  warrant its own plan, consistent with §12's phase-1 scope note and this
  section's own three-phase list below. The Keys browser is now planned as
  its own follow-on plan (before Streams/Pub-Sub), not folded into phase 1.
  See §7 for what phase 1's UI actually is, and "Known Phase 1 limitations"
  at the end of this document.
- **Phase 2** (`…-redis-plugin-phase2.md`, written after phase 1 lands):
  Streams — overview, consumer groups, pending-entries list (claim/ack/
  XDEL), XADD, trim, dashboard integration.
- **Phase 3** (`…-redis-plugin-phase3.md`): Pub/Sub (channel list + live
  tail + publish), Cluster topology (per-shard SCAN, hides the db picker),
  FLUSHDB confirmation, remaining empty/error states (mockup 1g).

(The Keys browser, mentioned above as deferred out of phase 1, ships as its
own plan ahead of Streams — its scope is described in §7 pending that plan's
own spec addendum.)

Cluster is deferred past phase 1 specifically because its shard-enumeration
API needs verifying against the installed `StackExchange.Redis` version
once the package is actually pinned (§4) — Standalone and Sentinel cover
both single-node dev stacks and the "primary + replicas" mockup scenario
(1a's `redis-uk-prod`) without that risk.

## 2. Decisions

**Client library:** `StackExchange.Redis` (latest stable 2.8.x/2.9.x line,
pinned exactly like the other three plugins pin their client — see
`docker/README.md` new "Redis" section for the version actually resolved).

**Secret format.** Unlike Rabbit/Kafka/AWS's house `key=value;` convention,
Redis's own client already has a well-documented, fully-featured connection
string format (`ConfigurationOptions.Parse` / `.ToString()`) that natively
supports multiple endpoints, TLS, and ACL user/password — reinventing a
parser for all of that would be pure duplication. It does **not**, however,
escape commas inside a value (comma is the field/endpoint separator with no
escape syntax — a known, long-standing library limitation:
[#680](https://github.com/StackExchange/StackExchange.Redis/issues/680),
[#883](https://github.com/StackExchange/StackExchange.Redis/issues/883)), so
a generated password containing a comma would silently corrupt the parse.
`RedisConnectionSettings` therefore percent-encodes only the `User`/
`Password` values (`Uri.EscapeDataString`/`UnescapeDataString`) before
handing the string to `ConfigurationOptions.Parse`/`.ToString()` — genuinely
reusing the library for every other field (endpoints, TLS, ServiceName,
DefaultDatabase), with a minimal, targeted encoding layer on top for the
one gap it has, the same shape as RabbitMQ's own percent-encoding precedent
(`docs/design.md` §6.8) for a value its format can't otherwise carry safely.
The secret is a single string, an envelope of three parts (**corrected from
this section's original two-part draft** — implementation added a
`preferReplica` segment, since "read from replica when possible" is a
connection-level toggle that also can't be expressed unambiguously inside
`ConfigurationOptions` alone):

```
"{topology}|{preferReplica}|{configurationOptionsString}"
```

e.g. `"standalone|1|redis-01.uk.internal:6380,password=hunter2,ssl=True,user=sbconsole"`
or `"sentinel|0|sentinel-1:26379,sentinel-2:26379,serviceName=uk-prod,password=hunter2"`.

`topology` is one of `standalone` | `sentinel` | `cluster` (cluster wired in
phase 3, but the enum and parser handle all three from phase 1 so phase 3
doesn't have to touch the secret format). `preferReplica` is `0` or `1`.
The prefix is necessary because `ConfigurationOptions` alone can't
unambiguously distinguish "one endpoint, standalone" from "one seed
endpoint, cluster", reliably signal Sentinel vs. a replica list, or carry a
read-preference toggle — and the connection form (mockup 1f) needs to know
which fields to show *before* ever connecting. `RedisConnectionSettings`
(§3) owns parsing/building this envelope; it is pure logic, fully
unit-testable without a server, following `RabbitConfigParser`'s precedent
(`docs/design.md` §6.8) of a plugin-owned parser under `Client/`.
`User`/`Password` inside the `configurationOptionsString` portion are
percent-encoded (see the decision above this one) — a real example secret
looks like `"standalone|0|localhost:6379,password=hunter2"` for a
comma-free password, or with a comma in the password,
`"standalone|0|localhost:6379,password=hunter2%2Cwith-a-comma"`.

**"Vhost"-equivalent: database index.** Redis standalone/Sentinel expose 16
logical databases (`SELECT 0-15`); Cluster has exactly one (db0, hidden —
mockup 1g). The page header uses a single-level connection picker plus a
database-index picker (Kafka's simpler single-level pattern, not RabbitMQ's
two-level connection+vhost picker — there's no second axis to browse here).

**`GetOldestDeadLetterAsync`.** RabbitMQ leaves this at the SDK default
because peeking a queue (`basic.get`) has a side effect. Redis's `XPENDING`
summary form has none, and the pending-entries list is exactly the
"dead-letter" analogue the SDK hook exists for — so Redis **implements**
this properly in phase 2, using the oldest pending entry's idle time to
derive an effective enqueued-at timestamp. Flagging this explicitly per
`docs/design.md` §6.8's convention of stating *why* a hook is or isn't
implemented, since it's a deliberate deviation from the most recent
plugin's precedent.

**Testing.** Same deliberate deferral as every other plugin
(`docs/design.md` §8): unit tests only, against a substitute of
`IRedisOperations` for handlers, plus pure-logic unit tests (parsers,
`RedisConnectionSettings`, `FriendlyRedisError`) with real captured
`INFO`/`XINFO`-style fixture text. No Testcontainers, no real Redis traffic,
until the plugin's shape proves out further.

## 3. Data types

All new files live under `src/Plugins/SbConsole.Plugins.Redis/Client/`
unless noted. Phase tag shows when each is *implemented*; interfaces that
later phases extend are still declared in full here so no later phase has
to change an earlier phase's public shape.

```csharp
// RedisTopology.cs — phase 1
public enum RedisTopology { Standalone, Sentinel, Cluster }

// RedisConnectionSettings.cs — phase 1
public sealed record RedisConnectionSettings(
    RedisTopology Topology,
    IReadOnlyList<string> EndpointHostPorts,   // "host:port" strings, 1+ entries
    string? User,
    string? Password,
    bool Ssl,
    string? ServiceName,                        // Sentinel only; null otherwise
    bool PreferReplicaForReads,
    int DefaultDatabase)                         // ignored (forced 0) when Topology == Cluster
{
    public static RedisConnectionSettings Parse(string secret);
    public string ToSecretString();
}

// ServerOverview.cs — phase 1 (shape below is what actually shipped —
// corrected from this section's original draft, which listed
// `EvictedPerMinute` in place of `ExpiredKeysTotal`)
public sealed record ServerOverview(
    string RedisVersion, string Mode, int ConnectedReplicas,
    long UsedMemoryBytes, long? MaxMemoryBytes, string MaxMemoryPolicy, double MemoryFragmentationRatio,
    double HitRatioPercent, long OpsPerSecond, long EvictedKeysTotal, long ExpiredKeysTotal,
    long ConnectedClients, long BlockedClients,
    IReadOnlyList<KeyspaceDatabaseInfo> Databases);

public sealed record KeyspaceDatabaseInfo(int Index, long KeyCount, long KeysWithExpiry);

// MemoryByPrefixEntry.cs — phase 1 (shape below is what actually shipped —
// corrected from this section's original draft, which had `EstimatedBytes`
// and a `Kind` discriminator in place of `SampledBytes`)
public sealed record MemoryByPrefixEntry(string Prefix, long SampledBytes, int SampledKeyCount, int KeysWithoutTtlCount);

public sealed record SlowLogEntry(DateTimeOffset At, TimeSpan Duration, string Command, string? ClientAddress);

// KeySummary.cs — phase 1
public enum RedisKeyType { String, List, Set, SortedSet, Hash, Stream, Unknown }

public sealed record KeySummary(string Key, RedisKeyType Type, TimeSpan? Ttl, long ApproximateSizeBytes);

public sealed record KeyScanPage(
    IReadOnlyList<KeySummary> Matches, long Cursor, bool Complete,
    long KeysScannedSoFar, long DatabaseKeyCountAtScanStart);

// Per-type value DTOs — phase 1
public sealed record StringValue(string Key, TimeSpan? Ttl, long SizeBytes, string RawValue, bool LooksLikeJson);

public sealed record HashFieldPage(IReadOnlyList<HashFieldEntry> Fields, long Cursor, bool Complete, long TotalFieldCount);
public sealed record HashFieldEntry(string Field, string Value, long SizeBytes);

public sealed record ListSlice(long Length, IReadOnlyList<ListEntry> Head, IReadOnlyList<ListEntry> Tail);
public sealed record ListEntry(long Index, string Value); // Index uses Redis's own negative-from-tail convention for Tail entries

public sealed record SortedSetSlice(long Cardinality, IReadOnlyList<SortedSetEntry> Entries, long RangeStart, long RangeStop);
public sealed record SortedSetEntry(string Member, double Score);

public sealed record SetPage(long Cardinality, IReadOnlyList<string> Members, long Cursor, bool Complete);

// Requests.cs — phase 1
public sealed record ScanKeysRequest(int Database, string? Pattern, RedisKeyType? TypeFilter, long Cursor, int Count);
public sealed record SetTtlRequest(int Database, string Key, TimeSpan? ExpireIn, DateTimeOffset? ExpireAt, bool Persist);
public sealed record DryRunDeleteByPatternResult(long WouldDeleteKeyCount, long WouldDeleteBytes, bool Complete);
```

**Corrections to the DTOs above (post-implementation):** `ServerOverview`
drops `EvictedPerMinute` — Redis's `INFO` only exposes a cumulative
`evicted_keys` counter, not a rate, and computing a real per-minute rate
needs the SDK's `MetricHistoryStore` sampling two points over time, a
bigger, separable piece of work not needed for the Overview page to be
useful; `EvictedKeysTotal` (cumulative) ships instead, and a rate can be
layered on later without changing this shape. `MemoryByPrefixEntry` drops
the `Kind`/`EstimatedBytes` naming in favor of `SampledBytes` — it reports
the actual sampled bytes for whatever keys were sampled (matching the
mockup's own "sampled · MEMORY USAGE" caption), not an extrapolated total,
and phase 1 doesn't fetch each key's type for this table (an extra round
trip with no functional use yet).

Stream/consumer-group/PEL DTOs (`StreamOverview`, `StreamConsumerGroupSummary`,
`StreamConsumer`, `StreamPendingEntry`, `StreamEntry`) and Pub/Sub DTOs
(`PubSubChannelSummary`, `PubSubMessage`) are specified in the phase 2 and
phase 3 spec addenda respectively, written when those phases start, per
`docs/design.md` §6.8's "follow the previous plugin's spec/plan almost
verbatim, changing only the domain specifics" convention — writing them now
would risk locking in shapes before phase 1's real `IRedisOperations`
patterns (parser-vs-thin-wrapper split, cursor handling) are proven out.

## 4. Operations interface

`Client/IRedisOperations.cs`. Every method takes the raw secret string as
its first parameter — one shared singleton instance serves every
connection, matching `IRabbitOperations`/`IKafkaOperations`/`ISqsOperations`
(`docs/design.md` §6.8).

```csharp
public interface IRedisOperations
{
    // Connection (phase 1)
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    // Overview (phase 1)
    Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default);
    Task<IReadOnlyList<SlowLogEntry>> GetSlowLogAsync(string secret, int count, CancellationToken ct = default);

    // Keys (phase 1)
    Task<KeyScanPage> ScanKeysAsync(string secret, ScanKeysRequest request, CancellationToken ct = default);
    Task<RedisKeyType> GetKeyTypeAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<bool> DeleteKeyAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<bool> RenameKeyAsync(string secret, int database, string key, string newKey, CancellationToken ct = default);
    Task SetTtlAsync(string secret, SetTtlRequest request, CancellationToken ct = default);
    Task<DryRunDeleteByPatternResult> DryRunDeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default);
    Task<long> DeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default);

    // String (phase 1)
    Task<StringValue> GetStringAsync(string secret, int database, string key, CancellationToken ct = default);
    Task SetStringAsync(string secret, int database, string key, string value, TimeSpan? ttl, CancellationToken ct = default);

    // Hash (phase 1)
    Task<HashFieldPage> ScanHashFieldsAsync(string secret, int database, string key, string? fieldPattern, long cursor, int count, CancellationToken ct = default);
    Task SetHashFieldAsync(string secret, int database, string key, string field, string value, CancellationToken ct = default);
    Task<bool> DeleteHashFieldAsync(string secret, int database, string key, string field, CancellationToken ct = default);

    // List (phase 1)
    Task<ListSlice> GetListSliceAsync(string secret, int database, string key, int headCount, int tailCount, CancellationToken ct = default);
    Task PushListAsync(string secret, int database, string key, string value, bool atHead, CancellationToken ct = default);
    Task TrimListAsync(string secret, int database, string key, long start, long stop, CancellationToken ct = default);

    // Sorted set (phase 1)
    Task<SortedSetSlice> GetSortedSetRangeAsync(string secret, int database, string key, long start, long stop, bool descending, CancellationToken ct = default);
    Task AddSortedSetMemberAsync(string secret, int database, string key, string member, double score, CancellationToken ct = default);
    Task<double> IncrementSortedSetScoreAsync(string secret, int database, string key, string member, double by, CancellationToken ct = default);

    // Set (phase 1)
    Task<SetPage> ScanSetMembersAsync(string secret, int database, string key, long cursor, int count, CancellationToken ct = default);
    Task<bool> IsSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);
    Task AddSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);
    Task<bool> RemoveSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);

    // Streams (phase 2) and Pub/Sub (phase 3) methods are added to this
    // interface by their own phase's plan — not declared here, so phase 1's
    // implementers aren't asked to stub methods with no caller yet.
}
```

Parsing of raw Redis replies (`INFO` text, `SCAN`'s multi-bulk cursor
reply, etc.) into these DTOs lives in **pure static parser classes**
(`RedisInfoParser`, `RedisMemoryByPrefixSampler`, `RedisSlowLogParser`)
that `RedisOperations` calls after fetching raw data — the same
seam RabbitMQ draws between `AmqpClient`/`ManagementApiClient` (real I/O,
not unit tested) and `Routing/DeadLetterTopology` etc. (pure logic, fully
unit tested with captured fixtures). `RedisOperations` itself stays thin
and isn't unit tested directly, consistent with `docs/design.md` §8's
Testcontainers deferral.

## 5. Key scanning (phase 1)

`ScanKeysAsync` never calls `KEYS`. It issues one raw `SCAN cursor MATCH
pattern COUNT n` per call via `IDatabase.ExecuteAsync("SCAN", ...)`,
parsing the two-element multi-bulk reply (`[nextCursor, RedisKey[]]`)
itself rather than using `IServer.KeysAsync`'s auto-paging enumerable —
the mockup's progress bar (`812k / 2.41M scanned`) needs the raw cursor
value and a `DBSIZE` baseline, which the auto-paging helper doesn't expose.
`KeyScanPage.Complete` is `nextCursor == 0`; the caller (Keys.razor, phase
1 task 9) loops calling `ScanKeysAsync` with the returned cursor until
either `Complete` or enough matches have accumulated for one UI page,
mirroring the mockup's "142 matches so far · COUNT 1000 per step" copy.

`GetKeyTypeAsync` uses `TYPE` (O(1), needed before the UI knows which
editor to render for a scan-list selection). Each per-type value DTO's
`Get*Async` additionally reads TTL (`PTTL`) and an approximate size
(`MEMORY USAGE key SAMPLES 0`, matching the mockup's per-row "4 KB"/"38 KB"
badges and the value panel's "38 KB" header stat) alongside the value
itself, in parallel via `Task.WhenAll`.

`DryRunDeleteByPatternAsync` runs the same `SCAN`/`MATCH` loop to
completion, summing `MEMORY USAGE` for every match, without deleting
anything — the mockup's "Delete matching keys" dialog always shows this
dry-run result before the destructive `DeleteByPatternAsync` (itself
`UNLINK` in batches of 500, matching the dialog's "Method: UNLINK, batches
of 500" copy — `UNLINK` over `DEL` because it's non-blocking on large
values, the same reasoning Redis's own docs give).

## 6. Mutating actions and `ActionRisk`

| Action | Risk | Notes |
|---|---|---|
| Set string / hash field / list push / set add / zset add/incr | `Mutating` | writes, never destructive alone |
| Rename key | `Mutating` | |
| Set TTL / Persist | `Mutating` | |
| Delete key | `Destructive` | single key |
| Delete hash field / remove set member | `Mutating` | removes part of a key, not the key itself |
| Trim list | `Destructive` | can drop members permanently |
| Delete by pattern | `Destructive` | always preceded by a dry run (§5); confirmation shows the dry-run count, matching `docs/design.md` §6.1's rule that destructive confirmations must reflect real server state, not a client guess |

All follow the universal rule (`docs/design.md` §7): `IsProd` for the
confirmation dialog always comes from the server-resolved `ConnectionInfo`
(`IConnectionProvider`), never a client-supplied flag; audit rows are
written for attempted destructive commands even on failure.

## 7. UI (phase 1 — corrected to match what actually shipped)

**This section is corrected post-implementation.** The original draft below
described four nav items (Overview/Keys/Streams/Pub-Sub) with placeholder
pages for the unbuilt ones, an auto-refresh mechanism, and a
`StaleDataBanner`. None of that shipped in phase 1. What phase 1 actually
has:

- `RedisPlugin.NavItems` is a **single** item:
  `new PluginNavItem("Overview", "/p/redis/overview")` (no icon argument —
  matching the other four plugins' convention of passing none, rather than
  a string MudBlazor's `Icon` property, which expects SVG path markup, can't
  render). Keys/Streams/Pub-Sub nav items are **not** present yet — they are
  added by whichever later plan builds that page, rather than being
  stubbed out ahead of time with placeholder bodies. This is a deliberate
  change from the original draft's "ship all four nav items up front" plan:
  it turned out simpler to add a nav item exactly when its page exists.
- **`Pages/Overview.razor`** (`/p/redis/overview`): connection picker (no
  db picker — overview is server-wide), a memory/hit-ratio/ops/evicted-count
  tile row, a memory-by-prefix table, and a slow log table. Failures in the
  memory-by-prefix or slow-log sub-calls render an inline `MudAlert`
  warning next to that table (showing the friendly error, including
  ACL/NOPERM text) without blanking the rest of the page. There is **no**
  auto-refresh and **no** `StaleDataBanner` in phase 1 — the page loads once
  per connection selection; the mockup's auto-refresh interval and
  stale-data dimming are deferred (see "Known Phase 1 limitations" below).
- **`Client/RedisConnectionFields.razor`** (mockup 1f's connection form):
  topology select (Standalone/Sentinel/Cluster — Cluster option present but
  its extra fields are phase 3; selecting it in phase 1 just stores the
  topology token and `TestConnectionAsync`/`RedisConnectionFactory` reject
  it clearly at connect time), host:port, service name (Sentinel only —
  and only actually applied to the connection when the topology is
  Sentinel; see the Sentinel note below), ACL user/password, TLS checkbox,
  "read from replica when possible" checkbox. `IsProd`-and-no-TLS shows a
  `MudAlert` warning, same pattern as `RabbitConnectionFields`.
- **Keys browser, Streams, Pub/Sub pages**: not built in phase 1 (see §1's
  correction) — separate, later plans.

**Sentinel note:** `ServiceName` is only carried onto the real
`ConfigurationOptions` when `Topology == Sentinel` — a leftover value from
switching the topology control away from Sentinel is dropped rather than
silently putting a Standalone connection into Sentinel mode (setting
`ServiceName` is what triggers Sentinel mode in StackExchange.Redis).
Conversely, `RedisConnectionFactory` rejects a Sentinel-topology connection
with no service name before attempting to connect, rather than silently
connecting to the sentinel node itself as if it were the primary.

## 8. Dashboard problems and nav badge (phase 1 scope — corrected)

Phase 1 implements a memory-pressure `GetDashboardProblemsAsync`:

```csharp
private const double MemoryProblemThresholdPercent = 90.0;

public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
    Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
{
    var overview = await new RedisOperations().GetOverviewAsync(connectionString, ct);
    if (overview.MaxMemoryBytes is not { } max || max <= 0) return [];
    var usedPercent = 100.0 * overview.UsedMemoryBytes / max;
    if (usedPercent < MemoryProblemThresholdPercent) return [];
    return [new PluginDashboardProblem("Warning",
        "Memory usage is approaching the eviction threshold",
        $"{usedPercent:N0}% of {FormatBytes(max)} maxmemory ({overview.MaxMemoryPolicy})",
        "/p/redis/overview")];
}
```

**Correction (post-implementation): `GetDashboardMetricsAsync` returns no
metrics** (`[]`), not the "used memory %"/"hit ratio %" pair this section
originally specified. The host's dashboard (`Home.razor`) **sums**
same-label `PluginDashboardMetric` values across every connection of a
plugin — appropriate for a count, meaningless for a percentage (two Redis
connections at 60% and 90% memory would render as "150"). The real
memory-pressure signal is already surfaced correctly, per-connection, via
`GetDashboardProblemsAsync` above, since problems are listed individually
rather than summed. Rather than invent a new summable metric (e.g. a
connections-over-threshold count) without an actual dashboard consumer for
it, this hook reports nothing for now; revisit once there's a real need.

The "pel" nav badge shown on the Streams item in the mockup (1a/1b/1d's
sidebar) is `GetNavBadgeAsync` returning the total pending-entry count
across streams — implemented in **phase 2** once `IRedisOperations` has a
stream method to call; phase 1's `RedisPlugin` leaves it at the SDK
default (returns `null`, no badge shown), which is indistinguishable in
the UI from "no pending entries" and requires no phase-1 stub.

## 9. Handlers

Folder-per-resource under the plugin root (`Keys/`, `Overview/`), same
shape as every other plugin. A shared `HandlerRunner` (copied and adapted
from RabbitMQ's — `docs/design.md` §6.8 — plugins don't share code beyond
`Sdk`) centralizes the resolve-secret → try → friendly-error → audit
boilerplate for command handlers, and a plain resolve-secret → run for
query handlers.

Audit action naming: `redis.{resource}.{verb}` — e.g. `redis.key.delete`,
`redis.key.setttl`, `redis.pattern.delete`, `redis.hash.setfield`. Audit
target: `"{connectionName}/db{n}/{key}"` (no vhost axis; database index
takes its place) via a `HandlerRunner.Target(connectionName, database, key)`
helper.

## 10. Error handling

`Client/FriendlyRedisError.cs`, on top of `SbConsole.Sdk.FriendlyError`,
maps `StackExchange.Redis` exception types before falling back to the
generic truncator:

- `RedisConnectionException` → `"Could not reach Redis: {FailureType}"`
- `RedisTimeoutException` → `"Redis command timed out"`
- `RedisServerException` with message starting `NOAUTH` → `"Authentication required or failed (NOAUTH)"`
- `RedisServerException` with message starting `NOPERM` → `"Command refused by ACL (NOPERM)"` — surfaced verbatim in the UI per mockup 1g's "Command refused by ACL" state, since the message already names the missing permission
- `RedisServerException` with message starting `WRONGTYPE` → `"Wrong type for this key — it may have changed since the page loaded"`
- `RedisServerException` with message starting `CLUSTERDOWN` → `"Cluster is down"`
- anything else → `SbConsole.Sdk.FriendlyError.From(ex)`

## 11. Local dev stack

`docker-compose.yml` gains a `redis` service (image `redis:7-alpine`,
`profiles: ["redis"]`, `command: ["redis-server", "--requirepass",
"${REDIS_PASSWORD:-sbconsole}"]`, healthcheck via `redis-cli … ping`) and a
`redis-init` one-shot seeding a handful of sample keys (a `session:*` hash
or two, a `jobs:*` list, a `lb:*` sorted set, a `flags:*` set) via a mounted
script — matching the mockup's own sample data — so the Keys/Overview pages
have something to show without hand-seeding. `docker/README.md` gains a
"Redis (opt-in)" section following the RabbitMQ section's exact structure
(quick-start command, host-address table, "what's seeded and why", "connect
the app to it" with the literal secret string to paste, plus `REDIS_PORT`
added to the overrides table and `--profile redis` added to the reset
command). Streams/consumer-group seeding is added in phase 2's docker
changes, once the stream DTOs exist to seed meaningfully.

## 12. Out of scope (phase 1)

- Streams, consumer groups, pending-entries list, Pub/Sub — phases 2/3.
- Cluster topology's actual connection/shard-scan behavior — the enum
  value and connection-form field exist, but selecting Cluster in phase 1
  produces a `TestConnectionAsync` failure with a clear "Cluster support
  ships in a later update" message rather than attempting a real
  connection with unverified APIs.
- FLUSHDB and the ACL-aware disabled-button state (mockup 1f/1g) — phase 3,
  once Pub/Sub's ACL-checking precedent (`ACL WHOAMI`/`ACL GETUSER`) exists
  to reuse for the confirm dialog's ACL-denial notice.
- Big-keys report, Sentinel failover view (the mockup's own "try next"
  suggestions at the bottom of the doc) — not requested, not planned.
- The Keys browser (all five non-stream type editors, TTL/rename/delete/
  delete-by-pattern) — originally drafted as part of phase 1 (§1), it
  turned out substantial enough to warrant its own plan; see §1's
  correction.

## Known Phase 1 limitations

Flagged by the closing whole-branch review as legitimate but larger-scope
items, each deliberately deferred rather than fixed under this phase's time
pressure. Revisit these when the Keys plan lands, since several of them
(connection caching, per-key I/O concurrency) get materially more
important once that plan adds many more per-key round trips per page:

- **Sentinel is effectively single-endpoint.** The connection form has one
  Host:port field; a real Sentinel HA setup needs multiple seed endpoints.
  Not fixed in the UI — revisit with the Keys plan or whenever multi-seed
  Sentinel support is actually needed.
- **Memory-by-prefix sampling is sequential**, and can block the Overview
  page for tens of seconds against a large remote keyspace (up to 5,000
  keys, each a sequential `MEMORY USAGE` + TTL round trip) — worth
  refactoring to concurrent `Task.WhenAll` before/during the Keys plan,
  which will have much more of this kind of per-key I/O.
- **"Read from replica" (`PreferReplicaForReads`) is stored and
  round-tripped through the secret but not actually applied** — no
  `CommandFlags.PreferReplica` anywhere yet.
- **No connection multiplexer caching** — every operation call does a full
  `ConnectionMultiplexer` handshake; an Overview page load alone does
  three. Acceptable for this phase's read-only, low-traffic page, but
  should land before the Keys plan, which will make many more calls per
  page.
- **`RedisConnectionFactory.ConnectAsync(...).WaitAsync(ct)` can leak a
  connection on cancellation** — the underlying connect isn't actually
  cancelled, just abandoned by the awaiting task, and the resulting
  multiplexer (once it finishes connecting) is never disposed.
- **`AbortOnConnectFail = false`** on these short-lived, one-shot
  connections means connect/auth failures surface later as generic errors
  rather than a clear failure type early — worth revisiting.
