using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests;

public class RedisPluginTests
{
    [Fact]
    public void Id_and_connection_kind_are_redis()
    {
        var plugin = new RedisPlugin();

        plugin.Id.Should().Be("redis");
        plugin.ConnectionKind.Should().Be("redis");
    }

    [Fact]
    public void Nav_items_are_overview_keys_streams_then_pubsub()
    {
        var plugin = new RedisPlugin();

        plugin.NavItems.Select(n => n.Href).Should().Equal("/p/redis/overview", "/p/redis/keys", "/p/redis/streams", "/p/redis/pubsub");
    }

    [Fact]
    public void Contribution_reports_the_keys_browsers_audited_actions()
    {
        new RedisPlugin().Contribution.ActionCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Connection_form_component_is_the_redis_connection_fields_component()
    {
        new RedisPlugin().ConnectionFormComponentType.Should().Be(typeof(RedisConnectionFields));
    }

    [Fact]
    public void Connection_summary_reports_the_first_endpoint_and_topology()
    {
        var plugin = new RedisPlugin();
        var secret = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var summary = plugin.GetConnectionSummary(secret);

        summary["Host"].Should().Be("redis-01:6380");
        summary["Topology"].Should().Be("Standalone");
    }

    [Fact]
    public void Connection_summary_is_empty_for_an_unparsable_secret_instead_of_throwing()
    {
        new RedisPlugin().GetConnectionSummary("not a valid secret").Should().BeEmpty();
    }

    [Fact]
    public void Connection_summary_is_empty_for_a_well_formed_but_invalid_topology_token()
    {
        // Three '|'-delimited parts (so it does NOT hit the FormatException branch), but
        // "bogus" isn't a RedisTopology member — Enum.Parse throws ArgumentException, which
        // GetConnectionSummary must also swallow, not just FormatException.
        new RedisPlugin().GetConnectionSummary("bogus|0|host:6379").Should().BeEmpty();
    }

    [Fact]
    public void Connection_summary_is_empty_for_a_configuration_options_keyword_it_rejects()
    {
        // ConfigurationOptions.Parse throws ArgumentException on an unrecognized keyword —
        // a different source of the same exception type as the case above, still must not throw.
        new RedisPlugin().GetConnectionSummary("standalone|0|host:6379,notarealkeyword=1").Should().BeEmpty();
    }
}
