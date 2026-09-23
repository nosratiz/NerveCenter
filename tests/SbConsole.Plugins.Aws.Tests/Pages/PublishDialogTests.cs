using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class PublishDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs / SendMessageDialogTests.cs --
    // IAsyncLifetime.DisposeAsync must run before the synchronous Dispose() xunit would otherwise
    // call.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string TopicArn = "arn:aws:sns:eu-west-1:1:orders-topic";
    private const string FifoTopicArn = "arn:aws:sns:eu-west-1:1:orders-topic.fifo";

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter -- without it, <MudDialog> treats itself as "inline and not yet shown" and renders
    // nothing. That type isn't public, so a substitute implementing both it and the public
    // IMudDialogInstance is built via reflection and cascaded as IMudDialogInstance. Same pattern
    // as CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs / SendMessageDialogTests.cs.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public PublishDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton(_audit);
        Services.AddLogging();
        Services.AddSingleton<PublishCommandHandler>();
        Services.AddSingleton<GetSubscriptionFilterPoliciesQueryHandler>();

        // No subscriptions unless a test configures otherwise -- keeps the fan-out preview off by
        // default so tests that don't care about it aren't affected by it.
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SubscriptionSummary>)[]);

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(bool isFifo, string topicArn = TopicArn)
    {
        // Must cascade as CascadingValue<TRuntimeProxyType>, not CascadingValue<IMudDialogInstance>
        // -- MudDialog's internal cascading parameter is typed IMudDialogInstanceInternal, and
        // Blazor only matches a CascadingValue<T> to consumers whose parameter type T is
        // assignable from.
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<SbConsole.Plugins.Aws.Pages.PublishDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Aws.Pages.PublishDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Aws.Pages.PublishDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Aws.Pages.PublishDialog.TopicArn), topicArn);
                inner.AddComponentParameter(4, nameof(SbConsole.Plugins.Aws.Pages.PublishDialog.TopicName), "orders-topic");
                inner.AddComponentParameter(5, nameof(SbConsole.Plugins.Aws.Pages.PublishDialog.IsFifo), isFifo);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    // Regression test for issue 4: CurrentAttributes() used to call
    // .ToDictionary(a => a.Key, a => a.Value) directly, which throws ArgumentException the moment
    // two attribute rows share a key. That method is called from Matches(), which the fan-out
    // preview's markup evaluates on every render once the topic has subscriptions -- so simply
    // typing a duplicate key (a real, easy user action) crashed the Blazor circuit. At least one
    // subscription must be present for the fan-out preview (and therefore Matches()) to render.
    [Fact]
    public void Entering_the_same_key_in_two_attribute_rows_does_not_throw()
    {
        _snsOperations.ListSubscriptionsAsync(Secret, TopicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "orders-queue", false, false, null)]);

        var cut = RenderDialog(isFifo: false);
        cut.Find("button.add-attribute").Click();
        cut.Find("button.add-attribute").Click();
        cut.FindAll(".attribute-key input")[0].Change("channel");

        var act = () => cut.FindAll(".attribute-key input")[1].Change("channel");

        act.Should().NotThrow();
    }

    // Regression test for issue 3: SnsOperations.PublishAsync forwards MessageDeduplicationId
    // whenever the request's value "is { }" (matches any non-null string, including ""), so a
    // blank Deduplication ID field used to be sent to AWS as an empty string and rejected. The
    // dialog must translate a blank field to null instead, relying on the topic's own
    // content-based deduplication when it's enabled.
    [Fact]
    public async Task Publishing_a_fifo_topic_with_a_blank_deduplication_id_sends_null_not_empty_string()
    {
        var cut = RenderDialog(isFifo: true, topicArn: FifoTopicArn);
        cut.Find("#publish-message").Input("hello there");
        cut.Find(".message-group-id input").Input("group-1");
        // Deduplication ID field is deliberately left blank.

        cut.Find("button.confirm-publish").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).PublishAsync(
            Secret, FifoTopicArn,
            Arg.Is<SnsPublishRequest>(r => r.MessageDeduplicationId == null && r.MessageGroupId == "group-1" && r.Message == "hello there"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publishing_a_fifo_topic_with_a_filled_deduplication_id_sends_it_unchanged()
    {
        var cut = RenderDialog(isFifo: true, topicArn: FifoTopicArn);
        cut.Find("#publish-message").Input("hello there");
        cut.Find(".message-group-id input").Input("group-1");
        cut.Find(".message-dedup-id input").Input("dedup-1");

        cut.Find("button.confirm-publish").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).PublishAsync(
            Secret, FifoTopicArn,
            Arg.Is<SnsPublishRequest>(r => r.MessageDeduplicationId == "dedup-1"),
            Arg.Any<CancellationToken>());
    }

    // Basic rendering smoke test for the fan-out preview -- called out in the whole-branch review
    // as untested. Not an exhaustive test of EvaluateFilterMatch's own matching rules (those are
    // covered directly against SnsOperations.EvaluateFilterMatch elsewhere); this only checks that
    // the preview renders a ✓ for a subscription whose policy matches the currently-entered
    // attributes and a ✕ for one that doesn't.
    [Fact]
    public void Fanout_preview_shows_a_checkmark_only_for_a_subscription_whose_filter_policy_matches()
    {
        _snsOperations.ListSubscriptionsAsync(Secret, TopicArn, Arg.Any<CancellationToken>())
            .Returns([
                new SubscriptionSummary("arn:sub-1", "sqs", "high-priority-queue", false, false, "{\"priority\":[\"high\"]}"),
                new SubscriptionSummary("arn:sub-2", "sqs", "low-priority-queue", false, false, "{\"priority\":[\"low\"]}"),
            ]);

        var cut = RenderDialog(isFifo: false);
        cut.Find("button.add-attribute").Click();
        cut.FindAll(".attribute-key input")[0].Change("priority");
        cut.FindAll(".attribute-value input")[0].Change("high");

        var rows = cut.FindAll(".fanout-row");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("high-priority-queue");
        rows[0].QuerySelector(".fanout-status")!.TextContent.Should().Contain("✓");
        rows[1].TextContent.Should().Contain("low-priority-queue");
        rows[1].QuerySelector(".fanout-status")!.TextContent.Should().Contain("✕");
    }
}
