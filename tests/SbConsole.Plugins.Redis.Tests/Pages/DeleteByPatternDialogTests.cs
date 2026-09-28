using Bunit;
using FluentAssertions;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class DeleteByPatternDialogTests : RedisPageTestBase
{
    private const string Pattern = "session:*";

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync(bool isProd = false)
    {
        var cut = RenderDialog<DeleteByPatternDialog>(new Dictionary<string, object?>
        {
            [nameof(DeleteByPatternDialog.ConnectionId)] = ConnectionId,
            [nameof(DeleteByPatternDialog.ConnectionName)] = ConnectionName,
            [nameof(DeleteByPatternDialog.Database)] = 0,
            [nameof(DeleteByPatternDialog.Pattern)] = Pattern,
            [nameof(DeleteByPatternDialog.IsProd)] = isProd,
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Runs_the_dry_run_on_open_and_disables_continue_until_it_completes()
    {
        var tcs = new TaskCompletionSource<DryRunDeleteByPatternResult>();
        Operations.DryRunDeleteByPatternAsync(Secret, 0, Pattern, Arg.Any<CancellationToken>()).Returns(tcs.Task);

        var cut = RenderDialog<DeleteByPatternDialog>(new Dictionary<string, object?>
        {
            [nameof(DeleteByPatternDialog.ConnectionId)] = ConnectionId,
            [nameof(DeleteByPatternDialog.ConnectionName)] = ConnectionName,
            [nameof(DeleteByPatternDialog.Database)] = 0,
            [nameof(DeleteByPatternDialog.Pattern)] = Pattern,
        });

        cut.Find("button.delete-by-pattern-continue").HasAttribute("disabled").Should().BeTrue();

        tcs.SetResult(new DryRunDeleteByPatternResult(142, 4096, true));
        await SettleAsync(cut);

        cut.Find("button.delete-by-pattern-continue").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".delete-by-pattern-dryrun-result").TextContent.Should().Contain("142");
    }

    [Fact]
    public async Task Continue_confirms_with_the_dry_run_count_then_deletes()
    {
        Operations.DryRunDeleteByPatternAsync(Secret, 0, Pattern, Arg.Any<CancellationToken>())
            .Returns(new DryRunDeleteByPatternResult(142, 4096, true));
        Operations.DeleteByPatternAsync(Secret, 0, Pattern, Arg.Any<CancellationToken>()).Returns(142L);
        var cut = await RenderAsync(isProd: true);

        cut.Find("button.delete-by-pattern-continue").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", Pattern, true, 142, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteByPatternAsync(Secret, 0, Pattern, Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Declining_confirmation_does_not_delete()
    {
        Operations.DryRunDeleteByPatternAsync(Secret, 0, Pattern, Arg.Any<CancellationToken>())
            .Returns(new DryRunDeleteByPatternResult(142, 4096, true));
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderAsync();

        cut.Find("button.delete-by-pattern-continue").Click();
        await SettleAsync(cut);

        await Operations.DidNotReceive().DeleteByPatternAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
