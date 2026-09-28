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

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderWithAsync(SetPage page)
    {
        Operations.ScanSetMembersAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(page);
        var cut = RenderEditor();
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Renders_cardinality_and_members_as_pills()
    {
        var cut = await RenderWithAsync(new SetPage(2, ["user-1", "user-2"], 0, true));

        cut.Find(".set-editor-cardinality").TextContent.Should().Contain("SCARD 2").And.Contain("unordered");
        cut.FindAll(".set-member-pill").Select(p => p.TextContent.Trim()).Should().Equal("user-1", "user-2");
        cut.FindAll(".set-editor-more").Should().BeEmpty();
    }

    [Fact]
    public async Task Shows_a_plus_n_pill_for_members_not_shown_and_clicking_it_shows_more()
    {
        var members = Enumerable.Range(0, 40).Select(i => $"m{i}").ToList();
        var cut = await RenderWithAsync(new SetPage(40, members, 0, true));

        cut.FindAll(".set-member-pill").Should().HaveCount(24);
        cut.Find(".set-editor-more").TextContent.Trim().Should().Be("+16");

        cut.Find(".set-editor-more").Click();
        await SettleAsync(cut);

        cut.FindAll(".set-member-pill").Should().HaveCount(40);
        cut.FindAll(".set-editor-more").Should().BeEmpty();
    }

    [Fact]
    public async Task The_plus_n_pill_sscans_the_next_page_when_the_loaded_members_run_out()
    {
        Operations.ScanSetMembersAsync(Secret, Arg.Is(0), Key, Arg.Is(0L), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SetPage(30, Enumerable.Range(0, 24).Select(i => $"m{i}").ToList(), 11, false));
        Operations.ScanSetMembersAsync(Secret, Arg.Is(0), Key, Arg.Is(11L), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SetPage(30, Enumerable.Range(24, 6).Select(i => $"m{i}").ToList(), 0, true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".set-editor-more").Click();
        await SettleAsync(cut);

        cut.FindAll(".set-member-pill").Should().HaveCount(30);
        cut.FindAll(".set-editor-more").Should().BeEmpty();
    }

    [Fact]
    public async Task Is_member_check_calls_sismember_and_shows_the_answer()
    {
        Operations.IsSetMemberAsync(Secret, 0, Key, "acme-uk", Arg.Any<CancellationToken>()).Returns(true);
        Operations.IsSetMemberAsync(Secret, 0, Key, "nope", Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderWithAsync(new SetPage(1, ["acme-uk"], 0, true));

        cut.Find("#set-editor-ismember").Input("acme-uk");
        cut.Find("#set-editor-ismember").KeyDown("Enter");
        await SettleAsync(cut);
        cut.Find(".set-editor-ismember-answer").TextContent.Should().Contain("is a member");

        cut.Find("#set-editor-ismember").Input("nope");
        cut.Find("#set-editor-ismember").KeyDown("Enter");
        await SettleAsync(cut);
        cut.Find(".set-editor-ismember-answer").TextContent.Should().Contain("not a member");
    }

    [Fact]
    public async Task Adding_a_member_calls_the_add_operation()
    {
        var cut = await RenderWithAsync(new SetPage(0, [], 0, true));

        cut.Find(".set-editor-add-toggle").Click();
        cut.Find("#set-editor-new-member").Input("user-3");
        cut.Find(".set-editor-add-member").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddSetMemberAsync(Secret, 0, Key, "user-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_member_calls_the_remove_operation()
    {
        var cut = await RenderWithAsync(new SetPage(1, ["user-1"], 0, true));

        cut.FindAll(".set-member-remove").Should().BeEmpty();
        cut.Find(".set-editor-remove-toggle").Click();
        cut.Find(".set-member-remove").Click();
        await SettleAsync(cut);

        await Operations.Received(1).RemoveSetMemberAsync(Secret, 0, Key, "user-1", Arg.Any<CancellationToken>());
    }
}
