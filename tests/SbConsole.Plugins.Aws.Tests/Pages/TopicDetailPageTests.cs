using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class TopicDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public TopicDetailPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton<ListSubscriptionsQueryHandler>();
        Services.AddSingleton<SubscribeCommandHandler>();
        Services.AddSingleton<UnsubscribeCommandHandler>();
        Services.AddSingleton<GetTopicAttributesQueryHandler>();
        Services.AddSingleton<GetTopicDeliveryLogsQueryHandler>();
        _snsOperations.GetTopicAttributesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddLogging();
    }

    // [SupplyParameterFromQuery]-only properties can't be set via Render<T>(parameters => ...) --
    // must navigate with a real query string (established precedent: ServiceBus.Tests'
    // PeekPageTests.NavigateToPeekQuery). TopicArnEncoded is a plain (non-query) [Parameter], so
    // -- same as Peek.razor's QueueName in that same precedent -- it's set directly through the
    // Render<T> parameter builder rather than expected to resolve off the navigated URL: bUnit's
    // Render<T> doesn't run routing, so a route template's {TopicArnEncoded} segment is never
    // parsed out of NavigationManager's current URI on its own.
    [Fact]
    public void Renders_subscriptions_from_the_handler()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync("mode=access-keys;region=us-east-1", topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Markup.Should().Contain("shipment-updates");
        cut.Find("td.sub-state").TextContent.Should().Contain("Confirmed");
    }

    [Fact]
    public void Shows_pending_state_for_an_unconfirmed_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Find("td.sub-state").TextContent.Should().Contain("Pending");
    }

    [Fact]
    public void Resend_is_only_shown_for_a_pending_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([
                new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null),
                new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null),
            ]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        // A pending subscription's ARN is the literal "PendingConfirmation" -- there's nothing to
        // unsubscribe from yet, so Remove must never be offered on that row (calling
        // UnsubscribeAsync on it always fails on real AWS). Only the confirmed row gets Remove;
        // only the pending row gets Resend.
        cut.FindAll("button.resend-action").Should().ContainSingle();
        cut.FindAll("button.remove-subscription").Should().ContainSingle();
    }

    [Fact]
    public void Edit_filter_is_only_offered_on_a_confirmed_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([
                new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null),
                new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null),
            ]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        // A pending subscription's ARN is the literal "PendingConfirmation" -- SetSubscriptionAttributes
        // has nothing to target, so only the confirmed row gets Edit filter.
        cut.FindAll("button.edit-filter-action").Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_goes_through_confirmation_before_calling_the_handler()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Remove", "shipment-updates", false, null, Arg.Any<CancellationToken>()).Returns(true);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Find("button.remove-subscription").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Remove", "shipment-updates", false, null, Arg.Any<CancellationToken>());
        await _snsOperations.Received(1).UnsubscribeAsync("mode=access-keys;region=us-east-1", "arn:sub-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_does_not_call_the_handler_when_confirmation_is_declined()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Remove", "shipment-updates", false, null, Arg.Any<CancellationToken>()).Returns(false);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));

        cut.Find("button.remove-subscription").Click();
        await Task.Delay(30);

        await _snsOperations.DidNotReceive().UnsubscribeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private const string AttributesTopicArn = "arn:aws:sns:us-east-1:123456789012:shipment-updates-topic.fifo";

    private IRenderedComponent<TopicDetail> RenderTopic(string topicArn)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        return Render<TopicDetail>(parameters => parameters
            .Add(p => p.TopicArnEncoded, Uri.EscapeDataString(topicArn)));
    }

    // MudTabs only renders the active panel's content, so a test has to click the tab header.
    private static void ActivateTab(IRenderedComponent<TopicDetail> cut, string text) =>
        cut.FindAll(".mud-tab").First(t => t.TextContent.Trim() == text).Click();

    [Fact]
    public void Attributes_tab_shows_the_well_known_fields_and_every_other_raw_attribute()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        _snsOperations.GetTopicAttributesAsync("mode=access-keys;region=us-east-1", AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["TopicArn"] = AttributesTopicArn,
                ["DisplayName"] = "Shipment updates",
                ["Owner"] = "123456789012",
                ["FifoTopic"] = "true",
                ["ContentBasedDeduplication"] = "false",
                ["KmsMasterKeyId"] = "alias/aws/sns",
                ["SubscriptionsConfirmed"] = "3",
                ["SubscriptionsPending"] = "1",
                ["SubscriptionsDeleted"] = "0",
                ["SignatureVersion"] = "2",
            });

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Attributes");

        var summary = cut.Find(".topic-summary-table").TextContent;
        summary.Should().Contain("Display name").And.Contain("Shipment updates");
        summary.Should().Contain("Owner").And.Contain("123456789012");
        summary.Should().Contain("FIFO").And.Contain("Yes");
        summary.Should().Contain("Content-based dedup").And.Contain("No");
        summary.Should().Contain("KMS key").And.Contain("alias/aws/sns");
        cut.Find(".subs-confirmed").TextContent.Should().Contain("3");
        cut.Find(".subs-pending").TextContent.Should().Contain("1");
        cut.Find(".subs-deleted").TextContent.Should().Contain("0");
        cut.Find(".attributes-table").TextContent.Should().Contain("SignatureVersion");
    }

    [Fact]
    public void Access_policy_tab_pretty_prints_the_policy_json_read_only()
    {
        const string policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"}]}";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        _snsOperations.GetTopicAttributesAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["Policy"] = policy,
                ["EffectiveDeliveryPolicy"] = "{\"http\":{}}",
            });

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Access policy");

        var pre = cut.Find("pre.topic-policy");
        pre.TextContent.Should().Contain("\n").And.Contain("\"Version\": \"2012-10-17\"");
        cut.Find("pre.topic-effective-delivery-policy").TextContent.Should().Contain("\"http\"");
        cut.FindAll("pre.topic-delivery-policy").Should().BeEmpty();
        cut.FindAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public void Access_policy_tab_shows_a_non_json_policy_raw()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        _snsOperations.GetTopicAttributesAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Policy"] = "not { json" });

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Access policy");

        cut.Find("pre.topic-policy").TextContent.Should().Be("not { json");
    }

    [Fact]
    public void A_failed_attributes_load_is_an_inline_warning_and_subscriptions_still_render()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);
        _snsOperations.GetTopicAttributesAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException("AccessDenied")));

        var cut = RenderTopic(AttributesTopicArn);

        cut.Find("td.sub-state").TextContent.Should().Contain("Confirmed");
        ActivateTab(cut, "Attributes");
        cut.Find(".topic-attributes-error").TextContent.Should().Contain("AccessDenied");
        ActivateTab(cut, "Access policy");
        cut.Find(".topic-attributes-error").TextContent.Should().Contain("AccessDenied");
    }

    // --- Delivery logs tab -----------------------------------------------------------------------

    private static readonly DateTimeOffset LogTime = new(2026, 9, 25, 11, 0, 0, TimeSpan.Zero);

    private void GivenDeliveryLogs(DeliveryLogsResult result) =>
        _snsOperations.GetDeliveryLogsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private static DeliveryLogsResult TwoLogRows(bool isTruncated = false) => new(
    [
        new DeliveryLogEntry(LogTime, "FAILURE", "m-fail", "https://hooks.example.com/orders", 500, "Internal Server Error", 1200, 3),
        new DeliveryLogEntry(LogTime.AddMinutes(-5), "SUCCESS", "m-ok", "arn:aws:sqs:us-east-1:123456789012:orders-queue", 200, "{\"sqsRequestId\":\"r\"}", 21, 1),
    ], LoggingNotConfigured: false, IsTruncated: isTruncated);

    [Fact]
    public void Delivery_logs_are_only_fetched_when_the_tab_is_first_opened()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(TwoLogRows());

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Attributes");

        _snsOperations.DidNotReceiveWithAnyArgs().GetDeliveryLogsAsync(default!, default!, default, default, default);

        ActivateTab(cut, "Delivery logs");
        cut.WaitForAssertion(() => cut.FindAll(".delivery-logs-table tbody tr").Should().HaveCount(2));
        ActivateTab(cut, "Subscriptions");
        ActivateTab(cut, "Delivery logs");

        // Default window is 24h; re-opening the tab reuses what was loaded.
        _snsOperations.Received(1).GetDeliveryLogsAsync("mode=access-keys;region=us-east-1", AttributesTopicArn, TimeSpan.FromHours(24), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Delivery_logs_render_status_destination_code_response_dwell_and_attempts()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(TwoLogRows());

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.FindAll(".delivery-logs-table tbody tr").Should().HaveCount(2));
        var first = cut.FindAll(".delivery-logs-table tbody tr")[0].TextContent;
        first.Should().Contain("FAILURE").And.Contain("https://hooks.example.com/orders").And.Contain("500")
            .And.Contain("Internal Server Error").And.Contain("1200").And.Contain("3").And.Contain("m-fail");
        cut.FindAll(".delivery-logs-table tbody tr")[1].TextContent.Should().Contain("SUCCESS");
    }

    [Fact]
    public void Failures_filter_hides_successful_deliveries_without_refetching()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(TwoLogRows());

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");
        cut.WaitForAssertion(() => cut.FindAll(".delivery-logs-table tbody tr").Should().HaveCount(2));

        cut.Find("button.delivery-logs-status-failures").Click();

        cut.FindAll(".delivery-logs-table tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("m-fail");
        _snsOperations.ReceivedWithAnyArgs(1).GetDeliveryLogsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void Changing_the_window_reloads_with_that_window()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(TwoLogRows());

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");
        cut.WaitForAssertion(() => cut.FindAll(".delivery-logs-table tbody tr").Should().HaveCount(2));

        cut.Find("button.delivery-logs-window-7d").Click();

        cut.WaitForAssertion(() => _snsOperations.Received(1).GetDeliveryLogsAsync(
            Arg.Any<string>(), AttributesTopicArn, TimeSpan.FromDays(7), Arg.Any<int>(), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Not_configured_logging_shows_an_info_alert_naming_the_topic_attributes()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(new DeliveryLogsResult([], LoggingNotConfigured: true, IsTruncated: false));

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.Find(".delivery-logs-not-configured").TextContent
            .Should().Contain("Delivery status logging isn't enabled for this topic").And.Contain("FeedbackRoleArn"));
        cut.FindAll(".delivery-logs-table").Should().BeEmpty();
    }

    [Fact]
    public void Configured_feedback_roles_without_any_log_groups_yet_say_no_logs_have_been_written()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        _snsOperations.GetTopicAttributesAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["SQSFailureFeedbackRoleArn"] = "arn:aws:iam::123456789012:role/sns-logs" });
        GivenDeliveryLogs(new DeliveryLogsResult([], LoggingNotConfigured: true, IsTruncated: false));

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.Find(".delivery-logs-not-configured").TextContent
            .Should().Contain("no delivery status logs have been written yet"));
    }

    [Fact]
    public void A_failed_delivery_logs_load_is_an_inline_warning_and_other_tabs_are_unaffected()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);
        _snsOperations.GetDeliveryLogsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DeliveryLogsResult>(new Amazon.CloudWatchLogs.Model.AccessDeniedException("denied")));

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.Find(".delivery-logs-error").TextContent.Should().Contain("logs:FilterLogEvents"));
        ActivateTab(cut, "Subscriptions");
        cut.Find("td.sub-state").TextContent.Should().Contain("Confirmed");
    }

    [Fact]
    public void An_unparsed_event_shows_its_raw_text_and_truncation_is_noted()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(new DeliveryLogsResult(
            [new DeliveryLogEntry(LogTime, "FAILURE", null, null, null, null, null, null, RawMessage: "garbled {")],
            LoggingNotConfigured: false, IsTruncated: true));

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.Find(".delivery-logs-table .delivery-log-raw").TextContent.Should().Contain("garbled {"));
        cut.Find(".delivery-logs-truncated").Should().NotBeNull();
    }

    [Fact]
    public void An_empty_window_says_so()
    {
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), AttributesTopicArn, Arg.Any<CancellationToken>()).Returns([]);
        GivenDeliveryLogs(new DeliveryLogsResult([], LoggingNotConfigured: false, IsTruncated: false));

        var cut = RenderTopic(AttributesTopicArn);
        ActivateTab(cut, "Delivery logs");

        cut.WaitForAssertion(() => cut.Find(".delivery-logs-empty").TextContent.Should().Contain("No delivery status log events"));
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\n  \"a\": 1\n}")]
    [InlineData("{\"arn\":\"a+b&c\"}", "{\n  \"arn\": \"a+b&c\"\n}")]
    [InlineData("not json", "not json")]
    [InlineData("", "")]
    public void FormatJson_indents_valid_json_and_passes_anything_else_through(string raw, string expected)
    {
        TopicDetail.FormatJson(raw).ReplaceLineEndings("\n").Should().Be(expected);
    }
}
