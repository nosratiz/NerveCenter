using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class SortedSetEditorTests : RedisPageTestBase
{
    private const string Key = "lb:us-east";

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor()
    {
        return RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SortedSetEditor>(0);
            builder.AddComponentParameter(1, nameof(SortedSetEditor.ConnectionId), ConnectionId);
            builder.AddComponentParameter(2, nameof(SortedSetEditor.ConnectionName), ConnectionName);
            builder.AddComponentParameter(3, nameof(SortedSetEditor.Database), 0);
            builder.AddComponentParameter(4, nameof(SortedSetEditor.Key), Key);
            builder.CloseComponent();
        });
    }

    private void SetupRange(SortedSetSlice slice) =>
        Operations.GetSortedSetRangeAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(slice);

    [Fact]
    public async Task Renders_cardinality_and_entries()
    {
        SetupRange(new SortedSetSlice(1, [new SortedSetEntry("player-1", 42.5)], 0, 49));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".sortedset-editor-cardinality").TextContent.Should().Contain("ZCARD 1").And.Contain("by score, desc");
        cut.Markup.Should().Contain("player-1");
        cut.Find(".sortedset-entry-score").TextContent.Should().Be("42.5");
    }

    [Fact]
    public async Task Score_bars_are_scaled_to_the_top_score()
    {
        SetupRange(new SortedSetSlice(18402, [new SortedSetEntry("team-orion", 9812), new SortedSetEntry("team-grus", 4906)], 0, 49));
        var cut = RenderEditor();
        await SettleAsync(cut);

        var bars = cut.FindAll(".sortedset-entry-bar > span");
        bars[0].GetAttribute("style").Should().Contain("width:100%");
        bars[1].GetAttribute("style").Should().Contain("width:50%");
        cut.FindAll(".sortedset-entry-score")[0].TextContent.Should().Be("9,812");
    }

    [Fact]
    public async Task Changing_the_range_reloads_with_the_new_bounds()
    {
        SetupRange(new SortedSetSlice(0, [], 0, 49));
        var cut = RenderEditor();
        await SettleAsync(cut);

        await Operations.Received(1).GetSortedSetRangeAsync(Secret, 0, Key, 0, 49, true, Arg.Any<CancellationToken>());
        cut.Find("#sortedset-editor-range-stop").Change("9");
        await SettleAsync(cut);

        await Operations.Received(1).GetSortedSetRangeAsync(Secret, 0, Key, 0, 9, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_a_member_calls_the_add_operation_with_the_score()
    {
        SetupRange(new SortedSetSlice(0, [], 0, 49));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".sortedset-editor-add-toggle").Click();
        cut.Find("#sortedset-editor-new-member").Input("player-2");
        cut.Find("#sortedset-editor-new-score").Input("10");
        cut.Find(".sortedset-editor-add-member").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddSortedSetMemberAsync(Secret, 0, Key, "player-2", 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Incrementing_a_score_calls_the_increment_operation()
    {
        SetupRange(new SortedSetSlice(0, [], 0, 49));
        Operations.IncrementSortedSetScoreAsync(Secret, 0, Key, "player-1", 5, Arg.Any<CancellationToken>()).Returns(47.5);
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".sortedset-editor-incr-toggle").Click();
        cut.Find("#sortedset-editor-incr-member").Input("player-1");
        cut.Find("#sortedset-editor-incr-by").Input("5");
        cut.Find(".sortedset-editor-increment").Click();
        await SettleAsync(cut);

        await Operations.Received(1).IncrementSortedSetScoreAsync(Secret, 0, Key, "player-1", 5, Arg.Any<CancellationToken>());
    }
}
