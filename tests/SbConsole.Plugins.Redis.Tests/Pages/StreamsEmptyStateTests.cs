using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using StreamsPage = SbConsole.Plugins.Redis.Pages.Streams;

namespace SbConsole.Plugins.Redis.Tests.Pages;

/// <summary>Streams.razor already shipped in phase 2 with no page-level tests; this covers only the
/// new empty-state addition (design spec §14's "remaining empty/error states" scope item).</summary>
public class StreamsEmptyStateTests : RedisPageTestBase
{
    [Fact]
    public async Task An_empty_scan_result_shows_a_sensible_empty_state_instead_of_a_blank_list()
    {
        Operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StreamScanPage([], Cursor: 0, Complete: true, KeysScannedSoFar: 0, DatabaseKeyCountAtScanStart: 0));

        RenderFragment page = builder =>
        {
            builder.OpenComponent<StreamsPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);

        cut.Find(".streams-list-empty").TextContent.Should().Contain("No streams found");
        cut.FindAll(".stream-row").Should().BeEmpty();
    }
}
