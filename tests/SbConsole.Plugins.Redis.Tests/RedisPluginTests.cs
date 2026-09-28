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
    public void Nav_items_start_with_overview_only()
    {
        var plugin = new RedisPlugin();

        plugin.NavItems.Select(n => n.Href).Should().Equal("/p/redis/overview");
    }

    [Fact]
    public void Contribution_reports_no_actions_yet()
    {
        new RedisPlugin().Contribution.ActionCount.Should().Be(0);
    }

    [Fact]
    public void Connection_form_component_falls_back_to_the_host_default_for_now()
    {
        new RedisPlugin().ConnectionFormComponentType.Should().BeNull();
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
}
