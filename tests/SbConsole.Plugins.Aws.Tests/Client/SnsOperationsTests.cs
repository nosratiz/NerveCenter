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

    [Fact]
    public void EvaluateFilterMatch_returns_true_when_no_filter_policy_is_set()
    {
        SnsOperations.EvaluateFilterMatch(null, new Dictionary<string, string> { ["region"] = "uk" }).Should().BeTrue();
    }

    [Fact]
    public void EvaluateFilterMatch_matches_a_simple_value_list_policy()
    {
        var policy = """{"region":["uk","eu"]}""";

        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["region"] = "uk" }).Should().BeTrue();
        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["region"] = "us" }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_fails_when_the_message_has_no_matching_attribute()
    {
        var policy = """{"region":["uk"]}""";

        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["eventType"] = "shipment.dispatched" }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_requires_every_key_in_the_policy_to_match()
    {
        var policy = """{"region":["uk"],"eventType":["shipment.dispatched"]}""";
        var attributes = new Dictionary<string, string> { ["region"] = "uk", ["eventType"] = "shipment.delivered" };

        SnsOperations.EvaluateFilterMatch(policy, attributes).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_treats_malformed_policy_json_as_no_match()
    {
        SnsOperations.EvaluateFilterMatch("not json", new Dictionary<string, string>()).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_treats_a_non_object_top_level_policy_as_no_match()
    {
        SnsOperations.EvaluateFilterMatch("5", new Dictionary<string, string> { ["region"] = "uk" }).Should().BeFalse();
        SnsOperations.EvaluateFilterMatch("[]", new Dictionary<string, string> { ["region"] = "uk" }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_treats_a_non_array_property_value_as_no_match()
    {
        SnsOperations.EvaluateFilterMatch("""{"region":"uk"}""", new Dictionary<string, string> { ["region"] = "uk" }).Should().BeFalse();
    }
}
