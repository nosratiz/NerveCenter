using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public static class FriendlyRedisError
{
    public static string From(Exception ex) => ex switch
    {
        RedisConnectionException conn => $"Could not reach Redis: {conn.FailureType}",
        RedisTimeoutException => "Redis command timed out",
        RedisServerException server when server.Message.StartsWith("NOAUTH", StringComparison.Ordinal) =>
            "Authentication required or failed (NOAUTH)",
        RedisServerException server when server.Message.StartsWith("NOPERM", StringComparison.Ordinal) =>
            $"Command refused by ACL (NOPERM): {server.Message["NOPERM".Length..].Trim()}",
        RedisServerException server when server.Message.StartsWith("WRONGTYPE", StringComparison.Ordinal) =>
            "Wrong type for this key — it may have changed since the page loaded",
        RedisServerException server when server.Message.StartsWith("CLUSTERDOWN", StringComparison.Ordinal) =>
            "Cluster is down",
        // Streams-specific (design spec §10 addendum): XACK/XCLAIM/XPENDING/XGROUP all throw this
        // when the stream key or the named consumer group no longer exists -- most often because
        // another operator deleted the group (or the whole stream) between the page loading its
        // consumer-groups table and the row action being clicked.
        RedisServerException server when server.Message.StartsWith("NOGROUP", StringComparison.Ordinal) =>
            "Stream or consumer group not found — it may have been deleted since the page loaded",
        _ => SbConsole.Sdk.FriendlyError.From(ex),
    };
}
