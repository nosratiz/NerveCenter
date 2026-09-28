using FluentAssertions;
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

#pragma warning disable CS0618 // Type or member is obsolete

namespace SbConsole.Plugins.Redis.Tests.Client;

public class FriendlyRedisErrorTests
{
    [Fact]
    public void Maps_connection_exception_to_a_short_reachability_message()
    {
        var ex = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom, lots of internal detail here", null);

        FriendlyRedisError.From(ex).Should().Be("Could not reach Redis: UnableToConnect");
    }

    [Fact]
    public void Maps_timeout_exception_to_a_fixed_message()
    {
        var ex = new RedisTimeoutException("timeout detail", CommandStatus.Unknown);

        FriendlyRedisError.From(ex).Should().Be("Redis command timed out");
    }

    [Theory]
    [InlineData("NOAUTH Authentication required.", "Authentication required or failed (NOAUTH)")]
    [InlineData("WRONGTYPE Operation against a key holding the wrong kind of value", "Wrong type for this key — it may have changed since the page loaded")]
    [InlineData("CLUSTERDOWN Hash slot not served", "Cluster is down")]
    [InlineData("NOGROUP No such key 'orders:events' or consumer group 'workers'", "Stream or consumer group not found — it may have been deleted since the page loaded")]
    public void Maps_known_server_error_prefixes(string serverMessage, string friendlyMessage)
    {
        var ex = new RedisServerException(serverMessage);

        FriendlyRedisError.From(ex).Should().Be(friendlyMessage);
    }

    [Fact]
    public void Passes_a_NOPERM_message_through_verbatim_because_it_already_names_the_missing_permission()
    {
        var ex = new RedisServerException("NOPERM User sbconsole has no permissions to run the 'xclaim' command");

        FriendlyRedisError.From(ex).Should().Be("Command refused by ACL (NOPERM): User sbconsole has no permissions to run the 'xclaim' command");
    }

    [Fact]
    public void Falls_back_to_the_sdk_helper_for_anything_unmapped()
    {
        var ex = new InvalidOperationException("some other failure");

        FriendlyRedisError.From(ex).Should().Be(SbConsole.Sdk.FriendlyError.From(ex));
    }
}

#pragma warning restore CS0618
