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

    [Fact]
    public void The_configuration_options_portion_is_a_genuine_ConfigurationOptions_string()
    {
        // Proves RedisConnectionSettings reuses StackExchange.Redis's own parser/serializer
        // rather than a hand-rolled one: everything after the second '|' must be independently
        // parseable by ConfigurationOptions.Parse and carry the same endpoint/Ssl/ServiceName —
        // fields a hand-rolled parser could drop or corrupt without any of the other tests here
        // noticing (they only ever round-trip through this class's own Parse/ToSecretString).
        var secret = new RedisConnectionSettings(
            RedisTopology.Sentinel, ["sentinel-1:26379", "sentinel-2:26379"], "sbconsole", "hunter2",
            true, "uk-prod", false, 0).ToSecretString();

        var configPortion = secret.Split('|', 3)[2];
        var options = StackExchange.Redis.ConfigurationOptions.Parse(configPortion);

        options.EndPoints.Should().HaveCount(2);
        options.Ssl.Should().BeTrue();
        options.ServiceName.Should().Be("uk-prod");
    }

    [Fact]
    public void A_leftover_service_name_does_not_leak_into_the_options_for_a_standalone_topology()
    {
        // Simulates a user who selected Sentinel, typed a service name, then switched the
        // topology control back to Standalone -- the record can still carry the leftover
        // ServiceName value, but it must never reach the built ConfigurationOptions, since setting
        // ServiceName on ConfigurationOptions puts StackExchange.Redis into Sentinel mode
        // automatically regardless of what this plugin's own Topology enum says.
        var settings = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, null, false, "uk-prod", false, 0);

        settings.ToConfigurationOptions().ServiceName.Should().BeNull();
    }

    [Fact]
    public void A_leftover_service_name_does_not_leak_into_the_secret_string_for_a_standalone_topology()
    {
        var settings = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, null, false, "uk-prod", false, 0);

        var configPortion = settings.ToSecretString().Split('|', 3)[2];

        StackExchange.Redis.ConfigurationOptions.Parse(configPortion).ServiceName.Should().BeNull();
    }
}
