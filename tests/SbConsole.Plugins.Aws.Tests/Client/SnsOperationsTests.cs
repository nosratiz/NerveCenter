using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SnsOperationsTests
{
    [Fact]
    public void TopicNameFromArn_returns_the_last_segment()
    {
        SnsOperations.TopicNameFromArn("arn:aws:sns:eu-west-1:123456789012:order-events-topic")
            .Should().Be("order-events-topic");
    }

    [Fact]
    public async Task ListTopicsAsync_against_an_unreachable_endpoint_throws_a_friendly_exception()
    {
        var ops = new SnsOperations();

        var act = () => ops.ListTopicsAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        // Real network calls can't be unit-tested without a broker/emulator (same "light coverage
        // by necessity" limitation the SQS plan's SqsOperations accepted) -- this only pins that a
        // connection failure surfaces as SOME exception, not a hang or a silently-empty list.
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void ClassifySubscription_flags_the_literal_PendingConfirmation_arn_as_pending()
    {
        var summary = SnsOperations.ClassifySubscription("PendingConfirmation", "https", "https://ops.example.com", new Dictionary<string, string>());

        summary.IsPending.Should().BeTrue();
        summary.SubscriptionArn.Should().Be("PendingConfirmation");
    }

    [Fact]
    public void ClassifySubscription_reads_raw_delivery_and_filter_policy_from_attributes()
    {
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true", ["FilterPolicy"] = """{"region":["uk"]}""" };

        var summary = SnsOperations.ClassifySubscription("arn:aws:sns:us-east-1:1:topic:sub-id", "sqs", "shipment-updates", attributes);

        summary.IsPending.Should().BeFalse();
        summary.RawMessageDelivery.Should().BeTrue();
        summary.FilterPolicyJson.Should().Be("""{"region":["uk"]}""");
    }

    [Fact]
    public void ClassifySubscription_defaults_raw_delivery_and_filter_policy_to_null_when_absent()
    {
        var summary = SnsOperations.ClassifySubscription("arn:aws:sns:us-east-1:1:topic:sub-id", "email", "ops@example.com", new Dictionary<string, string>());

        summary.RawMessageDelivery.Should().BeNull();
        summary.FilterPolicyJson.Should().BeNull();
    }
}
