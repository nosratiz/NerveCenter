using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Owns one live PUBSUB/PSUBSCRIBE subscription's resources (design spec §14). Unlike every other
/// IRedisOperations call, which opens a ConnectionMultiplexer for the duration of a single request
/// (`await using`, disposed before the method returns), a live tail needs its multiplexer to stay
/// connected for as long as the UI is watching -- this type is what eventually closes it.
///
/// DisposeAsync unsubscribes first (so no further messages are dispatched to a caller that's about
/// to go away) and only then disposes the multiplexer; it is idempotent and safe to call more than
/// once -- the Blazor page calls it from both an explicit "Unsubscribe" click and its own
/// DisposeAsync, whichever happens first, and must not leak the connection if neither ever fires
/// cleanly (e.g. a page crash), which is why ownership is expressed as IAsyncDisposable rather than
/// a bare "unsubscribe" method the caller could forget to pair with a dispose.
/// </summary>
internal sealed class RedisPubSubSubscription(
    IConnectionMultiplexer multiplexer, ISubscriber subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler)
    : IAsyncDisposable
{
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await subscriber.UnsubscribeAsync(channel, handler);
        }
        catch
        {
            // Best-effort -- the connection may already be gone (server restart, network drop).
            // The multiplexer dispose below must still run either way.
        }

        await multiplexer.DisposeAsync();
    }
}
