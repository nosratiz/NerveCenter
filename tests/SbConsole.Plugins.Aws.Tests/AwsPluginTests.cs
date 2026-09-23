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
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 13));
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
    public async Task GetNavBadgeAsync_returns_null_for_an_unrelated_nav_href()
    {
        var plugin = new AwsPlugin();

        var badge = await plugin.GetNavBadgeAsync("/p/aws/queues", "mode=default-chain;region=us-east-1");

        badge.Should().BeNull();
    }
}
