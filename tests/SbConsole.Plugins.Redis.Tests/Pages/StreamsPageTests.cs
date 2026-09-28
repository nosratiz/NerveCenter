using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;
using StreamsPage = SbConsole.Plugins.Redis.Pages.Streams;

namespace SbConsole.Plugins.Redis.Tests.Pages;

/// <summary>
/// The mockup-1d Streams page: header stream dropdown (?stream= round-trip), LENGTH/LAST ID/GROUPS/PENDING
/// tiles, consumer groups + consumers (stalled-consumer detection), pending entries with bulk
/// Claim/Ack/XDEL and an entry detail. Fixture data mirrors the seeded demo stream: billing has one
/// healthy and one stalled consumer.
/// </summary>
public class StreamsPageTests : RedisPageTestBase
{
    private const string Key = "orders:stream";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string IdAgo(TimeSpan ago, int seq = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"{(Now - ago).ToUnixTimeMilliseconds()}-{seq}");

    private static readonly string FirstId = IdAgo(TimeSpan.FromMinutes(20));
    private static readonly string LastId = IdAgo(TimeSpan.FromSeconds(2));
    private static readonly string PoisonId = IdAgo(TimeSpan.FromMinutes(47));
    private static readonly string StalledId2 = IdAgo(TimeSpan.FromMinutes(47), 1);
    private static readonly string HealthyId = IdAgo(TimeSpan.FromSeconds(1));

    private static readonly StreamSummary Orders = new(Key, 245, LastId, FirstId, LastId);
    private static readonly StreamSummary Logs = new("logs:app", 30, LastId, FirstId, LastId);

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private void SetupScan(params StreamSummary[] matches) =>
        Operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StreamScanPage(matches, 0, true, matches.Length, matches.Length));

    private void SetupOrders()
    {
        SetupScan(Orders, Logs);
        Operations.GetStreamOverviewAsync(Secret, 0, Key, Arg.Any<CancellationToken>())
            .Returns(new StreamOverview(Key, 245, 3, 9, 3, LastId, FirstId, LastId));
        Operations.GetStreamConsumerGroupsAsync(Secret, 0, Key, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamConsumerGroupSummary>)
        [
            new("fulfilment", 3, 2, LastId, 12),
            new("billing", 2, 3, FirstId, 140),
            new("analytics", 2, 0, LastId, 20),
        ]);
        Operations.GetStreamEntriesAsync(Secret, Arg.Is(0), Key, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamEntry>)[new StreamEntry(LastId, [new("event", "order.created")])]);

        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Is(0), Key, "fulfilment", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamPendingEntry>)
        [
            new(IdAgo(TimeSpan.FromSeconds(3)), "fulfil-1", TimeSpan.FromSeconds(3), 1),
            new(IdAgo(TimeSpan.FromSeconds(4)), "fulfil-2", TimeSpan.FromSeconds(4), 1),
        ]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Is(0), Key, "billing", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamPendingEntry>)
        [
            new(PoisonId, "billing-2a41", TimeSpan.FromMinutes(47), 6),
            new(StalledId2, "billing-2a41", TimeSpan.FromMinutes(46), 1),
            new(HealthyId, "billing-7c9f", TimeSpan.FromSeconds(1.2), 1),
        ]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Is(0), Key, "analytics", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[]);

        Operations.GetStreamConsumersAsync(Secret, 0, Key, "fulfilment", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamConsumer>)
        [
            new("fulfil-1", 1, TimeSpan.FromSeconds(3)),
            new("fulfil-2", 1, TimeSpan.FromSeconds(4)),
            new("fulfil-3", 0, TimeSpan.FromSeconds(9)),
        ]);
        Operations.GetStreamConsumersAsync(Secret, 0, Key, "billing", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamConsumer>)
        [
            new("billing-7c9f", 1, TimeSpan.FromSeconds(0.4)),
            new("billing-2a41", 2, TimeSpan.FromMinutes(47)),
        ]);
        Operations.GetStreamConsumersAsync(Secret, 0, Key, "analytics", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamConsumer>)
        [
            new("analytics-1", 0, TimeSpan.FromSeconds(5)),
            new("analytics-2", 0, TimeSpan.FromMinutes(2)),
        ]);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderStreamsAsync(string? query = null)
    {
        if (query is not null)
        {
            Navigation.NavigateTo("/p/redis/streams?" + query);
        }

        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<StreamsPage>(0);
            builder.CloseComponent();
        });
        await SettleAsync(cut);
        return cut;
    }

    private static async Task SelectGroupAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string group)
    {
        await cut.Find($".group-row[data-group='{group}']").ClickAsync(new());
        await SettleAsync(cut);
    }

    private static IElement PendingRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string id) =>
        cut.Find($".pending-row[data-entry-id='{id}']");

    // ---- header: stream dropdown + ?stream= ----

    [Fact]
    public async Task The_first_scanned_stream_is_auto_selected_and_written_to_the_query_string()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        cut.Find(".selected-stream").TextContent.Should().Be(Key);
        cut.Find(".streams-crumb").TextContent.Should().Contain("db0 /");
        Navigation.Uri.Should().Contain("stream=orders%3Astream");
    }

    [Fact]
    public async Task The_stream_named_in_the_query_string_is_selected()
    {
        SetupOrders();
        Operations.GetStreamOverviewAsync(Secret, 0, "logs:app", Arg.Any<CancellationToken>())
            .Returns(new StreamOverview("logs:app", 30, 1, 1, 0, LastId, FirstId, LastId));

        var cut = await RenderStreamsAsync($"connectionId={ConnectionId}&database=0&stream=logs%3Aapp");

        cut.Find(".selected-stream").TextContent.Should().Be("logs:app");
        await Operations.Received().GetStreamOverviewAsync(Secret, 0, "logs:app", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_header_dropdown_lists_every_stream_and_switches_the_selection()
    {
        SetupOrders();
        Operations.GetStreamOverviewAsync(Secret, 0, "logs:app", Arg.Any<CancellationToken>())
            .Returns(new StreamOverview("logs:app", 30, 1, 1, 0, LastId, FirstId, LastId));
        var cut = await RenderStreamsAsync();

        await cut.Find(".streams-stream-menu .mud-menu-activator").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        await SettleAsync(cut);
        cut.FindAll(".streams-option").Select(o => o.GetAttribute("data-stream")).Should().Equal(Key, "logs:app");

        await cut.Find(".streams-option[data-stream='logs:app']").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".selected-stream").TextContent.Should().Be("logs:app");
        Navigation.Uri.Should().Contain("stream=logs%3Aapp");
    }

    // ---- tiles ----

    [Fact]
    public async Task Tiles_show_length_last_id_groups_and_pending()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        cut.Find(".streams-tile-length .rds-tile-value").TextContent.Should().Be("245");
        cut.Find(".streams-tile-length .rds-tile-caption").TextContent.Should().Be("first entry 20m ago");
        cut.Find(".streams-tile-lastid .rds-tile-value").TextContent.Should().Be(LastId);
        cut.Find(".streams-tile-lastid .rds-tile-caption").TextContent.Should().Match("*s ago");
        cut.Find(".streams-tile-groups .rds-tile-value").TextContent.Should().Be("3");
        cut.Find(".streams-tile-groups .rds-tile-caption").TextContent.Should().Be("7 consumers");
        cut.Find(".streams-tile-pending .rds-tile-value").TextContent.Should().Be("5");
        cut.Find(".streams-tile-pending .rds-tile-value").ClassList.Should().Contain("rds-warn");
        cut.Find(".streams-tile-pending .rds-overline").ClassList.Should().Contain("rds-warn");
        cut.Find(".streams-tile-pending .rds-tile-caption").TextContent.Should().Be("oldest idle 47m");
    }

    [Fact]
    public async Task The_pending_tile_is_not_warn_colored_when_nothing_is_pending()
    {
        SetupOrders();
        Operations.GetStreamConsumerGroupsAsync(Secret, 0, Key, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new("analytics", 2, 0, LastId, 0)]);
        var cut = await RenderStreamsAsync();

        cut.Find(".streams-tile-pending .rds-tile-value").TextContent.Should().Be("0");
        cut.Find(".streams-tile-pending .rds-tile-value").ClassList.Should().NotContain("rds-warn");
        cut.Find(".streams-tile-pending .rds-tile-caption").TextContent.Should().Be("nothing pending");
    }

    // ---- consumer groups ----

    [Fact]
    public async Task Groups_are_listed_with_lag_pending_and_consumers_and_the_hot_group_is_selected()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        var billing = cut.Find(".group-row[data-group='billing']");
        billing.QuerySelector(".group-row-lag")!.TextContent.Should().Be("140");
        billing.QuerySelector(".group-row-pending")!.TextContent.Should().Be("3");
        billing.QuerySelector(".group-row-consumers")!.TextContent.Should().Be("2");
        cut.Find(".group-row[data-group='billing']").ClassList.Should().Contain("rds-selected");
        cut.Find(".group-row[data-group='fulfilment']").ClassList.Should().NotContain("rds-selected");
        cut.Find(".streams-consumers-label").TextContent.Should().Be("billing — consumers");
    }

    [Fact]
    public async Task Without_a_hot_group_the_first_group_is_selected()
    {
        SetupOrders();
        Operations.GetStreamConsumerGroupsAsync(Secret, 0, Key, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<StreamConsumerGroupSummary>)
        [
            new("fulfilment", 3, 2, LastId, 12),
            new("analytics", 2, 0, LastId, 20),
        ]);
        var cut = await RenderStreamsAsync();

        cut.Find(".group-row[data-group='fulfilment']").ClassList.Should().Contain("rds-selected");
        cut.FindAll(".rds-warn.group-row-lag").Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_group_with_the_largest_backlog_over_the_threshold_is_warn_colored()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        cut.Find(".group-row[data-group='billing'] .group-row-lag").ClassList.Should().Contain("rds-warn");
        cut.Find(".group-row[data-group='billing'] .group-row-pending").ClassList.Should().Contain("rds-warn");
        cut.Find(".group-row[data-group='analytics'] .group-row-lag").ClassList.Should().NotContain("rds-warn");
        cut.Find(".group-row[data-group='fulfilment'] .group-row-lag").ClassList.Should().NotContain("rds-warn");
    }

    [Fact]
    public async Task Selecting_a_group_shows_its_consumers_and_pending_entries()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        await SelectGroupAsync(cut, "billing");

        cut.Find(".group-row[data-group='billing']").ClassList.Should().Contain("rds-selected");
        cut.FindAll(".consumer-row-name").Select(e => e.TextContent).Should().Equal("billing-7c9f", "billing-2a41");
        cut.FindAll(".pending-row").Should().HaveCount(3);
        cut.Find(".streams-pending-label").TextContent.Should().Be("Pending entries — billing");
    }

    // ---- stalled consumers ----

    [Fact]
    public async Task A_consumer_idle_over_five_minutes_holding_entries_is_flagged_as_stalled()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        var healthy = cut.Find(".consumer-row[data-consumer='billing-7c9f']");
        healthy.ClassList.Should().NotContain("rds-row-danger");
        healthy.QuerySelector(".rds-dot")!.ClassList.Should().NotContain("rds-dot-danger");
        healthy.QuerySelector(".consumer-row-idle")!.TextContent.Should().Be("idle 0.4s");

        var stalled = cut.Find(".consumer-row[data-consumer='billing-2a41']");
        stalled.ClassList.Should().Contain("rds-row-danger");
        stalled.QuerySelector(".rds-dot")!.ClassList.Should().Contain("rds-dot-danger");
        stalled.QuerySelector(".consumer-row-idle")!.TextContent.Should().Be("idle 47m");
        stalled.QuerySelector(".consumer-row-pending")!.TextContent.Should().Be("2");

        cut.Find(".streams-stalled[data-consumer='billing-2a41'] .streams-stalled-text").TextContent.Should().Be(
            "billing-2a41 stopped reading 47m ago and is holding 2 entries. Claim them onto a live consumer, or delete the consumer once they're moved.");
        cut.Find(".streams-claim-to-live").TextContent.Should().Be("Claim to billing-7c9f…");
    }

    [Fact]
    public async Task A_group_with_only_healthy_consumers_shows_no_stalled_warning()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "fulfilment");

        cut.FindAll(".streams-stalled").Should().BeEmpty();
        cut.FindAll(".consumer-row.rds-row-danger").Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_to_live_consumer_opens_the_claim_dialog_with_all_of_the_stalled_consumers_entries()
    {
        SetupOrders();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await cut.Find(".streams-claim-to-live").ClickAsync(new());
        await SettleAsync(cut);

        await dialogs.Received(1).ShowAsync<StreamClaimDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(StreamClaimDialog.Consumer)]! == "billing-7c9f"
                && (string)p[nameof(StreamClaimDialog.Group)]! == "billing"
                && (string)p[nameof(StreamClaimDialog.Key)]! == Key
                && ((IReadOnlyList<string>)p[nameof(StreamClaimDialog.EntryIds)]!).SequenceEqual(new[] { PoisonId, StalledId2 })),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Delete_consumer_warns_that_its_pending_entries_leave_the_PEL_then_confirms_with_IsProd()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await cut.Find(".streams-consumer-delete").ClickAsync(new());
        await SettleAsync(cut);
        cut.Find(".streams-delete-consumer-confirm").TextContent.Should()
            .Contain("billing-2a41's 2 pending entries leave the PEL with it");
        await Operations.DidNotReceiveWithAnyArgs().DeleteStreamConsumerAsync(default!, default, default!, default!, default!, default);

        await cut.Find(".streams-delete-consumer-go").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete consumer", "billing-2a41", true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteStreamConsumerAsync(Secret, 0, Key, "billing", "billing-2a41", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_consumer_does_nothing_when_the_confirmation_is_declined()
    {
        SetupOrders();
        Confirmation.ConfirmAsync("Delete consumer", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await cut.Find(".streams-consumer-delete").ClickAsync(new());
        await SettleAsync(cut);
        await cut.Find(".streams-delete-consumer-go").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().DeleteStreamConsumerAsync(default!, default, default!, default!, default!, default);
    }

    [Fact]
    public async Task A_failed_consumer_delete_shows_the_friendly_error_inline()
    {
        SetupOrders();
        Operations.DeleteStreamConsumerAsync(Secret, 0, Key, "billing", "billing-2a41", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("NOGROUP no such group"));
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await cut.Find(".streams-consumer-delete").ClickAsync(new());
        await SettleAsync(cut);
        await cut.Find(".streams-delete-consumer-go").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".streams-action-error").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    // ---- pending entries: selection + bulk actions ----

    [Fact]
    public async Task Checking_rows_counts_the_selection_and_acks_them_together()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");
        cut.FindAll(".streams-pending-ack[disabled]").Should().HaveCount(1);

        await PendingRow(cut, PoisonId).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await PendingRow(cut, StalledId2).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await SettleAsync(cut);
        cut.Find(".streams-pending-selected").TextContent.Should().Be("2 selected");

        await cut.Find(".streams-pending-ack").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).AcknowledgeStreamEntriesAsync(Secret, 0, Key, "billing",
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { PoisonId, StalledId2 })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task XDEL_of_one_selected_entry_confirms_with_IsProd_and_the_entry_id()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, PoisonId).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await SettleAsync(cut);
        await cut.Find(".streams-pending-delete").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", PoisonId, true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteStreamEntriesAsync(Secret, 0, Key,
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == PoisonId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task XDEL_of_several_entries_confirms_against_the_stream_with_a_count()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, PoisonId).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await PendingRow(cut, HealthyId).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await SettleAsync(cut);
        await cut.Find(".streams-pending-delete").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", Key, true, 2, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteStreamEntriesAsync(Secret, 0, Key,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { PoisonId, HealthyId })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task XDEL_does_nothing_when_the_confirmation_is_declined()
    {
        SetupOrders();
        Confirmation.ConfirmAsync("Delete", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, PoisonId).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await SettleAsync(cut);
        await cut.Find(".streams-pending-delete").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().DeleteStreamEntriesAsync(default!, default, default!, default!, default);
    }

    [Fact]
    public async Task Claim_on_the_selection_opens_the_claim_dialog_defaulting_to_the_live_consumer()
    {
        SetupOrders();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, StalledId2).QuerySelector(".pending-row-check")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await SettleAsync(cut);
        await cut.Find(".streams-pending-claim").ClickAsync(new());
        await SettleAsync(cut);

        await dialogs.Received(1).ShowAsync<StreamClaimDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(StreamClaimDialog.Consumer)]! == "billing-7c9f"
                && ((IReadOnlyList<string>)p[nameof(StreamClaimDialog.Consumers)]!).SequenceEqual(new[] { "billing-7c9f", "billing-2a41" })
                && ((IReadOnlyList<string>)p[nameof(StreamClaimDialog.EntryIds)]!).SequenceEqual(new[] { StalledId2 })),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Pending_idle_over_five_minutes_and_five_or_more_deliveries_are_highlighted()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        PendingRow(cut, PoisonId).QuerySelector(".pending-row-idle")!.ClassList.Should().Contain("rds-warn");
        PendingRow(cut, PoisonId).QuerySelector(".pending-row-deliveries")!.ClassList.Should().Contain("rds-danger");
        PendingRow(cut, HealthyId).QuerySelector(".pending-row-idle")!.ClassList.Should().NotContain("rds-warn");
        PendingRow(cut, StalledId2).QuerySelector(".pending-row-deliveries")!.ClassList.Should().NotContain("rds-danger");
    }

    // ---- entry detail ----

    [Fact]
    public async Task Clicking_a_pending_row_shows_its_fields_and_flags_likely_poison()
    {
        SetupOrders();
        Operations.GetStreamEntryAsync(Secret, 0, Key, PoisonId, Arg.Any<CancellationToken>())
            .Returns(new StreamEntry(PoisonId, [new("orderRef", "UK-2026-448120"), new("event", "order.paid")]));
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, PoisonId).ClickAsync(new());
        await SettleAsync(cut);

        PendingRow(cut, PoisonId).ClassList.Should().Contain("rds-selected");
        cut.Find(".streams-entry-id").TextContent.Should().Be(PoisonId);
        cut.Find(".streams-entry-deliveries").TextContent.Should().Be("delivered 6× — likely poison");
        cut.Find(".streams-entry-deliveries").ClassList.Should().Contain("rds-danger");
        cut.FindAll(".streams-entry-field").Select(e => e.TextContent).Should().Equal("orderRef", "event");
        cut.FindAll(".streams-entry-value").Select(e => e.TextContent).Should().Equal("UK-2026-448120", "order.paid");
        cut.Find(".streams-entry-footnote").TextContent.Should().Contain("flat field/value pairs");
    }

    [Fact]
    public async Task A_rarely_delivered_entry_is_not_called_poison()
    {
        SetupOrders();
        Operations.GetStreamEntryAsync(Secret, 0, Key, HealthyId, Arg.Any<CancellationToken>())
            .Returns(new StreamEntry(HealthyId, [new("event", "order.paid")]));
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, HealthyId).ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".streams-entry-deliveries").TextContent.Should().Be("delivered 1×");
        cut.Find(".streams-entry-deliveries").ClassList.Should().NotContain("rds-danger");
    }

    [Fact]
    public async Task A_pending_entry_that_was_deleted_or_trimmed_says_so()
    {
        SetupOrders();
        Operations.GetStreamEntryAsync(Secret, 0, Key, PoisonId, Arg.Any<CancellationToken>()).Returns((StreamEntry?)null);
        var cut = await RenderStreamsAsync();
        await SelectGroupAsync(cut, "billing");

        await PendingRow(cut, PoisonId).ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".streams-entry-missing").TextContent.Should().Contain("deleted or trimmed");
        cut.FindAll(".streams-entry-field").Should().BeEmpty();
    }

    // ---- header actions ----

    [Fact]
    public async Task XADD_opens_the_add_entry_dialog_for_the_selected_stream()
    {
        SetupOrders();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderStreamsAsync();

        await cut.Find(".streams-xadd-open").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<StreamAddEntryDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p => (string)p[nameof(StreamAddEntryDialog.Key)]! == Key && (Guid)p[nameof(StreamAddEntryDialog.ConnectionId)]! == ConnectionId),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Trim_opens_the_trim_dialog_with_the_server_side_prod_flag()
    {
        SetupOrders();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderStreamsAsync();

        await cut.Find(".streams-trim-open").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<StreamTrimDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(StreamTrimDialog.Key)]! == Key
                && (bool)p[nameof(StreamTrimDialog.IsProd)]! == true
                && (long)p[nameof(StreamTrimDialog.Length)]! == 245),
            Arg.Any<DialogOptions>());
    }

    // ---- recent entries, errors, empty states ----

    [Fact]
    public async Task Recent_entries_are_listed_below_the_panes()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        cut.Find(".entry-row-id").TextContent.Should().Be(LastId);
        cut.Find(".entry-row-fields").TextContent.Should().Be("event=order.created");
    }

    [Fact]
    public async Task A_failed_overview_shows_an_inline_alert()
    {
        SetupOrders();
        Operations.GetStreamOverviewAsync(Secret, 0, Key, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("WRONGTYPE"));
        var cut = await RenderStreamsAsync();

        cut.Find(".streams-detail-error").TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_stream_without_consumer_groups_explains_the_empty_state()
    {
        SetupScan(Logs);
        Operations.GetStreamOverviewAsync(Secret, 0, "logs:app", Arg.Any<CancellationToken>())
            .Returns(new StreamOverview("logs:app", 30, 1, 1, 0, LastId, FirstId, LastId));
        Operations.GetStreamConsumerGroupsAsync(Secret, 0, "logs:app", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[]);
        var cut = await RenderStreamsAsync();

        cut.Find(".streams-groups-empty").TextContent.Should().Contain("No consumer groups on logs:app");
        cut.FindAll(".group-row").Should().BeEmpty();
        cut.Find(".streams-pending-empty").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.Find(".streams-tile-groups .rds-tile-value").TextContent.Should().Be("0");
    }

    [Fact]
    public async Task A_group_with_no_pending_entries_says_it_is_caught_up()
    {
        SetupOrders();
        var cut = await RenderStreamsAsync();

        await SelectGroupAsync(cut, "analytics");

        cut.FindAll(".pending-row").Should().BeEmpty();
        cut.Find(".streams-pending-empty").TextContent.Should().Contain("analytics is caught up");
    }
}
