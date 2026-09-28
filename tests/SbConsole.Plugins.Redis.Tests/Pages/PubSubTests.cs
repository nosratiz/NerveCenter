using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using PubSubPage = SbConsole.Plugins.Redis.Pages.PubSub;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class PubSubTests : RedisPageTestBase
{
    public PubSubTests()
    {
        // Every test needs the initial channel-list load on page init to resolve to something --
        // most tests don't care what, so default to empty and let individual tests override.
        Operations.GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PubSubChannelSummary>>([]));
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<PubSubPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Loads_and_renders_active_channels_with_subscriber_counts()
    {
        Operations.GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PubSubChannelSummary("notifications:live", 3),
            new PubSubChannelSummary("orders:events", 1),
        ]);

        var cut = await RenderPageAsync();

        var rows = cut.FindAll(".pubsub-channel-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".pubsub-channel-row-name")!.TextContent.Should().Be("notifications:live");
        rows[0].QuerySelector(".pubsub-channel-row-subscribers")!.TextContent.Should().Be("3");
    }

    [Fact]
    public async Task Shows_a_sensible_empty_state_when_there_are_no_active_channels()
    {
        var cut = await RenderPageAsync();

        cut.Find(".pubsub-channels-empty").TextContent.Should().Contain("No active channels");
        cut.FindAll(".pubsub-channel-row").Should().BeEmpty();
    }

    [Fact]
    public async Task A_channels_read_failure_renders_an_inline_alert_instead_of_a_blank_list()
    {
        Operations.GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var cut = await RenderPageAsync();

        cut.Find(".pubsub-channels-error").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Publish_button_is_disabled_and_explains_why_when_the_acl_check_denies_it()
    {
        Operations.HasCommandPermissionAsync(Secret, "PUBLISH", Arg.Any<CancellationToken>()).Returns(false);

        var cut = await RenderPageAsync();

        cut.Find("button.pubsub-publish-submit").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".pubsub-publish-acl-denied").TextContent.Should().Contain("Command refused by ACL");
    }

    [Fact]
    public async Task Publish_button_is_enabled_when_the_acl_check_allows_it()
    {
        Operations.HasCommandPermissionAsync(Secret, "PUBLISH", Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await cut.Find(".pubsub-publish-channel input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });

        cut.Find("button.pubsub-publish-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Publishing_calls_the_handler_and_shows_a_success_snackbar()
    {
        Operations.PublishAsync(Secret, "notifications:live", "hello", Arg.Any<CancellationToken>()).Returns(2L);
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-publish-channel input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });
        await cut.Find(".pubsub-publish-payload input").InputAsync(new ChangeEventArgs { Value = "hello" });

        await cut.Find("button.pubsub-publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PublishAsync(Secret, "notifications:live", "hello", Arg.Any<CancellationToken>());
        Services.GetRequiredService<ISnackbar>().ShownSnackbars.Should().Contain(s => (s.Message ?? "").Contains("Published to notifications:live") && (s.Message ?? "").Contains("2 receiver"));
    }

    [Fact]
    public async Task Starting_a_tail_subscribes_and_shows_a_waiting_state_with_no_messages_yet()
    {
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "notifications:live", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscription));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });

        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("notifications:live");
        cut.Find(".pubsub-tail-empty").TextContent.Should().Contain("Waiting for messages");
        cut.FindAll("button.pubsub-tail-start").Should().BeEmpty();
        cut.Find("button.pubsub-tail-stop").Should().NotBeNull();
    }

    [Fact]
    public async Task A_pattern_tail_calls_PSUBSCRIBE_semantics_via_isPattern_true()
    {
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "notifications:*", true, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscription));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:*" });
        await cut.Find(".pubsub-tail-ispattern input").ChangeAsync(new ChangeEventArgs { Value = true });

        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).SubscribeAsync(Secret, "notifications:*", true, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>());
        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("(pattern)");
    }

    [Fact]
    public async Task Messages_delivered_by_the_subscription_callback_render_newest_first()
    {
        Func<PubSubMessage, Task>? capturedCallback = null;
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "notifications:live", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedCallback = callInfo.ArgAt<Func<PubSubMessage, Task>>(3);
                return Task.FromResult(subscription);
            });
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });
        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);
        capturedCallback.Should().NotBeNull();

        await cut.InvokeAsync(() => capturedCallback!(new PubSubMessage("notifications:live", "first", DateTimeOffset.UtcNow)));
        await cut.InvokeAsync(() => capturedCallback!(new PubSubMessage("notifications:live", "second", DateTimeOffset.UtcNow)));
        cut.Render();

        var rows = cut.FindAll(".pubsub-message-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".pubsub-message-row-payload")!.TextContent.Should().Be("second");
        rows[1].QuerySelector(".pubsub-message-row-payload")!.TextContent.Should().Be("first");
    }

    [Fact]
    public async Task Unsubscribing_disposes_the_live_subscription()
    {
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "notifications:live", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscription));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });
        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        await cut.Find("button.pubsub-tail-stop").ClickAsync(new());
        await SettleAsync(cut);

        await subscription.Received(1).DisposeAsync();
        cut.Find("button.pubsub-tail-start").Should().NotBeNull();
    }

    [Fact]
    public async Task Disposing_the_page_without_clicking_unsubscribe_still_disposes_the_live_subscription()
    {
        // A leaked subscription would keep a ConnectionMultiplexer connected forever -- the page
        // must clean up on navigation-away/circuit-drop, not only on the explicit button click.
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "notifications:live", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscription));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });
        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        await cut.FindComponent<PubSubPage>().Instance.DisposeAsync();

        await subscription.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task A_failed_subscribe_shows_an_inline_error_and_stays_unsubscribed()
    {
        Operations.SubscribeAsync(Secret, "notifications:live", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });

        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".pubsub-tail-error").TextContent.Should().NotBeEmpty();
        cut.Find("button.pubsub-tail-start").Should().NotBeNull();
    }
}
