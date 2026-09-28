using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Every method takes the raw connection secret as its first parameter — one shared instance
/// serves every connection (docs/design.md §6.8). Grows with each later task/plan; see
/// docs/superpowers/specs/2026-09-28-redis-plugin-design.md §4 for the full target shape.
/// </summary>
public interface IRedisOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(
        string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default);

    Task<IReadOnlyList<SlowLogEntry>> GetSlowLogAsync(string secret, int count, CancellationToken ct = default);

    // Keys — see docs/superpowers/specs/2026-09-28-redis-plugin-design.md §4-§5.
    Task<KeyScanPage> ScanKeysAsync(string secret, ScanKeysRequest request, CancellationToken ct = default);
    Task<RedisKeyType> GetKeyTypeAsync(string secret, int database, string key, CancellationToken ct = default);

    Task<KeyMetadata> GetKeyMetadataAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<bool> DeleteKeyAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<bool> RenameKeyAsync(string secret, int database, string key, string newKey, CancellationToken ct = default);
    Task SetTtlAsync(string secret, SetTtlRequest request, CancellationToken ct = default);
    Task<DryRunDeleteByPatternResult> DryRunDeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default);
    Task<long> DeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default);

    // String
    Task<StringValue> GetStringAsync(string secret, int database, string key, CancellationToken ct = default);
    Task SetStringAsync(string secret, int database, string key, string value, TimeSpan? ttl, CancellationToken ct = default);

    // Hash
    Task<HashFieldPage> ScanHashFieldsAsync(string secret, int database, string key, string? fieldPattern, long cursor, int count, CancellationToken ct = default);
    Task SetHashFieldAsync(string secret, int database, string key, string field, string value, CancellationToken ct = default);
    Task<bool> DeleteHashFieldAsync(string secret, int database, string key, string field, CancellationToken ct = default);

    // List
    Task<ListSlice> GetListSliceAsync(string secret, int database, string key, int headCount, int tailCount, CancellationToken ct = default);
    Task PushListAsync(string secret, int database, string key, string value, bool atHead, CancellationToken ct = default);
    Task TrimListAsync(string secret, int database, string key, long start, long stop, CancellationToken ct = default);

    // Sorted set
    Task<SortedSetSlice> GetSortedSetRangeAsync(string secret, int database, string key, long start, long stop, bool descending, CancellationToken ct = default);
    Task AddSortedSetMemberAsync(string secret, int database, string key, string member, double score, CancellationToken ct = default);
    Task<double> IncrementSortedSetScoreAsync(string secret, int database, string key, string member, double by, CancellationToken ct = default);

    // Set
    Task<SetPage> ScanSetMembersAsync(string secret, int database, string key, long cursor, int count, CancellationToken ct = default);
    Task<bool> IsSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);
    Task AddSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);
    Task<bool> RemoveSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default);

    // Streams (phase 2 -- design spec addendum §3a/§4a).
    Task<StreamScanPage> ScanStreamsAsync(string secret, ScanStreamsRequest request, CancellationToken ct = default);
    Task<StreamOverview> GetStreamOverviewAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<IReadOnlyList<StreamConsumerGroupSummary>> GetStreamConsumerGroupsAsync(string secret, int database, string key, CancellationToken ct = default);
    Task<IReadOnlyList<StreamConsumer>> GetStreamConsumersAsync(string secret, int database, string key, string group, CancellationToken ct = default);
    Task<IReadOnlyList<StreamPendingEntry>> GetStreamPendingEntriesAsync(string secret, int database, string key, string group, int count, CancellationToken ct = default);

    /// <summary>Most-recent-first (XREVRANGE). beforeId, when given, is an exclusive upper bound so the caller can page further back into history.</summary>
    Task<IReadOnlyList<StreamEntry>> GetStreamEntriesAsync(string secret, int database, string key, string? beforeId, int count, CancellationToken ct = default);

    /// <summary>One entry by exact id (XRANGE id id), or null if it has been deleted/trimmed.</summary>
    Task<StreamEntry?> GetStreamEntryAsync(string secret, int database, string key, string entryId, CancellationToken ct = default);

    /// <summary>XADD. Returns the server-generated entry id.</summary>
    Task<string> AddStreamEntryAsync(string secret, int database, string key, IReadOnlyList<StreamFieldValue> fields, long? maxLen, CancellationToken ct = default);

    /// <summary>XTRIM (approximate maxlen). Returns the number of entries evicted.</summary>
    Task<long> TrimStreamAsync(string secret, int database, string key, long maxLen, CancellationToken ct = default);

    /// <summary>XACK. Returns the number of entries actually acknowledged.</summary>
    Task<long> AcknowledgeStreamEntriesAsync(string secret, int database, string key, string group, IReadOnlyList<string> entryIds, CancellationToken ct = default);

    /// <summary>XCLAIM. Returns the claimed entries (empty for any id that no longer exists/wasn't pending).</summary>
    Task<IReadOnlyList<StreamEntry>> ClaimStreamEntriesAsync(string secret, int database, string key, string group, string consumer, long minIdleTimeMs, IReadOnlyList<string> entryIds, CancellationToken ct = default);

    /// <summary>XDEL. Returns the number of entries actually deleted.</summary>
    Task<long> DeleteStreamEntriesAsync(string secret, int database, string key, IReadOnlyList<string> entryIds, CancellationToken ct = default);

    /// <summary>XGROUP DELCONSUMER -- returns how many pending entries the consumer still owned (they are dropped from the PEL).</summary>
    Task<long> DeleteStreamConsumerAsync(string secret, int database, string key, string group, string consumer, CancellationToken ct = default);

    // ---- Pub/Sub (phase 3 -- design spec addendum §14). Server-wide, not per-database. ----

    /// <summary>PUBSUB CHANNELS [pattern] + PUBSUB NUMSUB, joined into one summary per active channel.</summary>
    Task<IReadOnlyList<PubSubChannelSummary>> GetPubSubChannelsAsync(string secret, string? pattern, CancellationToken ct = default);

    /// <summary>
    /// SUBSCRIBE (isPattern: false) or PSUBSCRIBE (isPattern: true), delivering every message to
    /// onMessage until the returned subscription is disposed. Unlike every other operation, the
    /// returned IAsyncDisposable owns a live ConnectionMultiplexer that stays connected until then --
    /// callers must dispose it exactly once they stop watching (see RedisPubSubSubscription).
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string secret, string channelOrPattern, bool isPattern, Func<PubSubMessage, Task> onMessage, CancellationToken ct = default);

    /// <summary>PUBLISH. Returns the number of clients that received the message.</summary>
    Task<long> PublishAsync(string secret, string channel, string payload, CancellationToken ct = default);

    // ---- FLUSHDB (deferred from phase 1 -- design spec §12/§14) ----

    /// <summary>FLUSHDB against the given database index. Destructive -- wipes every key in that database.</summary>
    Task FlushDatabaseAsync(string secret, int database, CancellationToken ct = default);

    // ---- ACL-aware disabled-button state (design spec §12/§14) ----

    /// <summary>
    /// Best-effort check of whether the connection's current ACL user is allowed to run the given
    /// command (ACL WHOAMI, then ACL GETUSER) -- advisory only, used to proactively disable a
    /// button; see RedisAclPermissionEvaluator for what "best-effort" means here and why a failure
    /// to determine the answer returns true (fails open) rather than false.
    /// </summary>
    Task<bool> HasCommandPermissionAsync(string secret, string command, CancellationToken ct = default);
}
