using FluentAssertions;
using SbConsole.Plugins.Aws;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests;

public class AwsPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new AwsPlugin();

        plugin.Id.Should().Be("aws");
        plugin.ConnectionKind.Should().Be("aws");
        plugin.DisplayName.Should().Be("AWS SQS/SNS");
        plugin.ConnectionKindDisplayName.Should().Be("AWS SQS/SNS");
        plugin.NavItems.Should().Contain(n => n.Title == "Queues" && n.Href == "/p/aws/queues");
        plugin.NavItems.Should().Contain(n => n.Title == "Topics" && n.Href == "/p/aws/topics");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 5, ActionCount: 16));
    }

    [Fact]
    public async Task TestConnectionAsync_against_an_unreachable_address_fails_cleanly_and_never_throws()
    {
        var plugin = new AwsPlugin();

        var result = await plugin.TestConnectionAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TestConnectionAsync_leaves_identity_and_checks_null_when_credentials_are_rejected()
    {
        var plugin = new AwsPlugin();

        var result = await plugin.TestConnectionAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        result.Identity.Should().BeNull();
        result.Checks.Should().BeNull();
    }

    [Fact]
    public async Task GetNavBadgeAsync_returns_null_for_an_unrelated_nav_href()
    {
        var plugin = new AwsPlugin();

        // "/p/aws/topics", not "/p/aws/queues" -- the Queues href now reports a real DLQ badge (which
        // would call SQS), so only an href with no badge is safe to exercise without AWS.
        var badge = await plugin.GetNavBadgeAsync("/p/aws/topics", "mode=default-chain;region=us-east-1");

        badge.Should().BeNull();
    }

    [Fact]
    public void Declares_AwsConnectionFields_as_its_connection_form_component()
    {
        var plugin = new AwsPlugin();

        plugin.ConnectionFormComponentType.Should().Be(typeof(SbConsole.Plugins.Aws.Client.AwsConnectionFields));
    }

    [Fact]
    public void GetConnectionSummary_echoes_only_the_region()
    {
        var plugin = new AwsPlugin();

        var summary = plugin.GetConnectionSummary("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=shh");

        summary.Should().ContainSingle(kv => kv.Key == "Region" && kv.Value == "eu-west-1");
        summary.Values.Should().NotContain(v => v.Contains("AKIA123") || v.Contains("shh"));
    }

    [Fact]
    public void GetConnectionSummary_falls_back_to_a_placeholder_when_no_region_is_set()
    {
        var plugin = new AwsPlugin();

        var summary = plugin.GetConnectionSummary("mode=default-chain");

        summary["Region"].Should().Be("?");
    }
}
