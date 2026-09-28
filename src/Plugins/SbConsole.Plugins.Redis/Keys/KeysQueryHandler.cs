using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

/// <summary>
/// The Keys browser's single read-side handler (design spec §9: "one query handler for
/// scanning/reading") -- one method per read shape (scan, per-type get, dry-run), all thin
/// HandlerRunner.QueryAsync wrappers. The per-verb split lives on the command side instead, since
/// each write needs its own audited action name.
/// </summary>
public sealed class KeysQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<KeysQueryHandler> logger)
{
    public Task<PluginResult<KeyScanPage>> ScanAsync(Guid connectionId, ScanKeysRequest request, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.key.scan",
            secret => operations.ScanKeysAsync(secret, request, ct), ct);

    public Task<PluginResult<RedisKeyType>> GetTypeAsync(Guid connectionId, int database, string key, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.key.gettype",
            secret => operations.GetKeyTypeAsync(secret, database, key, ct), ct);

    public Task<PluginResult<StringValue>> GetStringAsync(Guid connectionId, int database, string key, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.string.get",
            secret => operations.GetStringAsync(secret, database, key, ct), ct);

    public Task<PluginResult<HashFieldPage>> ScanHashFieldsAsync(
        Guid connectionId, int database, string key, string? fieldPattern, long cursor, int count, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.hash.scan",
            secret => operations.ScanHashFieldsAsync(secret, database, key, fieldPattern, cursor, count, ct), ct);

    public Task<PluginResult<ListSlice>> GetListSliceAsync(
        Guid connectionId, int database, string key, int headCount, int tailCount, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.list.get",
            secret => operations.GetListSliceAsync(secret, database, key, headCount, tailCount, ct), ct);

    public Task<PluginResult<SortedSetSlice>> GetSortedSetRangeAsync(
        Guid connectionId, int database, string key, long start, long stop, bool descending, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.zset.get",
            secret => operations.GetSortedSetRangeAsync(secret, database, key, start, stop, descending, ct), ct);

    public Task<PluginResult<SetPage>> ScanSetMembersAsync(
        Guid connectionId, int database, string key, long cursor, int count, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.set.scan",
            secret => operations.ScanSetMembersAsync(secret, database, key, cursor, count, ct), ct);

    public Task<PluginResult<DryRunDeleteByPatternResult>> DryRunDeleteByPatternAsync(
        Guid connectionId, int database, string pattern, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.pattern.dryrun",
            secret => operations.DryRunDeleteByPatternAsync(secret, database, pattern, ct), ct);
}
