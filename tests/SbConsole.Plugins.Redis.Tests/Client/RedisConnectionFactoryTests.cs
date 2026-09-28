using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisConnectionFactoryTests
{
    [Fact]
    public async Task Sentinel_without_a_service_name_is_rejected_before_attempting_to_connect()
    {
        // Per RedisConnectionFactory's own comment, setting ServiceName is what puts
        // StackExchange.Redis into Sentinel mode -- a Sentinel-topology secret with no service
        // name would otherwise connect in plain standalone mode to the sentinel node itself
        // (wrong: INFO would report the sentinel, not the primary). This must be rejected clearly
        // and synchronously, before any network attempt, rather than connecting to the wrong node.
        var secret = new RedisConnectionSettings(
            RedisTopology.Sentinel, ["sentinel-1:26379"], null, null, false, ServiceName: null, false, 0)
            .ToSecretString();

        var act = async () => await RedisConnectionFactory.ConnectAsync(secret, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*Sentinel*service name*");
    }

    [Fact]
    public async Task Sentinel_with_an_empty_service_name_is_also_rejected()
    {
        var secret = new RedisConnectionSettings(
            RedisTopology.Sentinel, ["sentinel-1:26379"], null, null, false, ServiceName: "", false, 0)
            .ToSecretString();

        var act = async () => await RedisConnectionFactory.ConnectAsync(secret, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
