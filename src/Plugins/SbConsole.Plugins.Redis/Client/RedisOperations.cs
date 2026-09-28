using System.Globalization;
using System.Text;
using SbConsole.Sdk;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public sealed class RedisOperations : IRedisOperations
{
    // UNLINK in batches of 500 (design spec §5) -- non-blocking on large values, unlike DEL.
    private const int DeleteByPatternBatchSize = 500;
    private const int PatternScanCount = 1000;
    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
            var db = muxer.GetDatabase();
            var ping = await db.PingAsync();

            var server = muxer.GetServer(muxer.GetEndPoints()[0]);
            var sections = await GetInfoSectionsAsync(server, ct);
            var version = sections.TryGetValue("Server", out var serverSection)
                && serverSection.TryGetValue("redis_version", out var v) ? v : "unknown";

            return new ConnectionTestResult(
                Success: true,
                ErrorMessage: null,
                Identity: $"Redis {version}",
                Checks: [new ConnectionCheck("PING", ConnectionCheckStatus.Passed, $"{ping.TotalMilliseconds:N1} ms")]);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyRedisError.From(ex), null, null);
        }
    }

    public async Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);
        var sections = await GetInfoSectionsAsync(server, ct);
        return RedisInfoParser.Parse(sections);
    }

    public async Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(
        string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);
        var db = muxer.GetDatabase(database);

        var samples = new List<SampledKey>();
        await foreach (var key in server.KeysAsync(database: database, pattern: "*", pageSize: 1000))
        {
            if (samples.Count >= sampleSize)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var sizeResult = await db.ExecuteAsync("MEMORY", "USAGE", (string)key!);
            var size = sizeResult.IsNull ? 0L : (long)sizeResult;
            var ttl = await db.KeyTimeToLiveAsync(key);
            samples.Add(new SampledKey(key!, size, ttl.HasValue));
        }

        return RedisMemoryByPrefixSampler.Summarize(samples, delimiter, topN: 4);
    }

    public async Task<IReadOnlyList<SlowLogEntry>> GetSlowLogAsync(string secret, int count, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase();
        var raw = await db.ExecuteAsync("SLOWLOG", "GET", count.ToString()).WaitAsync(ct);
        return RedisSlowLogParser.Parse(raw);
    }

    internal static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> GetInfoSectionsAsync(
        IServer server, CancellationToken ct)
    {
        var groups = await server.InfoAsync().WaitAsync(ct);
        return groups.ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    // ---- Keys (design spec §5) ----

    public async Task<KeyScanPage> ScanKeysAsync(string secret, ScanKeysRequest request, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(request.Database);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);

        var args = new List<object>
        {
            request.Cursor.ToString(CultureInfo.InvariantCulture),
            "MATCH", request.Pattern is { Length: > 0 } p ? p : "*",
            "COUNT", request.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (request.TypeFilter is { } typeFilter && RedisKeyTypeNames.ToScanToken(typeFilter) is { } token)
        {
            args.Add("TYPE");
            args.Add(token);
        }

        var reply = await db.ExecuteAsync("SCAN", args.ToArray()).WaitAsync(ct);
        var (nextCursor, keys) = RedisScanReplyParser.Parse(reply);

        var matches = await Task.WhenAll(keys.Select(k => DescribeKeyAsync(db, k, ct)));
        var dbSize = await server.DatabaseSizeAsync(request.Database).WaitAsync(ct);

        return new KeyScanPage(matches, nextCursor, nextCursor == 0, request.Count, dbSize);
    }

    private static async Task<KeySummary> DescribeKeyAsync(IDatabase db, string key, CancellationToken ct)
    {
        var typeTask = db.KeyTypeAsync(key);
        var ttlTask = db.KeyTimeToLiveAsync(key);
        var sizeTask = db.ExecuteAsync("MEMORY", "USAGE", key, "SAMPLES", "0");
        await Task.WhenAll(typeTask, ttlTask, sizeTask).WaitAsync(ct);

        var size = sizeTask.Result.IsNull ? 0L : (long)sizeTask.Result;
        return new KeySummary(key, RedisKeyTypeNames.FromRedisType(typeTask.Result), ttlTask.Result, size);
    }

    public async Task<RedisKeyType> GetKeyTypeAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var type = await db.KeyTypeAsync(key).WaitAsync(ct);
        return RedisKeyTypeNames.FromRedisType(type);
    }

    public async Task<KeyMetadata> GetKeyMetadataAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var encodingTask = db.KeyEncodingAsync(key);
        var idleTask = TryIdleTimeAsync(db, key);
        await Task.WhenAll(encodingTask, idleTask).WaitAsync(ct);
        return new KeyMetadata(encodingTask.Result, idleTask.Result);
    }

    // OBJECT IDLETIME is refused under an LFU maxmemory-policy; that is a display nicety, not an error.
    private static async Task<TimeSpan?> TryIdleTimeAsync(IDatabase db, string key)
    {
        try
        {
            return await db.KeyIdleTimeAsync(key);
        }
        catch (RedisServerException)
        {
            return null;
        }
    }

    public async Task<bool> DeleteKeyAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.KeyDeleteAsync(key).WaitAsync(ct);
    }

    public async Task<bool> RenameKeyAsync(string secret, int database, string key, string newKey, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.KeyRenameAsync(key, newKey).WaitAsync(ct);
    }

    public async Task SetTtlAsync(string secret, SetTtlRequest request, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(request.Database);

        if (request.Persist)
        {
            await db.KeyPersistAsync(request.Key).WaitAsync(ct);
        }
        else if (request.ExpireIn is { } expireIn)
        {
            await db.KeyExpireAsync(request.Key, expireIn).WaitAsync(ct);
        }
        else if (request.ExpireAt is { } expireAt)
        {
            await db.KeyExpireAsync(request.Key, expireAt.UtcDateTime).WaitAsync(ct);
        }
        else
        {
            throw new ArgumentException("SetTtlRequest must set Persist, ExpireIn, or ExpireAt.", nameof(request));
        }
    }

    public async Task<DryRunDeleteByPatternResult> DryRunDeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        long cursor = 0;
        long count = 0;
        long bytes = 0;
        do
        {
            var reply = await db.ExecuteAsync("SCAN", cursor.ToString(CultureInfo.InvariantCulture), "MATCH", pattern, "COUNT", PatternScanCount.ToString(CultureInfo.InvariantCulture)).WaitAsync(ct);
            var (nextCursor, keys) = RedisScanReplyParser.Parse(reply);
            cursor = nextCursor;

            if (keys.Count > 0)
            {
                var sizes = await Task.WhenAll(keys.Select(k => db.ExecuteAsync("MEMORY", "USAGE", k, "SAMPLES", "0")));
                count += keys.Count;
                bytes += sizes.Sum(s => s.IsNull ? 0L : (long)s);
            }
        } while (cursor != 0);

        return new DryRunDeleteByPatternResult(count, bytes, Complete: true);
    }

    public async Task<long> DeleteByPatternAsync(string secret, int database, string pattern, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        long cursor = 0;
        long deleted = 0;
        var pending = new List<string>(DeleteByPatternBatchSize);
        do
        {
            var reply = await db.ExecuteAsync("SCAN", cursor.ToString(CultureInfo.InvariantCulture), "MATCH", pattern, "COUNT", PatternScanCount.ToString(CultureInfo.InvariantCulture)).WaitAsync(ct);
            var (nextCursor, keys) = RedisScanReplyParser.Parse(reply);
            cursor = nextCursor;
            pending.AddRange(keys);

            while (pending.Count >= DeleteByPatternBatchSize)
            {
                deleted += await UnlinkBatchAsync(db, pending.Take(DeleteByPatternBatchSize), ct);
                pending.RemoveRange(0, DeleteByPatternBatchSize);
            }
        } while (cursor != 0);

        if (pending.Count > 0)
        {
            deleted += await UnlinkBatchAsync(db, pending, ct);
        }

        return deleted;
    }

    private static async Task<long> UnlinkBatchAsync(IDatabase db, IEnumerable<string> keys, CancellationToken ct)
    {
        var args = keys.Cast<object>().ToArray();
        if (args.Length == 0)
        {
            return 0;
        }

        var result = await db.ExecuteAsync("UNLINK", args).WaitAsync(ct);
        return (long)result;
    }

    // ---- String ----

    public async Task<StringValue> GetStringAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        var valueTask = db.StringGetAsync(key);
        var ttlTask = db.KeyTimeToLiveAsync(key);
        var sizeTask = db.ExecuteAsync("MEMORY", "USAGE", key, "SAMPLES", "0");
        await Task.WhenAll(valueTask, ttlTask, sizeTask).WaitAsync(ct);

        var raw = valueTask.Result.IsNullOrEmpty ? "" : (string)valueTask.Result!;
        var size = sizeTask.Result.IsNull ? 0L : (long)sizeTask.Result;
        return new StringValue(key, ttlTask.Result, size, raw, RedisValuePreview.LooksLikeJson(raw));
    }

    public async Task SetStringAsync(string secret, int database, string key, string value, TimeSpan? ttl, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        await db.StringSetAsync(key, value, ttl, When.Always).WaitAsync(ct);
    }

    // ---- Hash ----

    public async Task<HashFieldPage> ScanHashFieldsAsync(string secret, int database, string key, string? fieldPattern, long cursor, int count, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        var reply = await db.ExecuteAsync("HSCAN", key, cursor.ToString(CultureInfo.InvariantCulture), "MATCH", fieldPattern is { Length: > 0 } p ? p : "*", "COUNT", count.ToString(CultureInfo.InvariantCulture)).WaitAsync(ct);
        var (nextCursor, items) = RedisScanReplyParser.Parse(reply);

        var fields = new List<HashFieldEntry>(items.Count / 2);
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            var value = items[i + 1];
            fields.Add(new HashFieldEntry(items[i], value, Encoding.UTF8.GetByteCount(value)));
        }

        var totalCount = await db.HashLengthAsync(key).WaitAsync(ct);
        return new HashFieldPage(fields, nextCursor, nextCursor == 0, totalCount);
    }

    public async Task SetHashFieldAsync(string secret, int database, string key, string field, string value, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        await db.HashSetAsync(key, field, value).WaitAsync(ct);
    }

    public async Task<bool> DeleteHashFieldAsync(string secret, int database, string key, string field, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.HashDeleteAsync(key, field).WaitAsync(ct);
    }

    // ---- List ----

    public async Task<ListSlice> GetListSliceAsync(string secret, int database, string key, int headCount, int tailCount, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        var lengthTask = db.ListLengthAsync(key);
        var headTask = headCount > 0 ? db.ListRangeAsync(key, 0, headCount - 1) : Task.FromResult(Array.Empty<RedisValue>());
        var tailTask = tailCount > 0 ? db.ListRangeAsync(key, -tailCount, -1) : Task.FromResult(Array.Empty<RedisValue>());
        await Task.WhenAll(lengthTask, headTask, tailTask).WaitAsync(ct);

        var head = headTask.Result.Select((v, i) => new ListEntry(i, (string)v!)).ToList();
        var tailValues = tailTask.Result;
        // Tail entries use Redis's own negative-from-tail LRANGE convention (design spec §3).
        var tail = tailValues.Select((v, i) => new ListEntry(-(tailValues.Length - i), (string)v!)).ToList();

        return new ListSlice(lengthTask.Result, head, tail);
    }

    public async Task PushListAsync(string secret, int database, string key, string value, bool atHead, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        if (atHead)
        {
            await db.ListLeftPushAsync(key, value).WaitAsync(ct);
        }
        else
        {
            await db.ListRightPushAsync(key, value).WaitAsync(ct);
        }
    }

    public async Task TrimListAsync(string secret, int database, string key, long start, long stop, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        await db.ListTrimAsync(key, start, stop).WaitAsync(ct);
    }

    // ---- Sorted set ----

    public async Task<SortedSetSlice> GetSortedSetRangeAsync(string secret, int database, string key, long start, long stop, bool descending, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        var cardTask = db.SortedSetLengthAsync(key);
        var order = descending ? Order.Descending : Order.Ascending;
        var entriesTask = db.SortedSetRangeByRankWithScoresAsync(key, start, stop, order);
        await Task.WhenAll(cardTask, entriesTask).WaitAsync(ct);

        var entries = entriesTask.Result.Select(e => new SortedSetEntry((string)e.Element!, e.Score)).ToList();
        return new SortedSetSlice(cardTask.Result, entries, start, stop);
    }

    public async Task AddSortedSetMemberAsync(string secret, int database, string key, string member, double score, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        await db.SortedSetAddAsync(key, member, score).WaitAsync(ct);
    }

    public async Task<double> IncrementSortedSetScoreAsync(string secret, int database, string key, string member, double by, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.SortedSetIncrementAsync(key, member, by).WaitAsync(ct);
    }

    // ---- Set ----

    public async Task<SetPage> ScanSetMembersAsync(string secret, int database, string key, long cursor, int count, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);

        var cardTask = db.SetLengthAsync(key);
        var reply = await db.ExecuteAsync("SSCAN", key, cursor.ToString(CultureInfo.InvariantCulture), "COUNT", count.ToString(CultureInfo.InvariantCulture)).WaitAsync(ct);
        var (nextCursor, members) = RedisScanReplyParser.Parse(reply);
        var cardinality = await cardTask.WaitAsync(ct);

        return new SetPage(cardinality, members, nextCursor, nextCursor == 0);
    }

    public async Task<bool> IsSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.SetContainsAsync(key, member).WaitAsync(ct);
    }

    public async Task AddSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        await db.SetAddAsync(key, member).WaitAsync(ct);
    }

    public async Task<bool> RemoveSetMemberAsync(string secret, int database, string key, string member, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.SetRemoveAsync(key, member).WaitAsync(ct);
    }

    // ---- Streams (phase 2) ----
    // XINFO STREAM/GROUPS/CONSUMERS and XPENDING all return StackExchange.Redis structs
    // (StreamInfo, StreamGroupInfo, StreamConsumerInfo, StreamPendingMessageInfo) with non-public
    // constructors, so -- unlike RedisStreamEntryMapper's StreamEntry/NameValueEntry mapping --
    // none of the mapping below can be pulled out into a pure, fixture-testable parser; it stays
    // inline here, untested, matching every other call in this thin wrapper (design spec §4a).

    public async Task<StreamScanPage> ScanStreamsAsync(string secret, ScanStreamsRequest request, CancellationToken ct = default)
    {
        // No native "list all streams" command -- SCAN ... TYPE stream, reusing the exact same
        // raw-SCAN/RedisScanReplyParser machinery ScanKeysAsync already uses (design spec §5),
        // rather than duplicating a second cursor-parsing path.
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(request.Database);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);

        var reply = await db.ExecuteAsync(
            "SCAN",
            request.Cursor.ToString(CultureInfo.InvariantCulture),
            "MATCH", request.Pattern is { Length: > 0 } p ? p : "*",
            "COUNT", request.Count.ToString(CultureInfo.InvariantCulture),
            "TYPE", "stream").WaitAsync(ct);
        var (nextCursor, keys) = RedisScanReplyParser.Parse(reply);

        var matches = await Task.WhenAll(keys.Select(k => DescribeStreamAsync(db, k, ct)));
        var dbSize = await server.DatabaseSizeAsync(request.Database).WaitAsync(ct);

        return new StreamScanPage(matches, nextCursor, nextCursor == 0, request.Count, dbSize);
    }

    private static async Task<StreamSummary> DescribeStreamAsync(IDatabase db, string key, CancellationToken ct)
    {
        var info = await db.StreamInfoAsync(key).WaitAsync(ct);
        return new StreamSummary(
            key,
            info.Length,
            info.LastGeneratedId.ToString(),
            info.FirstEntry.IsNull ? null : info.FirstEntry.Id.ToString(),
            info.LastEntry.IsNull ? null : info.LastEntry.Id.ToString());
    }

    public async Task<StreamOverview> GetStreamOverviewAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var info = await db.StreamInfoAsync(key).WaitAsync(ct);

        return new StreamOverview(
            key,
            info.Length,
            info.RadixTreeKeys,
            info.RadixTreeNodes,
            info.ConsumerGroupCount,
            info.LastGeneratedId.ToString(),
            info.FirstEntry.IsNull ? null : info.FirstEntry.Id.ToString(),
            info.LastEntry.IsNull ? null : info.LastEntry.Id.ToString());
    }

    public async Task<IReadOnlyList<StreamConsumerGroupSummary>> GetStreamConsumerGroupsAsync(string secret, int database, string key, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var groups = await db.StreamGroupInfoAsync(key).WaitAsync(ct);

        return groups
            .Select(g => new StreamConsumerGroupSummary(g.Name, g.ConsumerCount, g.PendingMessageCount, g.LastDeliveredId, g.Lag))
            .ToList();
    }

    public async Task<IReadOnlyList<StreamConsumer>> GetStreamConsumersAsync(string secret, int database, string key, string group, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var consumers = await db.StreamConsumerInfoAsync(key, group).WaitAsync(ct);

        return consumers
            .Select(c => new StreamConsumer(c.Name, c.PendingMessageCount, TimeSpan.FromMilliseconds(c.IdleTimeInMilliseconds)))
            .ToList();
    }

    public async Task<IReadOnlyList<StreamPendingEntry>> GetStreamPendingEntriesAsync(string secret, int database, string key, string group, int count, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        // Extended form (XPENDING key group - + count), not just the summary -- the pending-entries
        // table needs each entry's id/consumer/idle/delivery-count, not just the aggregate counts.
        var entries = await db.StreamPendingMessagesAsync(key, group, count, consumerName: RedisValue.Null).WaitAsync(ct);

        return entries
            .Select(e => new StreamPendingEntry(e.MessageId.ToString(), e.ConsumerName.ToString(), TimeSpan.FromMilliseconds(e.IdleTimeInMilliseconds), e.DeliveryCount))
            .ToList();
    }

    public async Task<IReadOnlyList<StreamEntry>> GetStreamEntriesAsync(string secret, int database, string key, string? beforeId, int count, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        // Descending order (XREVRANGE semantics) so the "recent entries" view reads newest-first;
        // beforeId, when given, becomes the (inclusive, per StreamRangeAsync's own contract) upper
        // bound, so paging further back in history starts from just before the caller's last-seen
        // id -- see the exclusive-filter step below.
        var entries = beforeId is null
            ? await db.StreamRangeAsync(key, count: count, messageOrder: Order.Descending).WaitAsync(ct)
            : await db.StreamRangeAsync(key, maxId: beforeId, count: count, messageOrder: Order.Descending).WaitAsync(ct);

        // beforeId is an exclusive bound in this method's contract, but StreamRangeAsync's maxId is
        // inclusive -- so when a beforeId was supplied, drop it from the result if the server
        // happened to still have that exact id (it always will, since it's the caller's own
        // last-seen id from the previous page).
        var filtered = beforeId is null ? entries : entries.Where(e => e.Id != beforeId).ToArray();
        return filtered.Select(RedisStreamEntryMapper.ToStreamEntry).ToList();
    }

    public async Task<StreamEntry?> GetStreamEntryAsync(string secret, int database, string key, string entryId, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var entries = await db.StreamRangeAsync(key, minId: entryId, maxId: entryId, count: 1).WaitAsync(ct);
        return entries.Length == 0 ? null : RedisStreamEntryMapper.ToStreamEntry(entries[0]);
    }

    public async Task<string> AddStreamEntryAsync(string secret, int database, string key, IReadOnlyList<StreamFieldValue> fields, long? maxLen, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var pairs = fields.Select(f => new NameValueEntry(f.Field, f.Value)).ToArray();
        var id = await db.StreamAddAsync(key, pairs, maxLength: maxLen, useApproximateMaxLength: maxLen is not null).WaitAsync(ct);
        return id.ToString();
    }

    public async Task<long> TrimStreamAsync(string secret, int database, string key, long maxLen, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.StreamTrimAsync(key, maxLen).WaitAsync(ct);
    }

    public async Task<long> AcknowledgeStreamEntriesAsync(string secret, int database, string key, string group, IReadOnlyList<string> entryIds, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var ids = entryIds.Select(id => (RedisValue)id).ToArray();
        return await db.StreamAcknowledgeAsync(key, group, ids).WaitAsync(ct);
    }

    public async Task<IReadOnlyList<StreamEntry>> ClaimStreamEntriesAsync(string secret, int database, string key, string group, string consumer, long minIdleTimeMs, IReadOnlyList<string> entryIds, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var ids = entryIds.Select(id => (RedisValue)id).ToArray();
        var claimed = await db.StreamClaimAsync(key, group, consumer, minIdleTimeMs, ids).WaitAsync(ct);
        return claimed.Select(RedisStreamEntryMapper.ToStreamEntry).ToList();
    }

    public async Task<long> DeleteStreamEntriesAsync(string secret, int database, string key, IReadOnlyList<string> entryIds, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        var ids = entryIds.Select(id => (RedisValue)id).ToArray();
        return await db.StreamDeleteAsync(key, ids).WaitAsync(ct);
    }

    public async Task<long> DeleteStreamConsumerAsync(string secret, int database, string key, string group, string consumer, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase(database);
        return await db.StreamDeleteConsumerAsync(key, group, consumer).WaitAsync(ct);
    }

    // ---- Pub/Sub (phase 3) ----

    public async Task<IReadOnlyList<PubSubChannelSummary>> GetPubSubChannelsAsync(string secret, string? pattern, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var db = muxer.GetDatabase();

        var channelsReply = pattern is { Length: > 0 }
            ? await db.ExecuteAsync("PUBSUB", "CHANNELS", pattern).WaitAsync(ct)
            : await db.ExecuteAsync("PUBSUB", "CHANNELS").WaitAsync(ct);
        var channels = ((RedisResult[]?)channelsReply ?? []).Select(r => (string)r!).ToList();
        if (channels.Count == 0)
        {
            return [];
        }

        // PUBSUB NUMSUB ch1 ch2 ... replies with a flat [channel, count, channel, count, ...] array.
        var numSubArgs = new object[channels.Count + 1];
        numSubArgs[0] = "NUMSUB";
        for (var i = 0; i < channels.Count; i++)
        {
            numSubArgs[i + 1] = channels[i];
        }

        var numSubReply = await db.ExecuteAsync("PUBSUB", numSubArgs).WaitAsync(ct);
        var flat = (RedisResult[])numSubReply!;

        var result = new List<PubSubChannelSummary>(channels.Count);
        for (var i = 0; i + 1 < flat.Length; i += 2)
        {
            result.Add(new PubSubChannelSummary((string)flat[i]!, (long)flat[i + 1]));
        }

        return result;
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string secret, string channelOrPattern, bool isPattern, Func<PubSubMessage, Task> onMessage, CancellationToken ct = default)
    {
        // Deliberately NOT `await using` -- a live tail needs the multiplexer to stay connected for
        // as long as the caller is watching. Ownership passes to the returned RedisPubSubSubscription,
        // which is the only thing that disposes it (design spec §14).
        var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var subscriber = muxer.GetSubscriber();
        var channel = isPattern ? RedisChannel.Pattern(channelOrPattern) : RedisChannel.Literal(channelOrPattern);

        // async void is correct here: ISubscriber.SubscribeAsync's handler parameter is a plain
        // Action<RedisChannel, RedisValue>, invoked by StackExchange.Redis on its own completion
        // thread per message, with no Task for us to observe. The try/catch is load-bearing: the
        // caller (a Blazor component) may already be disposed by the time a message arrives, and an
        // unhandled exception here must not crash that background thread.
        async void Handler(RedisChannel ch, RedisValue value)
        {
            try
            {
                await onMessage(new PubSubMessage(ch.ToString(), value.ToString(), DateTimeOffset.UtcNow));
            }
            catch
            {
                // Best-effort delivery -- see design spec §14's subscription-lifecycle note.
            }
        }

        await subscriber.SubscribeAsync(channel, Handler).WaitAsync(ct);
        return new RedisPubSubSubscription(muxer, subscriber, channel, Handler);
    }

    public async Task<long> PublishAsync(string secret, string channel, string payload, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var subscriber = muxer.GetSubscriber();
        return await subscriber.PublishAsync(RedisChannel.Literal(channel), payload).WaitAsync(ct);
    }

    // ---- FLUSHDB (deferred from phase 1) ----

    public async Task FlushDatabaseAsync(string secret, int database, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);
        await server.FlushDatabaseAsync(database).WaitAsync(ct);
    }

    // ---- ACL-aware disabled-button state ----

    // Only the two phase-3 call sites (design spec §12/§14) -- not the full Redis ACL category
    // table. See RedisAclPermissionEvaluator's doc comment for why this is scoped this narrowly.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AclCategoriesByCommand =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FLUSHDB"] = ["keyspace", "write", "slow", "dangerous"],
            ["PUBLISH"] = ["pubsub", "fast"],
        };

    public async Task<bool> HasCommandPermissionAsync(string secret, string command, CancellationToken ct = default)
    {
        try
        {
            await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
            var db = muxer.GetDatabase();

            var whoAmI = await db.ExecuteAsync("ACL", "WHOAMI").WaitAsync(ct);
            var username = (string?)whoAmI;
            if (string.IsNullOrEmpty(username))
            {
                return true;
            }

            var userInfo = await db.ExecuteAsync("ACL", "GETUSER", username).WaitAsync(ct);
            var commandsField = RedisAclPermissionEvaluator.ExtractCommandsField(userInfo);
            var categories = AclCategoriesByCommand.TryGetValue(command, out var known) ? known : [];
            return RedisAclPermissionEvaluator.IsAllowed(commandsField, command, categories);
        }
        catch (Exception)
        {
            // ACL WHOAMI/GETUSER can themselves be refused by a restrictive ACL, or fail for any
            // other reason (network blip, a server too old to support ACL) -- this hook only drives
            // an advisory disabled-button state (design spec §14), never the real authorization
            // boundary, so any failure here fails open rather than surfacing an error nobody asked
            // to see. A command that's actually denied still fails loudly and correctly via NOPERM
            // (FriendlyRedisError) if the user attempts it anyway.
            return true;
        }
    }
}
