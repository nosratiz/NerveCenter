using Bunit;
using FluentAssertions;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;

namespace SbConsole.Plugins.Redis.Tests.Pages;

/// <summary>The Streams page's three small dialogs: XADD, Trim and Claim. Each calls its own handler and shows errors inline, like SetTtlDialog.</summary>
public class StreamDialogTests : RedisPageTestBase
{
    private const string Key = "orders:stream";

    private Dictionary<string, object?> Common() => new()
    {
        ["ConnectionId"] = ConnectionId,
        ["ConnectionName"] = ConnectionName,
        ["Database"] = 0,
        ["Key"] = Key,
    };

    // ---- XADD ----

    [Fact]
    public async Task Add_entry_sends_the_added_fields_with_XADD_and_closes_with_the_new_id()
    {
        Operations.AddStreamEntryAsync(Secret, 0, Key, Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>()).Returns("1696000000999-0");
        var cut = RenderDialog<StreamAddEntryDialog>(Common());
        await SettleAsync(cut);
        cut.Find("button.streams-xadd-submit").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#streams-newfield-name").Input("event");
        cut.Find("#streams-newfield-value").Input("order.created");
        cut.Find("button.streams-newfield-add").Click();
        await SettleAsync(cut);
        cut.FindAll(".pendingfield-row").Should().HaveCount(1);
        cut.Find("button.streams-xadd-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddStreamEntryAsync(Secret, 0, Key,
            Arg.Is<IReadOnlyList<StreamFieldValue>>(f => f.Count == 1 && f[0].Field == "event" && f[0].Value == "order.created"),
            null, Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled && (string)r.Data! == "1696000000999-0"));
    }

    [Fact]
    public async Task Add_entry_includes_a_typed_but_not_yet_added_field()
    {
        Operations.AddStreamEntryAsync(Secret, 0, Key, Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>()).Returns("1-0");
        var cut = RenderDialog<StreamAddEntryDialog>(Common());
        await SettleAsync(cut);

        cut.Find("#streams-newfield-name").Input("event");
        cut.Find("#streams-newfield-value").Input("x");
        await SettleAsync(cut);
        cut.Find("button.streams-xadd-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).AddStreamEntryAsync(Secret, 0, Key,
            Arg.Is<IReadOnlyList<StreamFieldValue>>(f => f.Count == 1 && f[0].Field == "event"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_XADD_shows_the_error_inline_and_keeps_the_dialog_open()
    {
        Operations.AddStreamEntryAsync(Secret, 0, Key, Arg.Any<IReadOnlyList<StreamFieldValue>>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("WRONGTYPE"));
        var cut = RenderDialog<StreamAddEntryDialog>(Common());
        await SettleAsync(cut);

        cut.Find("#streams-newfield-name").Input("event");
        await SettleAsync(cut);
        cut.Find("button.streams-xadd-submit").Click();
        await SettleAsync(cut);

        cut.Find(".streams-xadd-error").TextContent.Should().NotBeNullOrWhiteSpace();
        DialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
    }

    // ---- Trim ----

    [Fact]
    public async Task Trim_confirms_with_the_prod_flag_then_calls_XTRIM()
    {
        var parameters = Common();
        parameters["IsProd"] = true;
        parameters["Length"] = 245L;
        var cut = RenderDialog<StreamTrimDialog>(parameters);
        await SettleAsync(cut);

        cut.Find("#streams-trim-maxlen").Input("500");
        cut.Find("button.streams-trim-submit").Click();
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Trim", Key, true, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).TrimStreamAsync(Secret, 0, Key, 500, Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Declining_the_trim_confirmation_does_not_trim()
    {
        Confirmation.ConfirmAsync("Trim", Key, Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var parameters = Common();
        parameters["IsProd"] = true;
        parameters["Length"] = 245L;
        var cut = RenderDialog<StreamTrimDialog>(parameters);
        await SettleAsync(cut);

        cut.Find("button.streams-trim-submit").Click();
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().TrimStreamAsync(default!, default, default!, default, default);
    }

    // ---- Claim ----

    private Dictionary<string, object?> ClaimParameters()
    {
        var parameters = Common();
        parameters["Group"] = "billing";
        parameters["EntryIds"] = (IReadOnlyList<string>)["1-0", "2-0"];
        parameters["Consumers"] = (IReadOnlyList<string>)["billing-7c9f", "billing-2a41"];
        parameters["Consumer"] = "billing-7c9f";
        return parameters;
    }

    [Fact]
    public async Task Claim_defaults_to_the_given_consumer_and_claims_every_entry_id()
    {
        Operations.ClaimStreamEntriesAsync(Secret, 0, Key, "billing", "billing-7c9f", 0, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamEntry>)[new StreamEntry("1-0", []), new StreamEntry("2-0", [])]);
        var cut = RenderDialog<StreamClaimDialog>(ClaimParameters());
        await SettleAsync(cut);

        cut.Find("button.streams-claim-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ClaimStreamEntriesAsync(Secret, 0, Key, "billing", "billing-7c9f", 0,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "1-0", "2-0" })), Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled && (int)r.Data! == 2));
    }

    [Fact]
    public async Task Claim_uses_a_typed_consumer_and_min_idle()
    {
        Operations.ClaimStreamEntriesAsync(Secret, 0, Key, "billing", "billing-new", 60000, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamEntry>)[]);
        var cut = RenderDialog<StreamClaimDialog>(ClaimParameters());
        await SettleAsync(cut);

        cut.Find("#streams-claim-consumer").Input("billing-new");
        cut.Find("#streams-claim-minidle").Input("60000");
        cut.Find("button.streams-claim-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ClaimStreamEntriesAsync(Secret, 0, Key, "billing", "billing-new", 60000,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Picking_a_listed_consumer_fills_the_target()
    {
        var cut = RenderDialog<StreamClaimDialog>(ClaimParameters());
        await SettleAsync(cut);

        cut.Find(".streams-claim-pick[data-consumer='billing-2a41']").Click();
        await SettleAsync(cut);
        cut.Find("button.streams-claim-submit").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ClaimStreamEntriesAsync(Secret, 0, Key, "billing", "billing-2a41", 0,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_claim_shows_the_error_inline()
    {
        Operations.ClaimStreamEntriesAsync(Secret, 0, Key, "billing", Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("NOGROUP"));
        var cut = RenderDialog<StreamClaimDialog>(ClaimParameters());
        await SettleAsync(cut);

        cut.Find("button.streams-claim-submit").Click();
        await SettleAsync(cut);

        cut.Find(".streams-claim-error").TextContent.Should().NotBeNullOrWhiteSpace();
        DialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
    }
}
