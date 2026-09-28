using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class SetEditorTests : RedisPageTestBase
{
    private const string Key = "flags:beta";

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor()
    {
        return RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SetEditor>(0);
            builder.AddComponentParameter(1, nameof(SetEditor.ConnectionId), ConnectionId);
            builder.AddComponentParameter(2, nameof(SetEditor.ConnectionName), ConnectionName);
            builder.AddComponentParameter(3, nameof(SetEditor.Database), 0);
            builder.AddComponentParameter(4, nameof(SetEditor.Key), Key);
            builder.CloseComponent();
        });
    }

    [Fact]
    public async Task Renders_cardinality_and_members()
    {
        Operations.ScanSetMembersAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SetPage(2, ["user-1", "user-2"], 0, true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".set-editor-cardinality").TextContent.Should().Contain("2");
        cut.Markup.Should().Contain("user-1");
        cut.Markup.Should().Contain("user-2");
    }

    [Fact]
    public async Task Adding_a_member_calls_the_add_operation()
    {
        Operations.ScanSetMembersAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SetPage(0, [], 0, true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("#set-editor-new-member").Input("user-3");
        cut.Find(".set-editor-add-member").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddSetMemberAsync(Secret, 0, Key, "user-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_member_calls_the_remove_operation()
    {
        Operations.ScanSetMembersAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SetPage(1, ["user-1"], 0, true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".set-member-remove").Click();
        await SettleAsync(cut);

        await Operations.Received(1).RemoveSetMemberAsync(Secret, 0, Key, "user-1", Arg.Any<CancellationToken>());
    }
}
