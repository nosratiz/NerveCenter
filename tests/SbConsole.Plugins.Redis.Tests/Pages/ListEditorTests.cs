using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class ListEditorTests : RedisPageTestBase
{
    private const string Key = "jobs:queue";

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor(bool isProd = false)
    {
        return RenderWithPopovers(builder =>
        {
            builder.OpenComponent<ListEditor>(0);
            builder.AddComponentParameter(1, nameof(ListEditor.ConnectionId), ConnectionId);
            builder.AddComponentParameter(2, nameof(ListEditor.ConnectionName), ConnectionName);
            builder.AddComponentParameter(3, nameof(ListEditor.Database), 0);
            builder.AddComponentParameter(4, nameof(ListEditor.Key), Key);
            builder.AddComponentParameter(5, nameof(ListEditor.IsProd), isProd);
            builder.CloseComponent();
        });
    }

    private void SetupSlice(ListSlice slice) =>
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(slice);

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderEmptyAsync(bool isProd = false)
    {
        SetupSlice(new ListSlice(0, [], []));
        var cut = RenderEditor(isProd);
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Renders_llen_head_rows_a_gap_and_negative_tail_rows()
    {
        SetupSlice(new ListSlice(2318,
            [new ListEntry(0, "first"), new ListEntry(1, "second")],
            [new ListEntry(-2, "penultimate"), new ListEntry(-1, "last")]));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".list-editor-length").TextContent.Should().Contain("LLEN 2,318").And.Contain("head is next to pop");
        var indexes = cut.FindAll(".list-entry-index").Select(e => e.TextContent.Trim()).ToList();
        indexes.Should().Equal("0", "1", "-2", "-1");
        cut.Find(".list-editor-gap").TextContent.Should().Contain("2,314 more");
        cut.FindAll(".list-entry-value").Last().TextContent.Should().Be("last");
    }

    [Fact]
    public async Task A_short_list_renders_each_element_once_with_positive_indexes_and_no_gap()
    {
        // LRANGE 0..N and -M..-1 overlap when the list is shorter than N+M.
        SetupSlice(new ListSlice(3,
            [new ListEntry(0, "a"), new ListEntry(1, "b"), new ListEntry(2, "c")],
            [new ListEntry(-2, "b"), new ListEntry(-1, "c")]));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.FindAll(".list-entry-index").Select(e => e.TextContent.Trim()).Should().Equal("0", "1", "2");
        cut.FindAll(".list-editor-gap").Should().BeEmpty();
    }

    [Fact]
    public async Task Pushing_defaults_to_the_head()
    {
        var cut = await RenderEmptyAsync();

        cut.Find(".list-editor-push").Click();
        cut.Find("#list-editor-push-value").Input("job-9");
        cut.Find(".list-editor-push-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).PushListAsync(Secret, 0, Key, "job-9", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_head_tail_link_switches_the_push_to_the_tail()
    {
        var cut = await RenderEmptyAsync();

        cut.Find(".list-editor-push").Click();
        cut.Find(".list-editor-push-side").Click();
        cut.Find(".list-editor-push-submit").TextContent.Should().Contain("RPUSH");
        cut.Find("#list-editor-push-value").Input("job-9");
        cut.Find(".list-editor-push-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).PushListAsync(Secret, 0, Key, "job-9", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trimming_confirms_with_IsProd_then_calls_the_trim_operation()
    {
        var cut = await RenderEmptyAsync(isProd: true);

        cut.Find(".list-editor-trim-toggle").Click();
        cut.Find(".list-editor-trim").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Trim", Key, true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).TrimListAsync(Secret, 0, Key, 0, -1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trim_does_not_call_the_operation_when_confirmation_is_declined()
    {
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderEmptyAsync();

        cut.Find(".list-editor-trim-toggle").Click();
        cut.Find(".list-editor-trim").Click();
        await SettleAsync(cut);

        await Operations.DidNotReceive().TrimListAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
