using Bunit;
using FluentAssertions;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class SetTtlDialogTests : RedisPageTestBase
{
    private const string Key = "session:1";

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync()
    {
        var cut = RenderDialog<SetTtlDialog>(new Dictionary<string, object?>
        {
            [nameof(SetTtlDialog.ConnectionId)] = ConnectionId,
            [nameof(SetTtlDialog.ConnectionName)] = ConnectionName,
            [nameof(SetTtlDialog.Database)] = 0,
            [nameof(SetTtlDialog.Key)] = Key,
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Defaults_to_expire_in_seconds_and_applies_it()
    {
        var cut = await RenderAsync();

        cut.Find("button.setttl-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetTtlAsync(Secret,
            Arg.Is<SetTtlRequest>(r => r.Database == 0 && r.Key == Key && r.ExpireIn == TimeSpan.FromSeconds(3600) && !r.Persist && r.ExpireAt == null),
            Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Persist_mode_sends_a_persist_request()
    {
        var cut = await RenderAsync();

        var group = cut.FindComponent<MudRadioGroup<string>>();
        await cut.InvokeAsync(() => group.Instance.ValueChanged.InvokeAsync("persist"));
        cut.Find("button.setttl-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetTtlAsync(Secret, Arg.Is<SetTtlRequest>(r => r.Persist), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_apply_shows_the_error_inline_and_stays_open()
    {
        Operations.SetTtlAsync(Secret, Arg.Any<SetTtlRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new TimeoutException());
        var cut = await RenderAsync();

        cut.Find("button.setttl-submit").Click();
        await SettleAsync(cut);

        cut.Find(".setttl-error").Should().NotBeNull();
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
