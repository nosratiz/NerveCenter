using System.Globalization;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Parses the raw two-element multi-bulk reply every *SCAN command (SCAN/HSCAN/SSCAN/ZSCAN)
/// returns: [nextCursor, items[]]. Deliberately agnostic to how items pair up (plain keys for
/// SCAN/SSCAN, field/value or member/score pairs for HSCAN/ZSCAN) -- callers interpret pairs
/// themselves, matching the design spec §5's "parse the reply itself rather than use the
/// auto-paging enumerable" decision (the raw cursor value is needed for the progress bar).
/// </summary>
public static class RedisScanReplyParser
{
    public static (long Cursor, IReadOnlyList<string> Items) Parse(RedisResult reply)
    {
        var top = (RedisResult[])reply!;
        var cursor = long.Parse((string)top[0]!, CultureInfo.InvariantCulture);
        var itemsRaw = (RedisResult[])top[1]!;
        var items = itemsRaw.Select(r => (string)r!).ToList();
        return (cursor, items);
    }
}
