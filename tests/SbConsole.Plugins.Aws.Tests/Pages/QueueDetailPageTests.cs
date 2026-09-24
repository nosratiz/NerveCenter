using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class QueueDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string QueueUrl = "https://sqs.eu-west-1.amazonaws.com/123456789012/orders";
    private const string QueueArn = "arn:aws:sqs:eu-west-1:123456789012:orders";

    private readonly ISqsOperations _sqs = Substitute.For<ISqsOperations>();
    private readonly ISnsOperations _sns = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public QueueDetailPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("aws", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "aws-prod", "aws", ["prod"]) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _sns.ListSubscriptionsForEndpointAsync(Secret, QueueArn, Arg.Any<CancellationToken>()).Returns(new List<SubscriptionSummary>());
        Services.AddSingleton(_connections);
        Services.AddSingleton(_sqs);
        Services.AddSingleton(_sns);
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddLogging();
        Services.AddSingleton<GetQueueDetailQueryHandler>();
        Services.AddSingleton<ListQueueSnsSubscriptionsQueryHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
        Services.AddSingleton<PurgeQueueCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.Aws.Messages.SendMessageCommandHandler>();
        Services.AddSingleton<ListRedriveTasksQueryHandler>();
        Services.AddSingleton<CancelRedriveCommandHandler>();
        Services.AddSingleton<StartRedriveCommandHandler>();
        _sqs.ListMessageMoveTasksAsync(Secret, QueueArn, Arg.Any<CancellationToken>()).Returns(new List<MessageMoveTaskSummary>());
    }

    private static Dictionary<string, string> Attributes(string? redrivePolicy = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["QueueArn"] = QueueArn,
            ["ApproximateNumberOfMessages"] = "42",
            ["ApproximateNumberOfMessagesNotVisible"] = "7",
            ["ApproximateNumberOfMessagesDelayed"] = "3",
            ["VisibilityTimeout"] = "30",
            ["CreatedTimestamp"] = "1700000000",
        };
        if (redrivePolicy is not null)
        {
            attributes["RedrivePolicy"] = redrivePolicy;
        }

        return attributes;
    }

    private void GivenDetail(
        Dictionary<string, string> attributes,
        IReadOnlyDictionary<string, string>? tags = null,
        IReadOnlyList<string>? sources = null) =>
        _sqs.GetQueueDetailAsync(Secret, QueueUrl, Arg.Any<CancellationToken>())
            .Returns(SqsOperations.ToQueueDetail(QueueUrl, attributes, tags ?? new Dictionary<string, string>(), sources ?? []));

    // Same approach as TopicDetailPageTests: the query-string connectionId needs a real navigation,
    // while the route segment is a plain [Parameter] set through Render<T> (bUnit doesn't route).
    private IRenderedComponent<QueueDetail> RenderPage()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/queues/{Uri.EscapeDataString(QueueUrl)}?connectionId={_connectionId}");
        return Render<QueueDetail>(parameters => parameters
            .Add(p => p.QueueUrlEncoded, Uri.EscapeDataString(QueueUrl)));
    }

    [Fact]
    public void The_route_matches_the_dashboard_problem_link_shape()
    {
        // AwsPlugin.BuildQueueProblemLink emits /p/aws/queues/{escaped url}?connectionId={id} --
        // this page's route template must accept exactly that single escaped segment.
        var route = typeof(QueueDetail).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        route.Template.Should().Be("/p/aws/queues/{QueueUrlEncoded}");
        AwsPlugin.BuildQueueProblemLink(_connectionId, QueueUrl)
            .Should().Be($"/p/aws/queues/{Uri.EscapeDataString(QueueUrl)}?connectionId={_connectionId}");
    }

    [Fact]
    public void Renders_name_arn_and_the_three_count_tiles()
    {
        GivenDetail(Attributes());

        var cut = RenderPage();

        cut.Find(".queue-name").TextContent.Should().Contain("orders");
        cut.Find(".queue-arn").TextContent.Should().Contain(QueueArn);
        cut.Find(".tile-visible").TextContent.Should().Contain("42");
        cut.Find(".tile-in-flight").TextContent.Should().Contain("7");
        cut.Find(".tile-delayed").TextContent.Should().Contain("3");
        cut.FindAll(".tile-sources").Should().BeEmpty();
    }

    [Fact]
    public void A_dead_letter_queue_shows_the_sources_tile()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a", "https://sqs/b"]);

        var cut = RenderPage();

        cut.Find(".tile-sources").TextContent.Should().Contain("2");
    }

    [Fact]
    public void Redrive_out_panel_shows_the_target_queue_and_max_receive_count()
    {
        GivenDetail(Attributes("{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:123456789012:orders-dlq\",\"maxReceiveCount\":5}"));

        var cut = RenderPage();

        var panel = cut.Find(".redrive-out");
        panel.TextContent.Should().Contain("orders-dlq");
        panel.TextContent.Should().Contain("5");
        panel.TextContent.Should().NotContain("No DLQ configured");
    }

    [Fact]
    public void Redrive_out_panel_says_no_DLQ_configured_without_a_redrive_policy()
    {
        GivenDetail(Attributes());

        var cut = RenderPage();

        cut.Find(".redrive-out").TextContent.Should().Contain("No DLQ configured");
    }

    [Fact]
    public void SNS_panel_lists_subscribed_topics_with_a_link_to_the_topic_detail_page()
    {
        GivenDetail(Attributes());
        _sns.ListSubscriptionsForEndpointAsync(Secret, QueueArn, Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary>
            {
                new("arn:sub-1", "sqs", QueueArn, false, null, null, "arn:aws:sns:eu-west-1:123456789012:order-events"),
            });

        var cut = RenderPage();

        var panel = cut.Find(".sns-subscriptions");
        panel.TextContent.Should().Contain("Subscribed to 1 SNS topic");
        panel.TextContent.Should().Contain("order-events");
        panel.TextContent.Should().Contain("sqs");
        var link = cut.Find(".sns-topic-link");
        link.GetAttribute("href").Should().Be(
            $"/p/aws/topics/{Uri.EscapeDataString("arn:aws:sns:eu-west-1:123456789012:order-events")}?connectionId={_connectionId}");
    }

    [Fact]
    public void An_SNS_permission_failure_renders_an_inline_warning_not_a_page_failure()
    {
        GivenDetail(Attributes());
        _sns.ListSubscriptionsForEndpointAsync(Secret, QueueArn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<SubscriptionSummary>>(new InvalidOperationException("not authorized to perform sns:ListSubscriptions")));

        var cut = RenderPage();

        cut.FindAll(".sns-subscriptions-error").Should().ContainSingle();
        cut.Find(".tile-visible").TextContent.Should().Contain("42");
        cut.FindAll(".queue-detail-error").Should().BeEmpty();
    }

    [Fact]
    public void Renders_sorted_attributes_and_tags_tables()
    {
        GivenDetail(Attributes(), tags: new Dictionary<string, string> { ["team"] = "payments" });

        var cut = RenderPage();

        var attributeNames = cut.FindAll(".attribute-name").Select(e => e.TextContent.Trim()).ToList();
        attributeNames.Should().Contain("VisibilityTimeout");
        attributeNames.Should().BeInAscendingOrder(StringComparer.Ordinal);
        cut.Find(".tags-table").TextContent.Should().Contain("team").And.Contain("payments");
    }

    [Fact]
    public void Unavailable_tags_render_an_honest_message()
    {
        _sqs.GetQueueDetailAsync(Secret, QueueUrl, Arg.Any<CancellationToken>())
            .Returns(SqsOperations.ToQueueDetail(QueueUrl, Attributes(), tags: null, deadLetterSourceQueueUrls: []));

        var cut = RenderPage();

        cut.Find(".tags-unavailable").TextContent.Should().Contain("unavailable");
    }

    [Fact]
    public void A_failed_detail_load_shows_an_error_instead_of_the_page_body()
    {
        _sqs.GetQueueDetailAsync(Secret, QueueUrl, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueueDetails>(new InvalidOperationException("boom")));

        var cut = RenderPage();

        cut.FindAll(".queue-detail-error").Should().ContainSingle();
        cut.FindAll(".tile-visible").Should().BeEmpty();
    }

    [Fact]
    public void Receive_button_links_to_the_receive_page_for_this_queue()
    {
        GivenDetail(Attributes());

        var cut = RenderPage();

        cut.Find("a.receive-action").GetAttribute("href").Should().Be(
            $"/p/aws/queues/receive?connectionId={_connectionId}&queueUrl={Uri.EscapeDataString(QueueUrl)}&queueName=orders");
    }

    [Fact]
    public async Task Delete_goes_through_prod_confirmation_sourced_from_the_connection_provider()
    {
        GivenDetail(Attributes());
        _confirmation.ConfirmAsync("Delete", "orders", true, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = RenderPage();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Delete", "orders", true, null, Arg.Any<CancellationToken>());
        await _sqs.Received(1).DeleteQueueAsync(Secret, QueueUrl, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declined_delete_does_not_call_the_handler()
    {
        GivenDetail(Attributes());
        _confirmation.ConfirmAsync("Delete", "orders", true, null, Arg.Any<CancellationToken>()).Returns(false);

        var cut = RenderPage();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await _sqs.DidNotReceive().DeleteQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_goes_through_prod_confirmation_with_the_visible_count()
    {
        GivenDetail(Attributes());
        _confirmation.ConfirmAsync("Purge", "orders", true, 42, Arg.Any<CancellationToken>()).Returns(false);

        var cut = RenderPage();
        cut.Find("button.purge-queue-action").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Purge", "orders", true, 42, Arg.Any<CancellationToken>());
    }

    private const string DestinationArn = "arn:aws:sqs:eu-west-1:123456789012:orders-src";

    private static MessageMoveTaskSummary MoveTask(string status, long moved, long? toMove, string? handle = "handle-1", string? failure = null) =>
        new(handle, status, QueueArn, DestinationArn, moved, toMove, failure, DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));

    private void GivenTasks(params MessageMoveTaskSummary[] tasks) =>
        _sqs.ListMessageMoveTasksAsync(Secret, QueueArn, Arg.Any<CancellationToken>()).Returns(tasks.ToList());

    [Fact]
    public void A_non_DLQ_has_no_redrive_tasks_panel_and_never_lists_move_tasks()
    {
        GivenDetail(Attributes());

        var cut = RenderPage();

        cut.FindAll(".redrive-tasks").Should().BeEmpty();
        cut.FindAll("button.redrive-action").Should().BeEmpty();
        _sqs.DidNotReceive().ListMessageMoveTasksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_DLQ_renders_each_redrive_task_with_its_progress()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("RUNNING", 25, 100), MoveTask("FAILED", 3, 10, handle: null, failure: "AccessDenied"));

        var cut = RenderPage();

        var rows = cut.FindAll(".redrive-task");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("RUNNING").And.Contain("25").And.Contain("100").And.Contain("orders-src");
        rows[0].QuerySelectorAll(".redrive-task-progress").Should().ContainSingle();
        rows[1].TextContent.Should().Contain("FAILED").And.Contain("AccessDenied");
        cut.FindAll("button.cancel-redrive").Should().ContainSingle("only a RUNNING task can be cancelled");
    }

    [Fact]
    public void A_DLQ_without_tasks_says_so()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);

        var cut = RenderPage();

        cut.Find(".redrive-tasks").TextContent.Should().Contain("No redrive tasks");
        cut.FindAll("button.redrive-action").Should().ContainSingle();
    }

    [Fact]
    public void A_failed_task_listing_is_an_inline_warning_not_a_page_failure()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        _sqs.ListMessageMoveTasksAsync(Secret, QueueArn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<MessageMoveTaskSummary>>(new InvalidOperationException("not authorized to perform sqs:ListMessageMoveTasks")));

        var cut = RenderPage();

        cut.FindAll(".redrive-tasks-error").Should().ContainSingle();
        cut.FindAll(".queue-detail-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_invokes_the_cancel_handler_and_reloads_the_tasks()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("RUNNING", 25, 100));

        var cut = RenderPage();
        GivenTasks(MoveTask("CANCELLING", 30, 100, handle: null));
        await cut.Find("button.cancel-redrive").ClickAsync(new());

        await _sqs.Received(1).CancelMessageMoveTaskAsync(Secret, "handle-1", Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => cut.Find(".redrive-task").TextContent.Should().Contain("CANCELLING"));
        cut.FindAll("button.cancel-redrive").Should().BeEmpty();
    }

    [Fact]
    public async Task Redrive_goes_through_confirmation_with_IsProd_from_the_connection_provider()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        _confirmation.ConfirmAsync("Redrive", "orders", true, null, Arg.Any<CancellationToken>()).Returns(false);

        var cut = RenderPage();
        await cut.Find("button.redrive-action").ClickAsync(new());

        await _confirmation.Received(1).ConfirmAsync("Redrive", "orders", true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Poll_refreshes_the_tasks_while_one_is_running()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("RUNNING", 25, 100));
        var cut = RenderPage();
        cut.Instance.IsPolling.Should().BeTrue();

        GivenTasks(MoveTask("COMPLETED", 100, 100, handle: null));
        var keepPolling = await cut.InvokeAsync(() => cut.Instance.PollRedriveTasksAsync());

        cut.Find(".redrive-task").TextContent.Should().Contain("COMPLETED");
        keepPolling.Should().BeFalse("nothing is RUNNING any more");
    }

    [Fact]
    public async Task Poll_does_nothing_when_no_task_is_running()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("COMPLETED", 100, 100, handle: null));
        var cut = RenderPage();
        cut.Instance.IsPolling.Should().BeFalse();
        _sqs.ClearReceivedCalls();

        var keepPolling = await cut.InvokeAsync(() => cut.Instance.PollRedriveTasksAsync());

        keepPolling.Should().BeFalse();
        await _sqs.DidNotReceive().ListMessageMoveTasksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Poll_never_overlaps_an_in_flight_refresh()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("RUNNING", 25, 100));
        var cut = RenderPage();
        var pending = new TaskCompletionSource<IReadOnlyList<MessageMoveTaskSummary>>();
        _sqs.ListMessageMoveTasksAsync(Secret, QueueArn, Arg.Any<CancellationToken>()).Returns(pending.Task);
        _sqs.ClearReceivedCalls();

        var first = cut.InvokeAsync(() => cut.Instance.PollRedriveTasksAsync());
        var second = await cut.InvokeAsync(() => cut.Instance.PollRedriveTasksAsync());

        second.Should().BeTrue("the skipped tick keeps the loop alive for the next one");
        await _sqs.Received(1).ListMessageMoveTasksAsync(Secret, QueueArn, Arg.Any<CancellationToken>());
        pending.SetResult([MoveTask("RUNNING", 50, 100)]);
        (await first).Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_the_page_stops_polling()
    {
        GivenDetail(Attributes(), sources: ["https://sqs/a"]);
        GivenTasks(MoveTask("RUNNING", 25, 100));
        var page = RenderPage().Instance;
        page.IsPolling.Should().BeTrue();

        await DisposeComponentsAsync();

        page.IsPolling.Should().BeFalse();
    }
}
