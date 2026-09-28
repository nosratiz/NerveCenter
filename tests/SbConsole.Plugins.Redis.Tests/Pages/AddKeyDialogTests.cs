using Bunit;
using FluentAssertions;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class AddKeyDialogTests : RedisPageTestBase
{
    public AddKeyDialogTests()
    {
        // A missing key's TYPE is "none" -> Unknown; the dialog refuses to write into an existing key.
        Operations.GetKeyTypeAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(RedisKeyType.Unknown);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync()
    {
        var cut = RenderDialog<AddKeyDialog>(new Dictionary<string, object?>
        {
            [nameof(AddKeyDialog.ConnectionId)] = ConnectionId,
            [nameof(AddKeyDialog.ConnectionName)] = ConnectionName,
            [nameof(AddKeyDialog.Database)] = 0,
        });
        await SettleAsync(cut);
        return cut;
    }

    private static async Task SubmitAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        cut.Find("button.addkey-submit").Click();
        await SettleAsync(cut);
    }

    [Fact]
    public async Task Submit_is_disabled_until_a_key_name_is_entered()
    {
        var cut = await RenderAsync();

        cut.Find("button.addkey-submit").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#addkey-name").Input("config:new");
        cut.Find("#addkey-value").Input("x");
        cut.Find("button.addkey-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_string_key_is_created_with_set_and_its_ttl_in_one_command()
    {
        var cut = await RenderAsync();

        cut.Find("#addkey-name").Input("config:new");
        cut.Find("#addkey-value").Input("{\"a\":1}");
        cut.Find("#addkey-ttl").Input("600");
        await SubmitAsync(cut);

        await Operations.Received(1).SetStringAsync(Secret, 0, "config:new", "{\"a\":1}", TimeSpan.FromSeconds(600), Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().SetTtlAsync(default!, default!, default);
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled
            && r.Data is AddKeyDialog.AddedKey && ((AddKeyDialog.AddedKey)r.Data).Key == "config:new"));
    }

    [Fact]
    public async Task A_hash_key_is_created_with_hset_then_expire()
    {
        var cut = await RenderAsync();

        cut.Find(".addkey-type-hash").Click();
        cut.Find("#addkey-name").Input("session:new");
        cut.Find("#addkey-field").Input("userId");
        cut.Find("#addkey-value").Input("42");
        cut.Find("#addkey-ttl").Input("1800");
        await SubmitAsync(cut);

        Received.InOrder(() =>
        {
            Operations.SetHashFieldAsync(Secret, 0, "session:new", "userId", "42", Arg.Any<CancellationToken>());
            Operations.SetTtlAsync(Secret, Arg.Is<SetTtlRequest>(r => r.Key == "session:new" && r.ExpireIn == TimeSpan.FromSeconds(1800)), Arg.Any<CancellationToken>());
        });
        await Audit.Received().RecordAsync(Arg.Any<string>(), $"{ConnectionName}/db0/session:new", Arg.Any<ActionRisk>(), true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_list_key_is_created_with_a_push_and_no_ttl_when_left_blank()
    {
        var cut = await RenderAsync();

        cut.Find(".addkey-type-list").Click();
        cut.Find("#addkey-name").Input("jobs:new");
        cut.Find("#addkey-value").Input("job-1");
        await SubmitAsync(cut);

        await Operations.Received(1).PushListAsync(Secret, 0, "jobs:new", "job-1", false, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().SetTtlAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_set_key_is_created_with_sadd()
    {
        var cut = await RenderAsync();

        cut.Find(".addkey-type-set").Click();
        cut.Find("#addkey-name").Input("flags:new");
        cut.Find("#addkey-value").Input("acme-uk");
        await SubmitAsync(cut);

        await Operations.Received(1).AddSetMemberAsync(Secret, 0, "flags:new", "acme-uk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_sorted_set_key_is_created_with_zadd_and_the_score()
    {
        var cut = await RenderAsync();

        cut.Find(".addkey-type-zset").Click();
        cut.Find("#addkey-name").Input("lb:new");
        cut.Find("#addkey-value").Input("team-orion");
        cut.Find("#addkey-score").Input("9812");
        await SubmitAsync(cut);

        await Operations.Received(1).AddSortedSetMemberAsync(Secret, 0, "lb:new", "team-orion", 9812, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_existing_key_is_refused_inline_without_writing()
    {
        Operations.GetKeyTypeAsync(Secret, 0, "config:pricing", Arg.Any<CancellationToken>()).Returns(RedisKeyType.String);
        var cut = await RenderAsync();

        cut.Find("#addkey-name").Input("config:pricing");
        cut.Find("#addkey-value").Input("x");
        await SubmitAsync(cut);

        cut.Find(".addkey-error").TextContent.Should().Contain("already exists");
        await Operations.DidNotReceiveWithAnyArgs().SetStringAsync(default!, default, default!, default!, default, default);
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }

    [Fact]
    public async Task A_failed_write_shows_a_friendly_error_and_stays_open()
    {
        Operations.SetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException());
        var cut = await RenderAsync();

        cut.Find("#addkey-name").Input("config:new");
        cut.Find("#addkey-value").Input("x");
        await SubmitAsync(cut);

        cut.Find(".addkey-error").Should().NotBeNull();
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
