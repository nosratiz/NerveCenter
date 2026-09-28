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
            var unixSeconds = (long)fields[1];
            var microseconds = (long)fields[2];
            var args = (RedisResult[])fields[3]!;
            var command = string.Join(' ', args.Select(a => (string)a!));
            var clientAddress = (string?)fields[4];

            result.Add(new SlowLogEntry(
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
                TimeSpan.FromMicroseconds(microseconds),
                command,
                string.IsNullOrEmpty(clientAddress) ? null : clientAddress));
        }

        return result;
    }
}
