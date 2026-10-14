using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>Maps between our own <see cref="RedisKeyType"/> and StackExchange.Redis's RedisType / SCAN's "TYPE" token.</summary>
public static class RedisKeyTypeNames
{
    public static RedisKeyType FromRedisType(RedisType type) => type switch
    {
        RedisType.String => RedisKeyType.String,
        RedisType.List => RedisKeyType.List,
        RedisType.Set => RedisKeyType.Set,
        RedisType.SortedSet => RedisKeyType.SortedSet,
        RedisType.Hash => RedisKeyType.Hash,
        RedisType.Stream => RedisKeyType.Stream,
        _ => RedisKeyType.Unknown,
    };

    /// <summary>The lowercase token SCAN's "TYPE" filter expects ("zset" for sorted sets) — null for Unknown, which is not a real Redis type.</summary>
    public static string? ToScanToken(RedisKeyType type) => type switch
    {
        RedisKeyType.String => "string",
        RedisKeyType.List => "list",
        RedisKeyType.Set => "set",
        RedisKeyType.SortedSet => "zset",
        RedisKeyType.Hash => "hash",
        RedisKeyType.Stream => "stream",
        _ => null,
    };
}
