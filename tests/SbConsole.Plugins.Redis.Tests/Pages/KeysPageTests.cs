using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class KeysPageTests : RedisPageTestBase
{
    private static readonly KeySummary Csrf = new("session:1:csrf", RedisKeyType.Hash, TimeSpan.FromMinutes(29), 128);
    private static readonly KeySummary Data = new("session:2:data", RedisKeyType.String, null, 64);
    private static readonly KeySummary Queue = new("jobs:queue", RedisKeyType.List, null, 256);

    private void SetupScan(params KeySummary[] matches)
    {
        Operations.ScanKeysAsync(Secret, Arg.Any<ScanKeysRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage(matches, 0, true, matches.Length, matches.Length));
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderKeys() =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Redis.Pages.Keys>(0);
            builder.CloseComponent();
        });

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderKeysAsync(params KeySummary[] matches)
    {
        SetupScan(matches.Length == 0 ? [Csrf, Data, Queue] : matches);
        var cut = RenderKeys();
        await SettleAsync(cut);
        return cut;
    }

    private static async Task SelectAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key)
    {
        cut.Find($"[data-key='{key}']").Click();
        await SettleAsync(cut);
    }

    [Fact]
    public async Task Scans_automatically_on_load_with_the_match_all_pattern()
    {
        await RenderKeysAsync();

        await Operations.Received(1).ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "*" && r.Cursor == 0 && r.Count == 1000), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pattern_in_the_query_string_prefills_the_input_and_is_scanned()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("p/redis/keys?pattern=session%3Au%3A77*");

        var cut = await RenderKeysAsync();

        cut.Find("#keys-pattern").GetAttribute("value").Should().Be("session:u:77*");
        await Operations.Received(1).ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "session:u:77*"), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "*"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scanned_keys_group_into_a_prefix_tree_with_counts()
    {
        var cut = await RenderKeysAsync();

        var folders = cut.FindAll(".prefix-node-row");
        folders.Select(f => f.QuerySelector(".prefix-node-segment")!.TextContent).Should().Contain(["jobs:", "session:"]);
        folders.Single(f => f.TextContent.Contains("session:")).QuerySelector(".prefix-node-count")!.TextContent.Should().Be("2");
    }

    [Fact]
    public async Task Leaf_rows_show_badge_tail_ttl_and_size()
    {
        var cut = await RenderKeysAsync();

        var csrf = cut.Find("[data-key='session:1:csrf']");
        csrf.QuerySelector(".redis-type-badge")!.TextContent.Should().Be("hash");
        csrf.QuerySelector(".key-row-key")!.TextContent.Should().Be("csrf");
        csrf.QuerySelector(".key-row-ttl")!.TextContent.Should().Be("29m");
        csrf.QuerySelector(".key-row-size")!.TextContent.Should().Be("128 B");

        var data = cut.Find("[data-key='session:2:data']");
        data.QuerySelector(".redis-type-badge")!.TextContent.Should().Be("str");
        data.QuerySelector(".key-row-ttl")!.TextContent.Should().Be("no ttl");
        data.QuerySelector(".key-row-ttl")!.ClassList.Should().Contain("rds-warn");
    }

    [Fact]
    public async Task A_key_that_is_also_a_prefix_lists_its_children_flat_beside_it()
    {
        var cut = await RenderKeysAsync(
            new KeySummary("session:u:77190", RedisKeyType.Hash, null, 3000),
            new KeySummary("session:u:77190:csrf", RedisKeyType.String, null, 64));

        cut.FindAll(".key-row-key").Select(k => k.TextContent).Should().Equal("77190", "77190:csrf");
        cut.FindAll(".prefix-node-segment").Select(s => s.TextContent).Should().Equal("session:", "u:");
    }

    [Fact]
    public async Task Clicking_a_prefix_node_collapses_and_expands_it()
    {
        var cut = await RenderKeysAsync();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("jobs")).Click();
        cut.FindAll("[data-key='jobs:queue']").Should().BeEmpty();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("jobs")).Click();
        cut.FindAll("[data-key='jobs:queue']").Should().ContainSingle();
    }

    [Fact]
    public async Task Shows_scan_progress_and_the_match_count()
    {
        var next = new TaskCompletionSource<KeyScanPage>();
        Operations.ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 0), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage([Csrf], 7, false, 1000, 2_410_000));
        Operations.ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 7), Arg.Any<CancellationToken>())
            .Returns(next.Task);
        var cut = RenderKeys();
        await SettleAsync(cut);

        cut.Find(".keys-scan-progress-count").TextContent.Should().Be("1,000 / 2.41M scanned");
        cut.Find(".keys-scan-progress").TextContent.Should().Contain("1 matches so far").And.Contain("COUNT 1000 per step");

        // COUNT is only a work hint, so the running total can overshoot; completion snaps it to DBSIZE.
        next.SetResult(new KeyScanPage([], 0, true, 1000, 2_410_000));
        await SettleAsync(cut);
        cut.Find(".keys-scan-progress-count").TextContent.Should().Be("2.41M / 2.41M scanned");
        cut.Find(".keys-scan-progress").TextContent.Should().NotContain("so far");
    }

    [Fact]
    public async Task Pause_stops_the_scan_after_the_current_step_and_resume_continues_from_the_cursor()
    {
        var second = new TaskCompletionSource<KeyScanPage>();
        Operations.ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 0), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage([Csrf], 5, false, 1000, 10_000));
        Operations.ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 5), Arg.Any<CancellationToken>())
            .Returns(second.Task);
        Operations.ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 9), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage([Queue], 0, true, 1000, 10_000));
        var cut = RenderKeys();
        await SettleAsync(cut);

        cut.Find(".keys-scan-progress").TextContent.Should().Contain("so far");
        cut.FindAll(".keys-scan-skeleton").Should().NotBeEmpty();
        cut.Find(".keys-scan-pause").Click();
        second.SetResult(new KeyScanPage([Data], 9, false, 1000, 10_000));
        await SettleAsync(cut);

        await Operations.DidNotReceive().ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 9), Arg.Any<CancellationToken>());
        cut.FindAll("[data-key]").Should().HaveCount(2);

        cut.Find(".keys-scan-resume").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Cursor == 9), Arg.Any<CancellationToken>());
        cut.FindAll("[data-key]").Should().HaveCount(3);
        cut.FindAll(".keys-scan").Should().ContainSingle(); // complete: the link turns into Rescan
    }

    [Fact]
    public async Task Selecting_a_hash_shows_the_metadata_line_and_a_ttl_chip()
    {
        Operations.GetKeyMetadataAsync(Secret, 0, "session:1:csrf", Arg.Any<CancellationToken>()).Returns(new KeyMetadata("hashtable", TimeSpan.FromSeconds(3)));
        Operations.ScanHashFieldsAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HashFieldPage([new HashFieldEntry("a", "1", 1)], 0, true, 42));
        var cut = await RenderKeysAsync();

        await SelectAsync(cut, "session:1:csrf");

        cut.Find("[data-key='session:1:csrf']").ClassList.Should().Contain("rds-selected");
        cut.Find(".keys-detail-key").TextContent.Should().Be("session:1:csrf");
        cut.Find(".keys-detail-meta").TextContent.Should().Be("42 fields · 128 B · encoding hashtable · idle 3s");
        cut.Find(".keys-ttl-chip").TextContent.Should().Contain("TTL").And.Contain("29m");
        cut.Find(".keys-ttl-chip").ClassList.Should().NotContain("keys-ttl-none");
    }

    [Fact]
    public async Task Missing_metadata_parts_are_omitted_and_no_ttl_reads_none()
    {
        Operations.GetKeyMetadataAsync(Secret, 0, "session:2:data", Arg.Any<CancellationToken>()).Returns(new KeyMetadata(null, null));
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StringValue("session:2:data", null, 5, "hello", false));
        var cut = await RenderKeysAsync();

        await SelectAsync(cut, "session:2:data");

        cut.Find(".keys-detail-meta").TextContent.Should().Be("64 B");
        cut.Find(".keys-ttl-chip").TextContent.Should().Contain("none");
        cut.Find(".keys-ttl-chip").ClassList.Should().Contain("keys-ttl-none");
        cut.Find(".string-editor-code").TextContent.Should().Be("hello");
    }

    [Fact]
    public async Task Selecting_a_list_key_renders_the_list_editor_and_its_llen_in_the_meta()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(2, [new ListEntry(0, "a")], [new ListEntry(-1, "b")]));
        var cut = await RenderKeysAsync();

        await SelectAsync(cut, "jobs:queue");

        cut.Find(".keys-detail-key").TextContent.Should().Be("jobs:queue");
        cut.FindAll(".list-editor-length").Should().NotBeEmpty();
        cut.Find(".keys-detail-meta").TextContent.Should().StartWith("LLEN 2");
    }

    [Fact]
    public async Task A_stream_key_links_to_the_streams_page()
    {
        var stream = new KeySummary("orders:stream", RedisKeyType.Stream, null, 4096);
        Operations.GetStreamOverviewAsync(Secret, 0, "orders:stream", Arg.Any<CancellationToken>())
            .Returns(new StreamOverview("orders:stream", 1200, 3, 9, 2, "1-0", "0-1", "1-0"));
        var cut = await RenderKeysAsync(stream);

        await SelectAsync(cut, "orders:stream");

        cut.Find(".keys-stream-summary").TextContent.Should().Contain("1,200").And.Contain("2");
        cut.Find("a.keys-open-in-streams").GetAttribute("href")
            .Should().Be($"/p/redis/streams?connectionId={ConnectionId}&database=0&stream=orders%3Astream");
    }

    [Fact]
    public async Task Deleting_a_key_confirms_calls_the_operation_and_removes_the_row()
    {
        Operations.DeleteKeyAsync(Secret, 0, "jobs:queue", Arg.Any<CancellationToken>()).Returns(true);
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        var cut = await RenderKeysAsync();
        await SelectAsync(cut, "jobs:queue");

        cut.Find(".keys-detail-delete").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", "jobs:queue", true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteKeyAsync(Secret, 0, "jobs:queue", Arg.Any<CancellationToken>());
        cut.FindAll("[data-key='jobs:queue']").Should().BeEmpty();
        cut.FindAll(".keys-detail-empty").Should().ContainSingle();
    }

    [Fact]
    public async Task Renaming_a_key_calls_the_operation_and_updates_the_selection()
    {
        Operations.RenameKeyAsync(Secret, 0, "jobs:queue", "jobs:renamed", Arg.Any<CancellationToken>()).Returns(true);
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        var cut = await RenderKeysAsync();
        await SelectAsync(cut, "jobs:queue");

        cut.Find(".keys-detail-rename-toggle").Click();
        await SettleAsync(cut);
        cut.Find("#keys-detail-rename-input").Input("jobs:renamed");
        cut.Find(".keys-detail-rename-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).RenameKeyAsync(Secret, 0, "jobs:queue", "jobs:renamed", Arg.Any<CancellationToken>());
        cut.Find(".keys-detail-key").TextContent.Should().Be("jobs:renamed");
        cut.FindAll("[data-key='jobs:renamed']").Should().ContainSingle();
    }

    [Fact]
    public async Task Pressing_enter_in_the_pattern_rescans_with_it()
    {
        var cut = await RenderKeysAsync();

        cut.Find("#keys-pattern").Input("session:*");
        cut.Find("#keys-pattern").KeyDown("Enter");
        await SettleAsync(cut);

        await Operations.Received().ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "session:*"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rescan_link_rescans_with_the_current_pattern()
    {
        var cut = await RenderKeysAsync();

        cut.Find("#keys-pattern").Input("jobs:*");
        cut.Find(".keys-scan").Click();
        await SettleAsync(cut);

        await Operations.Received().ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "jobs:*"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_matching_is_armed_by_select_matches_and_disabled_for_a_blank_pattern()
    {
        var cut = await RenderKeysAsync();

        cut.Find("button.keys-delete-by-pattern").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#keys-select-matches").Change(true);
        cut.Find("button.keys-delete-by-pattern").HasAttribute("disabled").Should().BeFalse();

        cut.Find("#keys-pattern").Input("");
        cut.Find("button.keys-delete-by-pattern").HasAttribute("disabled").Should().BeTrue();
    }
}
