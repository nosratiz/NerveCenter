using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;
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

    /// <summary>Starts a tail on <paramref name="target"/> and returns the captured per-message callback.</summary>
    private async Task<(IRenderedComponent<Bunit.Rendering.ContainerFragment> Cut, Func<PubSubMessage, Task> Deliver, IAsyncDisposable Subscription)> StartTailAsync(string target = "notifications:live")
    {
        Func<PubSubMessage, Task>? captured = null;
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, target, Arg.Any<bool>(), Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<Func<PubSubMessage, Task>>(3);
                return Task.FromResult(subscription);
            });
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = target });
        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);
        captured.Should().NotBeNull();
        return (cut, captured!, subscription);
    }

    [Fact]
    public async Task Renders_the_console_header_with_the_connection_chip_and_no_database_chip()
    {
        var cut = await RenderPageAsync();

        cut.Find(".rds-header .rds-title").TextContent.Should().Be("Pub/Sub");
        cut.Find(".rds-header .selected-connection").TextContent.Should().Be(ConnectionName);
        cut.FindAll(".rds-header .database-menu").Should().BeEmpty("Pub/Sub is server-wide, not per database");
        cut.Find(".rds-split").Should().NotBeNull();
    }

    [Fact]
    public async Task Header_meta_counts_the_channels_with_subscribers()
    {
        Operations.GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PubSubChannelSummary("notifications:live", 3),
            new PubSubChannelSummary("orders:events", 1),
        ]);

        var cut = await RenderPageAsync();

        cut.Find(".redis-header-meta").TextContent.Should().Contain("2 channels with subscribers");
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

        cut.Find(".pubsub-channels-table").ClassList.Should().Contain("rds-table");
        var rows = cut.FindAll(".pubsub-channel-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".pubsub-channel-row-name")!.TextContent.Should().Be("notifications:live");
        rows[0].QuerySelector(".pubsub-channel-row-subscribers")!.TextContent.Should().Be("3");
    }

    [Fact]
    public async Task Clicking_a_channel_row_prefills_the_subscribe_box_and_highlights_the_row()
    {
        Operations.GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            new PubSubChannelSummary("notifications:live", 3),
            new PubSubChannelSummary("orders:events", 1),
        ]);
        var cut = await RenderPageAsync();

        await cut.FindAll(".pubsub-channel-row")[1].ClickAsync(new());

        cut.Find(".pubsub-tail-target input").GetAttribute("value").Should().Be("orders:events");
        cut.Find(".pubsub-publish-channel input").GetAttribute("value").Should().Be("orders:events");
        cut.Find(".pubsub-tail-mode-channel").ClassList.Should().Contain("rds-active");
        var rows = cut.FindAll(".pubsub-channel-row");
        rows[1].ClassList.Should().Contain("rds-selected");
        rows[0].ClassList.Should().NotContain("rds-selected");
    }

    [Fact]
    public async Task The_empty_state_explains_that_only_subscribed_channels_are_listed_and_suggests_a_pattern_tail()
    {
        var cut = await RenderPageAsync();

        var empty = cut.Find(".pubsub-channels-empty").TextContent;
        empty.Should().Contain("No channels with subscribers");
        empty.Should().Contain("at least one subscriber");
        empty.Should().Contain("notifications:*");
        cut.FindAll(".pubsub-channel-row").Should().BeEmpty();
    }

    [Fact]
    public async Task The_empty_state_suggestion_prefills_a_pattern_subscription()
    {
        var cut = await RenderPageAsync();

        await cut.Find("button.pubsub-empty-suggest").ClickAsync(new());

        cut.Find(".pubsub-tail-target input").GetAttribute("value").Should().Be("notifications:*");
        cut.Find(".pubsub-tail-mode-pattern").ClassList.Should().Contain("rds-active");
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
    public async Task The_channel_filter_is_passed_to_the_query_on_refresh()
    {
        var cut = await RenderPageAsync();

        await cut.Find(".pubsub-channel-pattern input").InputAsync(new ChangeEventArgs { Value = "orders:*" });
        await cut.Find("button.pubsub-channels-refresh").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).GetPubSubChannelsAsync(Secret, "orders:*", Arg.Any<CancellationToken>());
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
    public async Task A_failed_publish_shows_the_friendly_error_and_writes_no_success_snackbar()
    {
        Operations.PublishAsync(Secret, "notifications:live", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-publish-channel input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });

        await cut.Find("button.pubsub-publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        var snackbars = Services.GetRequiredService<ISnackbar>().ShownSnackbars;
        snackbars.Should().Contain(s => s.Severity == Severity.Error);
        snackbars.Should().NotContain(s => (s.Message ?? "").Contains("Published to"));
    }

    [Fact]
    public async Task The_status_line_reads_idle_before_any_subscription()
    {
        var cut = await RenderPageAsync();

        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("idle");
        cut.FindAll(".pubsub-tail-status .rds-dot:not(.pubsub-dot-idle)").Should().BeEmpty();
    }

    [Fact]
    public async Task Starting_a_tail_subscribes_and_shows_a_waiting_state_with_no_messages_yet()
    {
        var (cut, _, _) = await StartTailAsync();

        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("tailing notifications:live");
        cut.Find(".pubsub-tail-status .rds-dot").ClassList.Should().NotContain("pubsub-dot-idle");
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
        await cut.Find("button.pubsub-tail-mode-pattern").ClickAsync(new());

        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).SubscribeAsync(Secret, "notifications:*", true, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>());
        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("(pattern)");
    }

    [Fact]
    public async Task Typing_a_glob_switches_the_mode_to_pattern_and_a_plain_name_back_to_channel()
    {
        var cut = await RenderPageAsync();

        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:*" });
        cut.Find(".pubsub-tail-mode-pattern").ClassList.Should().Contain("rds-active");

        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "notifications:live" });
        cut.Find(".pubsub-tail-mode-channel").ClassList.Should().Contain("rds-active");
    }

    [Fact]
    public async Task An_explicit_channel_mode_subscribes_literally_even_when_the_name_contains_a_glob_character()
    {
        var subscription = Substitute.For<IAsyncDisposable>();
        Operations.SubscribeAsync(Secret, "odd*name", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscription));
        var cut = await RenderPageAsync();
        await cut.Find(".pubsub-tail-target input").InputAsync(new ChangeEventArgs { Value = "odd*name" });
        await cut.Find("button.pubsub-tail-mode-channel").ClickAsync(new());

        await cut.Find("button.pubsub-tail-start").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).SubscribeAsync(Secret, "odd*name", false, Arg.Any<Func<PubSubMessage, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Messages_delivered_by_the_subscription_callback_render_newest_first()
    {
        var (cut, deliver, _) = await StartTailAsync();

        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", "first", DateTimeOffset.UtcNow)));
        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", "second", DateTimeOffset.UtcNow)));
        cut.Render();

        var rows = cut.FindAll(".pubsub-message-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".pubsub-message-row-payload")!.TextContent.Should().Be("second");
        rows[1].QuerySelector(".pubsub-message-row-payload")!.TextContent.Should().Be("first");
        rows[0].QuerySelector(".pubsub-message-row-channel")!.ClassList.Should().Contain("rds-accent");
        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("2 messages");
    }

    [Fact]
    public async Task Message_time_renders_as_local_HH_mm_ss()
    {
        var (cut, deliver, _) = await StartTailAsync();
        var at = new DateTimeOffset(2026, 9, 28, 9, 41, 8, 123, TimeSpan.Zero);

        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", "x", at)));
        cut.Render();

        cut.Find(".pubsub-message-row-time").TextContent.Should().Be(at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Json_payloads_render_compact_on_one_line()
    {
        var (cut, deliver, _) = await StartTailAsync();

        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", "{\n  \"kind\": \"heartbeat\",\n  \"seq\": [1, 2]\n}", DateTimeOffset.UtcNow)));
        cut.Render();

        cut.Find(".pubsub-message-row-payload").TextContent.Should().Be("{\"kind\":\"heartbeat\",\"seq\":[1,2]}");
    }

    [Fact]
    public async Task Long_payloads_are_truncated_and_clicking_the_row_expands_the_full_text()
    {
        var (cut, deliver, _) = await StartTailAsync();
        var payload = new string('x', 400);

        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", payload, DateTimeOffset.UtcNow)));
        cut.Render();

        var inline = cut.Find(".pubsub-message-row-payload").TextContent;
        inline.Length.Should().BeLessThan(payload.Length);
        inline.Should().EndWith("…");
        cut.FindAll(".pubsub-message-full").Should().BeEmpty();

        await cut.Find(".pubsub-message-row").ClickAsync(new());

        cut.Find(".pubsub-message-full").TextContent.Should().Be(payload);
        cut.Find(".pubsub-message-row").ClassList.Should().Contain("rds-selected");

        await cut.Find(".pubsub-message-row").ClickAsync(new());
        cut.FindAll(".pubsub-message-full").Should().BeEmpty();
    }

    [Fact]
    public async Task The_tail_keeps_only_the_newest_messages_up_to_the_cap_and_says_so()
    {
        var (cut, deliver, _) = await StartTailAsync();

        for (var i = 0; i < PubSubPage.MaxTailMessages + 5; i++)
        {
            var n = i;
            await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", $"m{n}", DateTimeOffset.UtcNow)));
        }
        cut.Render();

        var rows = cut.FindAll(".pubsub-message-row");
        rows.Should().HaveCount(PubSubPage.MaxTailMessages);
        rows[0].QuerySelector(".pubsub-message-row-payload")!.TextContent.Should().Be($"m{PubSubPage.MaxTailMessages + 4}");
        cut.Find(".pubsub-tail-cap").TextContent.Should().Contain($"last {PubSubPage.MaxTailMessages}");
    }

    [Fact]
    public async Task Clear_empties_the_message_list_without_stopping_the_tail()
    {
        var (cut, deliver, subscription) = await StartTailAsync();
        await cut.InvokeAsync(() => deliver(new PubSubMessage("notifications:live", "first", DateTimeOffset.UtcNow)));
        cut.Render();

        await cut.Find("button.pubsub-tail-clear").ClickAsync(new());

        cut.FindAll(".pubsub-message-row").Should().BeEmpty();
        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("0 messages");
        cut.Find("button.pubsub-tail-stop").Should().NotBeNull();
        await subscription.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task Subscribing_and_stopping_refresh_the_channel_list_so_the_tails_own_subscription_shows_up_and_goes_away()
    {
        var (cut, _, _) = await StartTailAsync();
        await Operations.Received(2).GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await cut.Find("button.pubsub-tail-stop").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(3).GetPubSubChannelsAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unsubscribing_disposes_the_live_subscription()
    {
        var (cut, _, subscription) = await StartTailAsync();

        await cut.Find("button.pubsub-tail-stop").ClickAsync(new());
        await SettleAsync(cut);

        await subscription.Received(1).DisposeAsync();
        cut.Find("button.pubsub-tail-start").Should().NotBeNull();
        cut.Find(".pubsub-tail-status").TextContent.Should().Contain("idle");
    }

    [Fact]
    public async Task Disposing_the_page_without_clicking_unsubscribe_still_disposes_the_live_subscription()
    {
        // A leaked subscription would keep a ConnectionMultiplexer connected forever -- the page
        // must clean up on navigation-away/circuit-drop, not only on the explicit button click.
        var (cut, _, subscription) = await StartTailAsync();

        await cut.FindComponent<PubSubPage>().Instance.DisposeAsync();

        await subscription.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Switching_connection_disposes_the_live_subscription_from_the_previous_one()
    {
        // Mixing two servers' messages into one tail, or leaking the old multiplexer, are both bugs.
        var other = new ConnectionInfo(Guid.NewGuid(), "redis-dev", "redis", [], false, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null);
        Connections.ListAsync("redis", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "redis", ["prod"], true, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null),
            other,
        ]);
        Connections.GetSecretAsync(other.Id, Arg.Any<CancellationToken>()).Returns("standalone|0|dev:6379");
        Operations.GetPubSubChannelsAsync("standalone|0|dev:6379", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PubSubChannelSummary>>([]));
        var (cut, _, subscription) = await StartTailAsync();

        await cut.Find(".connection-menu .mud-menu-activator").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await SettleAsync(cut);
        await cut.FindAll(".connection-option").Single(o => o.TextContent == "redis-dev").ClickAsync(new());
        await SettleAsync(cut);

        await subscription.Received(1).DisposeAsync();
        cut.Find("button.pubsub-tail-start").Should().NotBeNull();
        cut.Find(".selected-connection").TextContent.Should().Be("redis-dev");
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
