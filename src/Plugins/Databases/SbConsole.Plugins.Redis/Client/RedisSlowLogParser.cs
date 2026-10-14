using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public static class RedisSlowLogParser
{
    public static IReadOnlyList<SlowLogEntry> Parse(RedisResult reply)
    {
        var entries = (RedisResult[]?)reply ?? [];
        var result = new List<SlowLogEntry>(entries.Length);

        foreach (var entry in entries)
        {
            var fields = (RedisResult[])entry!;
            if (fields.Length < 4)
            {
                // Pre-4.0 Redis servers return a 4-field SLOWLOG entry (id, timestamp, duration,
                // command) with no client address/name -- skip rather than index out of bounds.
                continue;
            }

            var unixSeconds = (long)fields[1];
            var microseconds = (long)fields[2];
            var args = (RedisResult[])fields[3]!;
            var command = string.Join(' ', args.Select(a => (string)a!));
            var clientAddress = fields.Length > 4 ? (string?)fields[4] : null;

            result.Add(new SlowLogEntry(
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
                TimeSpan.FromMicroseconds(microseconds),
                command,
                string.IsNullOrEmpty(clientAddress) ? null : clientAddress));
        }

        return result;
    }
}
