using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class KeysPageTests : RedisPageTestBase
{
    private static readonly KeySummary Csrf = new("session:1:csrf", RedisKeyType.Hash, TimeSpan.FromMinutes(5), 128);
    private static readonly KeySummary Data = new("session:2:data", RedisKeyType.String, null, 64);
    private static readonly KeySummary Queue = new("jobs:queue", RedisKeyType.List, null, 256);

    private void SetupScan(params KeySummary[] matches)
    {
        Operations.ScanKeysAsync(Secret, Arg.Any<ScanKeysRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage(matches, 0, true, matches.Length, matches.Length));
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderKeysAsync()
    {
        SetupScan(Csrf, Data, Queue);
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Redis.Pages.Keys>(0);
            builder.CloseComponent();
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Scanned_keys_group_into_a_prefix_tree()
    {
        var cut = await RenderKeysAsync();

        cut.Markup.Should().Contain("session");
        cut.Markup.Should().Contain("jobs");
    }

    [Fact]
    public async Task Expanding_a_prefix_node_shows_its_keys_in_a_flat_list()
    {
        var cut = await RenderKeysAsync();

        cut.Find(".prefix-node-row").Click();
        await SettleAsync(cut);

        cut.FindAll(".key-row").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Selecting_a_string_key_renders_the_string_editor()
    {
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StringValue("session:2:data", null, 5, "hello", false));
        var cut = await RenderKeysAsync();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("session")).Click();
        await SettleAsync(cut);
        cut.FindAll(".key-row").Single(e => e.TextContent.Contains("session:2:data")).Click();
        await SettleAsync(cut);

        cut.Find(".keys-detail-key").TextContent.Should().Be("session:2:data");
        cut.Find("#string-editor-value").Should().NotBeNull();
    }

    [Fact]
    public async Task Selecting_a_list_key_renders_the_list_editor()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(2, [new ListEntry(0, "a")], [new ListEntry(-1, "b")]));
        var cut = await RenderKeysAsync();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("jobs")).Click();
        await SettleAsync(cut);
        cut.Find(".key-row").Click();
        await SettleAsync(cut);

        cut.Find(".keys-detail-key").TextContent.Should().Be("jobs:queue");
        cut.FindAll(".list-editor-length").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Deleting_a_key_confirms_calls_the_operation_and_removes_the_row()
    {
        Operations.DeleteKeyAsync(Secret, 0, "jobs:queue", Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderKeysAsync();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("jobs")).Click();
        await SettleAsync(cut);
        cut.Find(".key-row").Click();
        await SettleAsync(cut);

        cut.Find(".keys-detail-delete").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", "jobs:queue", true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteKeyAsync(Secret, 0, "jobs:queue", Arg.Any<CancellationToken>());
        cut.FindAll(".key-row").Should().BeEmpty();
    }

    [Fact]
    public async Task Renaming_a_key_calls_the_operation_and_updates_the_selection()
    {
        Operations.RenameKeyAsync(Secret, 0, "jobs:queue", "jobs:renamed", Arg.Any<CancellationToken>()).Returns(true);
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        var cut = await RenderKeysAsync();

        cut.FindAll(".prefix-node-row").First(e => e.TextContent.Contains("jobs")).Click();
        await SettleAsync(cut);
        cut.Find(".key-row").Click();
        await SettleAsync(cut);

        cut.Find(".keys-detail-rename-toggle").Click();
        await SettleAsync(cut);
        cut.Find("#keys-detail-rename-input").Input("jobs:renamed");
        cut.Find(".keys-detail-rename-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).RenameKeyAsync(Secret, 0, "jobs:queue", "jobs:renamed", Arg.Any<CancellationToken>());
        cut.Find(".keys-detail-key").TextContent.Should().Be("jobs:renamed");
    }

    [Fact]
    public async Task Scanning_with_a_pattern_passes_it_to_the_operation()
    {
        var cut = await RenderKeysAsync();

        cut.Find("#keys-pattern").Input("session:*");
        cut.Find(".keys-scan").Click();
        await SettleAsync(cut);

        await Operations.Received().ScanKeysAsync(Secret, Arg.Is<ScanKeysRequest>(r => r.Pattern == "session:*"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_matching_keys_button_is_disabled_when_the_pattern_is_blank()
    {
        var cut = await RenderKeysAsync();

        cut.Find("#keys-pattern").Input("");

        cut.Find("button.keys-delete-by-pattern").HasAttribute("disabled").Should().BeTrue();
    }
}
