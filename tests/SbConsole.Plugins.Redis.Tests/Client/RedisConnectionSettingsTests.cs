using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisConnectionSettingsTests
{
    [Fact]
    public void Round_trips_a_standalone_connection_through_the_secret_string()
    {
        var original = new RedisConnectionSettings(
            Topology: RedisTopology.Standalone,
            EndpointHostPorts: ["redis-01.uk.internal:6380"],
            User: "sbconsole",
            Password: "hunter2",
            Ssl: true,
            ServiceName: null,
            PreferReplicaForReads: true,
            DefaultDatabase: 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Round_trips_a_sentinel_connection_with_multiple_endpoints_and_service_name()
    {
        var original = new RedisConnectionSettings(
            Topology: RedisTopology.Sentinel,
            EndpointHostPorts: ["sentinel-1:26379", "sentinel-2:26379", "sentinel-3:26379"],
            User: null,
            Password: "hunter2",
            Ssl: false,
            ServiceName: "uk-prod",
            PreferReplicaForReads: false,
            DefaultDatabase: 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Password_containing_a_comma_survives_the_round_trip()
    {
        var original = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, "p@ss,word=weird", false, null, false, 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Password.Should().Be("p@ss,word=weird");
    }

    [Fact]
    public void Parse_reports_the_host_and_port_it_was_given_verbatim()
    {
        var settings = RedisConnectionSettings.Parse(
            new RedisConnectionSettings(RedisTopology.Standalone, ["redis-01:6380"], "u", "p", true, null, false, 3)
                .ToSecretString());

        settings.EndpointHostPorts.Should().Equal("redis-01:6380");
        settings.DefaultDatabase.Should().Be(3);
    }
}
