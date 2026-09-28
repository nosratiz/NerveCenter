using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class StreamsPageTests : RedisPageTestBase
{
    private static readonly StreamSummary Orders = new("orders:events", 12, "1696000000123-0", "1695000000000-0", "1696000000123-0");
    private static readonly StreamSummary Jobs = new("jobs:events", 3, "1696000000200-0", "1695500000000-0", "1696000000200-0");

    private void SetupScan(params StreamSummary[] matches) =>
        Operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StreamScanPage(matches, 0, true, matches.Length, matches.Length));

    private void SetupDetail(string key, StreamOverview overview, IReadOnlyList<StreamConsumerGroupSummary>? groups = null, IReadOnlyList<StreamEntry>? entries = null)
    {
        Operations.GetStreamOverviewAsync(Secret, 0, key, Arg.Any<CancellationToken>()).Returns(overview);
        Operations.GetStreamConsumerGroupsAsync(Secret, 0, key, Arg.Any<CancellationToken>()).Returns(groups ?? []);
        Operations.GetStreamEntriesAsync(Secret, Arg.Any<int>(), key, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(entries ?? []);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderStreamsAsync()
    {
        SetupScan(Orders, Jobs);
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Redis.Pages.Streams>(0);
            builder.CloseComponent();
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Scanned_streams_are_listed_with_their_length()
    {
        var cut = await RenderStreamsAsync();

        cut.FindAll(".stream-row").Should().HaveCount(2);
        cut.Markup.Should().Contain("orders:events");
        cut.Markup.Should().Contain("jobs:events");
    }

    [Fact]
    public async Task Selecting_a_stream_shows_its_overview()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0"));
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);

        cut.Find(".streams-detail-key").TextContent.Should().Be("orders:events");
        cut.Find(".streams-overview-length").TextContent.Should().Contain("12");
        cut.Find(".streams-overview-groups").TextContent.Should().Contain("1");
    }

    [Fact]
    public async Task Selecting_a_group_shows_its_consumers_and_pending_entries()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0"),
            [new StreamConsumerGroupSummary("workers", 1, 1, "1696000000000-0", 0)]);
        Operations.GetStreamConsumersAsync(Secret, 0, "orders:events", "workers", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumer>)[new StreamConsumer("worker-1", 1, TimeSpan.FromSeconds(30))]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Any<int>(), "orders:events", "workers", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(30), 2)]);
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find(".group-row").Click();
        await SettleAsync(cut);

        cut.Find(".consumer-row-name").TextContent.Should().Be("worker-1");
        cut.Find(".pending-row-id").TextContent.Should().Be("1696000000000-0");
    }

    [Fact]
    public async Task Acking_a_pending_entry_calls_the_operation_and_removes_the_row()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0"),
            [new StreamConsumerGroupSummary("workers", 1, 1, "1696000000000-0", 0)]);
        Operations.GetStreamConsumersAsync(Secret, 0, "orders:events", "workers", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumer>)[]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Any<int>(), "orders:events", "workers", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(30), 2)]);
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find(".group-row").Click();
        await SettleAsync(cut);

        cut.Find(".pending-row-ack").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AcknowledgeStreamEntriesAsync(Secret, 0, "orders:events", "workers",
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "1696000000000-0"), Arg.Any<CancellationToken>());
        cut.FindAll(".pending-row").Should().BeEmpty();
    }

    [Fact]
    public async Task Claiming_a_pending_entry_calls_the_operation_with_the_entered_consumer()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0"),
            [new StreamConsumerGroupSummary("workers", 1, 1, "1696000000000-0", 0)]);
        Operations.GetStreamConsumersAsync(Secret, 0, "orders:events", "workers", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumer>)[]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Any<int>(), "orders:events", "workers", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(30), 2)]);
        Operations.ClaimStreamEntriesAsync(Secret, 0, "orders:events", "workers", "worker-2", Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamEntry>)[new StreamEntry("1696000000000-0", [])]);
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find(".group-row").Click();
        await SettleAsync(cut);
        cut.Find("#streams-claim-consumer").Input("worker-2");
        cut.Find(".pending-row-claim").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ClaimStreamEntriesAsync(Secret, 0, "orders:events", "workers", "worker-2", Arg.Any<long>(),
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "1696000000000-0"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_pending_entry_confirms_with_IsProd_then_calls_XDEL()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0"),
            [new StreamConsumerGroupSummary("workers", 1, 1, "1696000000000-0", 0)]);
        Operations.GetStreamConsumersAsync(Secret, 0, "orders:events", "workers", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumer>)[]);
        Operations.GetStreamPendingEntriesAsync(Secret, Arg.Any<int>(), "orders:events", "workers", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(30), 2)]);
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find(".group-row").Click();
        await SettleAsync(cut);

        cut.Find(".pending-row-delete").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", "1696000000000-0", true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteStreamEntriesAsync(Secret, 0, "orders:events",
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "1696000000000-0"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trimming_confirms_with_IsProd_then_calls_the_trim_operation()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 0, "1696000000123-0", "1695000000000-0", "1696000000123-0"));
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find("#streams-trim-maxlen").Input("500");
        cut.Find(".streams-trim-submit").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Trim", "orders:events", true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).TrimStreamAsync(Secret, 0, "orders:events", 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_a_field_then_submitting_calls_XADD_with_that_field()
    {
        SetupDetail("orders:events", new StreamOverview("orders:events", 12, 1, 1, 0, "1696000000123-0", "1695000000000-0", "1696000000123-0"));
        Operations.AddStreamEntryAsync(Secret, 0, "orders:events", Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>())
            .Returns("1696000000999-0");
        var cut = await RenderStreamsAsync();

        cut.Find(".stream-row").Click();
        await SettleAsync(cut);
        cut.Find("#streams-newfield-name").Input("event");
        cut.Find("#streams-newfield-value").Input("order.created");
        cut.Find(".streams-newfield-add").Click();
        await SettleAsync(cut);
        cut.Find(".streams-xadd-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddStreamEntryAsync(Secret, 0, "orders:events",
            Arg.Is<IReadOnlyList<StreamFieldValue>>(f => f.Count == 1 && f[0].Field == "event" && f[0].Value == "order.created"),
            null, Arg.Any<CancellationToken>());
    }
}
