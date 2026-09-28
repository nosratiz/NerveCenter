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
}
