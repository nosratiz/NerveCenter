namespace SbConsole.Plugins.Redis.Client;

public sealed record ScanKeysRequest(int Database, string? Pattern, RedisKeyType? TypeFilter, long Cursor, int Count);

public sealed record SetTtlRequest(int Database, string Key, TimeSpan? ExpireIn, DateTimeOffset? ExpireAt, bool Persist);

public sealed record DryRunDeleteByPatternResult(long WouldDeleteKeyCount, long WouldDeleteBytes, bool Complete);
