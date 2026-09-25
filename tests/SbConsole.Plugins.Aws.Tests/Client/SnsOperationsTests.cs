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
    public void ClassifySubscription_reads_the_filter_policy_scope_when_present()
    {
        var attributes = new Dictionary<string, string> { ["FilterPolicy"] = """{"region":["uk"]}""", ["FilterPolicyScope"] = "MessageBody" };

        var summary = SnsOperations.ClassifySubscription("arn:aws:sns:us-east-1:1:topic:sub-id", "sqs", "shipment-updates", attributes);

        summary.FilterPolicyScope.Should().Be("MessageBody");
    }

    [Fact]
    public void BuildSubscribeAttributes_omits_the_filter_policy_when_none_is_given()
    {
        var attributes = SnsOperations.BuildSubscribeAttributes(new SubscribeRequest("arn:topic", "sqs", "arn:queue", true));

        attributes.Should().Equal(new Dictionary<string, string> { ["RawMessageDelivery"] = "true" });
    }

    [Fact]
    public void BuildSubscribeAttributes_sends_the_filter_policy_and_scope_when_given()
    {
        var request = new SubscribeRequest("arn:topic", "sqs", "arn:queue", false, """{"region":["uk"]}""", "MessageBody");

        var attributes = SnsOperations.BuildSubscribeAttributes(request);

        attributes.Should().Contain("FilterPolicy", """{"region":["uk"]}""");
        attributes.Should().Contain("FilterPolicyScope", "MessageBody");
    }

    [Fact]
    public void BuildSubscribeAttributes_defaults_the_scope_to_MessageAttributes_when_a_policy_has_none()
    {
        var request = new SubscribeRequest("arn:topic", "sqs", "arn:queue", false, """{"region":["uk"]}""");

        SnsOperations.BuildSubscribeAttributes(request).Should().Contain("FilterPolicyScope", "MessageAttributes");
    }

    [Fact]
    public void PlanFilterPolicyUpdates_clears_with_an_empty_FilterPolicy_and_never_touches_the_scope()
    {
        SnsOperations.PlanFilterPolicyUpdates("""{"a":["b"]}""", "MessageBody", null, "MessageAttributes")
            .Should().Equal([("FilterPolicy", "")]);
    }

    [Fact]
    public void PlanFilterPolicyUpdates_sets_only_the_policy_when_the_scope_is_unchanged()
    {
        SnsOperations.PlanFilterPolicyUpdates("""{"a":["b"]}""", null, """{"a":["c"]}""", "MessageAttributes")
            .Should().Equal([("FilterPolicy", """{"a":["c"]}""")]);
    }

    [Fact]
    public void PlanFilterPolicyUpdates_switches_to_MessageBody_scope_first_when_a_policy_already_exists()
    {
        // The new (possibly nested, body-only) policy must be validated under the new scope.
        SnsOperations.PlanFilterPolicyUpdates("""{"a":["b"]}""", "MessageAttributes", """{"a":{"b":["c"]}}""", "MessageBody")
            .Should().Equal([("FilterPolicyScope", "MessageBody"), ("FilterPolicy", """{"a":{"b":["c"]}}""")]);
    }

    [Fact]
    public void PlanFilterPolicyUpdates_sets_the_MessageBody_scope_first_even_when_no_policy_exists_yet()
    {
        // Policy-first would validate a nested (body-only) policy under the default
        // MessageAttributes scope, which SNS rejects ("Filter policy scope MessageAttributes does
        // not support nested filter policy") -- confirmed on LocalStack.
        SnsOperations.PlanFilterPolicyUpdates(null, null, """{"a":{"b":["c"]}}""", "MessageBody")
            .Should().Equal([("FilterPolicyScope", "MessageBody"), ("FilterPolicy", """{"a":{"b":["c"]}}""")]);
    }

    [Fact]
    public void PlanFilterPolicyUpdates_sets_the_MessageBody_scope_first_after_a_policy_was_cleared()
    {
        // A clear leaves FilterPolicy empty but the old scope in place.
        SnsOperations.PlanFilterPolicyUpdates("", "MessageAttributes", """{"a":{"b":["c"]}}""", "MessageBody")
            .Should().Equal([("FilterPolicyScope", "MessageBody"), ("FilterPolicy", """{"a":{"b":["c"]}}""")]);
    }

    [Fact]
    public void PlanFilterPolicyUpdates_switches_to_MessageAttributes_policy_first()
    {
        // The new policy must be flat (attribute-scope), which is also valid under the old body
        // scope -- whereas the old body policy may be nested and invalid under attribute scope.
        SnsOperations.PlanFilterPolicyUpdates("""{"a":{"b":["c"]}}""", "MessageBody", """{"a":["b"]}""", "MessageAttributes")
            .Should().Equal([("FilterPolicy", """{"a":["b"]}"""), ("FilterPolicyScope", "MessageAttributes")]);
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
    [Fact]
    public void FilterSubscriptionsByEndpoint_keeps_only_exact_endpoint_matches_and_carries_the_topic_arn()
    {
        var subscriptions = new List<Amazon.SimpleNotificationService.Model.Subscription>
        {
            new() { SubscriptionArn = "arn:sub-1", Protocol = "sqs", Endpoint = "arn:aws:sqs:eu-west-1:1:orders", TopicArn = "arn:aws:sns:eu-west-1:1:order-events" },
            new() { SubscriptionArn = "arn:sub-2", Protocol = "sqs", Endpoint = "arn:aws:sqs:eu-west-1:1:orders-dlq", TopicArn = "arn:aws:sns:eu-west-1:1:order-events" },
            new() { SubscriptionArn = "PendingConfirmation", Protocol = "sqs", Endpoint = "arn:aws:sqs:eu-west-1:1:orders", TopicArn = "arn:aws:sns:eu-west-1:1:billing" },
        };

        var matches = SnsOperations.FilterSubscriptionsByEndpoint(subscriptions, "arn:aws:sqs:eu-west-1:1:orders");

        matches.Should().HaveCount(2);
        matches.Select(m => m.TopicArn).Should().Equal("arn:aws:sns:eu-west-1:1:order-events", "arn:aws:sns:eu-west-1:1:billing");
        matches[0].TopicName.Should().Be("order-events");
        matches[1].IsPending.Should().BeTrue();
    }

    private static Func<string?, CancellationToken, Task<(IReadOnlyList<Amazon.SimpleNotificationService.Model.Subscription> Page, string? NextToken)>> Pages(
        int totalPages, List<string?> requestedTokens, string matchingEndpoint)
    {
        return (token, _) =>
        {
            requestedTokens.Add(token);
            var index = token is null ? 0 : int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyList<Amazon.SimpleNotificationService.Model.Subscription> page =
            [
                new() { SubscriptionArn = $"arn:sub-{index}", Protocol = "sqs", Endpoint = index == 0 ? matchingEndpoint : "arn:other", TopicArn = "arn:aws:sns:eu-west-1:1:t" },
                new() { SubscriptionArn = $"arn:sub-{index}b", Protocol = "sqs", Endpoint = "arn:other", TopicArn = "arn:aws:sns:eu-west-1:1:t" },
            ];
            var next = index + 1 < totalPages ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult((page, next));
        };
    }

    [Fact]
    public async Task ScanSubscriptionsForEndpoint_reads_every_page_when_under_the_cap()
    {
        var tokens = new List<string?>();

        var result = await SnsOperations.ScanSubscriptionsForEndpointAsync(Pages(3, tokens, "arn:q"), "arn:q", maxPages: 5, CancellationToken.None);

        tokens.Should().Equal(null, "1", "2");
        result.IsTruncated.Should().BeFalse();
        result.ScannedCount.Should().Be(6);
        result.Subscriptions.Should().ContainSingle(s => s.SubscriptionArn == "arn:sub-0");
    }

    [Fact]
    public async Task ScanSubscriptionsForEndpoint_stops_at_the_page_cap_and_reports_truncation()
    {
        var tokens = new List<string?>();

        var result = await SnsOperations.ScanSubscriptionsForEndpointAsync(Pages(50, tokens, "arn:q"), "arn:q", maxPages: 4, CancellationToken.None);

        tokens.Should().HaveCount(4, "the scan never reads past the cap");
        result.IsTruncated.Should().BeTrue();
        result.ScannedCount.Should().Be(8);
        result.Subscriptions.Should().ContainSingle();
    }

    [Fact]
    public void The_endpoint_scan_cap_is_20_pages()
    {
        SnsOperations.MaxEndpointScanPages.Should().Be(20);
    }

    [Fact]
    public async Task ApplyFilterPolicyUpdates_makes_every_call_in_plan_order()
    {
        var calls = new List<(string, string)>();

        await SnsOperations.ApplyFilterPolicyUpdatesAsync(
            [("FilterPolicyScope", "MessageBody"), ("FilterPolicy", "{}")],
            (name, value, _) => { calls.Add((name, value)); return Task.CompletedTask; },
            CancellationToken.None);

        calls.Should().Equal(("FilterPolicyScope", "MessageBody"), ("FilterPolicy", "{}"));
    }

    [Fact]
    public async Task ApplyFilterPolicyUpdates_rethrows_a_first_call_failure_as_is_since_nothing_changed()
    {
        var boom = new InvalidOperationException("denied");

        var act = () => SnsOperations.ApplyFilterPolicyUpdatesAsync(
            [("FilterPolicyScope", "MessageBody"), ("FilterPolicy", "{}")],
            (_, _, _) => Task.FromException(boom),
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task ApplyFilterPolicyUpdates_reports_a_scope_change_that_landed_before_the_policy_failed()
    {
        var boom = new InvalidOperationException("Invalid parameter: FilterPolicy");

        var act = () => SnsOperations.ApplyFilterPolicyUpdatesAsync(
            [("FilterPolicyScope", "MessageBody"), ("FilterPolicy", """{"a":{"b":["c"]}}""")],
            (name, _, _) => name == "FilterPolicy" ? Task.FromException(boom) : Task.CompletedTask,
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<FilterPolicyPartiallyAppliedException>()).Which;
        thrown.InnerException.Should().BeSameAs(boom);
        thrown.Message.Should().Contain("partially updated")
            .And.Contain("scope was changed to MessageBody")
            .And.Contain("previous filter policy");
    }

    [Fact]
    public async Task ApplyFilterPolicyUpdates_reports_a_scope_change_with_no_previous_policy_without_claiming_one()
    {
        var boom = new InvalidOperationException("Invalid parameter: FilterPolicy");

        var act = () => SnsOperations.ApplyFilterPolicyUpdatesAsync(
            [("FilterPolicyScope", "MessageBody"), ("FilterPolicy", """{"a":{"b":["c"]}}""")],
            (name, _, _) => name == "FilterPolicy" ? Task.FromException(boom) : Task.CompletedTask,
            CancellationToken.None,
            hadPreviousPolicy: false);

        var thrown = (await act.Should().ThrowAsync<FilterPolicyPartiallyAppliedException>()).Which;
        thrown.InnerException.Should().BeSameAs(boom);
        thrown.Message.Should().Contain("partially updated")
            .And.Contain("scope was changed to MessageBody")
            .And.Contain("no filter policy")
            .And.NotContain("previous filter policy");
    }

    [Fact]
    public async Task ApplyFilterPolicyUpdates_reports_a_policy_that_landed_before_the_scope_change_failed()
    {
        var boom = new InvalidOperationException("throttled");

        var act = () => SnsOperations.ApplyFilterPolicyUpdatesAsync(
            [("FilterPolicy", """{"a":["b"]}"""), ("FilterPolicyScope", "MessageAttributes")],
            (name, _, _) => name == "FilterPolicyScope" ? Task.FromException(boom) : Task.CompletedTask,
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<FilterPolicyPartiallyAppliedException>()).Which;
        thrown.Message.Should().Contain("partially updated")
            .And.Contain("new filter policy was set")
            .And.Contain("MessageAttributes");
    }
}
