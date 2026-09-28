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

    [Fact]
    public async Task Renders_length_head_and_tail()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(2, [new ListEntry(0, "first")], [new ListEntry(-1, "last")]));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".list-editor-length").TextContent.Should().Contain("2");
        cut.Markup.Should().Contain("first");
        cut.Markup.Should().Contain("last");
    }

    [Fact]
    public async Task Pushing_to_head_calls_the_push_operation_with_atHead_true()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("#list-editor-push-value").Input("job-9");
        cut.Find(".list-editor-push-head").Click();
        await SettleAsync(cut);

        await Operations.Received(1).PushListAsync(Secret, 0, Key, "job-9", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trimming_confirms_with_IsProd_then_calls_the_trim_operation()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        var cut = RenderEditor(isProd: true);
        await SettleAsync(cut);

        cut.Find(".list-editor-trim").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Trim", Key, true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).TrimListAsync(Secret, 0, Key, 0, -1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trim_does_not_call_the_operation_when_confirmation_is_declined()
    {
        Operations.GetListSliceAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ListSlice(0, [], []));
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".list-editor-trim").Click();
        await SettleAsync(cut);

        await Operations.DidNotReceive().TrimListAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
