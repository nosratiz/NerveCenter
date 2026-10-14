using FluentAssertions;
using MudBlazor;
using NSubstitute;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components.Shared;
using SbConsole.Web.Confirmation;

namespace SbConsole.Web.Tests;

public class MudConfirmationServiceTests
{
    private static readonly ConfirmationRequest Request = new(
        "Terminate", "pid 48213", IsProd: true, Consequence: "Dropped.",
        Details: [new ConfirmationDetail("pid", "48213")], AuditAction: "pg.backend.terminate", Destructive: true);

    private static (MudConfirmationService Svc, Func<DialogParameters?> Captured, Func<string?> Title) Create(string? typedSetting, bool canceled = false)
    {
        var dialogs = Substitute.For<IDialogService>();
        var settings = Substitute.For<ISettings>();
        settings.GetAsync("confirm.requireTypedForProd", Arg.Any<CancellationToken>()).Returns(typedSetting);
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult<DialogResult?>(canceled ? DialogResult.Cancel() : DialogResult.Ok(true)));
        DialogParameters? captured = null;
        string? title = null;
        dialogs.ShowAsync<ConfirmDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(ci => { title = ci.ArgAt<string>(0); captured = ci.ArgAt<DialogParameters>(1); return Task.FromResult(reference); });
        return (new MudConfirmationService(dialogs, settings), () => captured, () => title);
    }

    [Fact]
    public async Task Detailed_overload_passes_request_parameters_and_requires_typed_for_prod()
    {
        var (svc, captured, title) = Create(typedSetting: null);

        (await svc.ConfirmAsync(Request)).Should().BeTrue();

        var p = captured()!;
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Verb) && (string?)kv.Value == "Terminate");
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Target) && (string?)kv.Value == "pid 48213");
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.RequireTypedConfirmation) && (bool)kv.Value! == true);
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Consequence) && (string?)kv.Value == "Dropped.");
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.AuditAction) && (string?)kv.Value == "pg.backend.terminate");
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Destructive) && (bool)kv.Value! == true);
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.IsProd) && (bool)kv.Value! == true);
        title().Should().Be("Terminate");
    }

    [Fact]
    public async Task Detailed_overload_respects_disabled_typed_setting_and_cancel()
    {
        var (svc, captured, _) = Create(typedSetting: "false", canceled: true);

        (await svc.ConfirmAsync(Request)).Should().BeFalse();

        captured()!.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.RequireTypedConfirmation) && (bool)kv.Value! == false);
    }

    [Fact]
    public async Task Plain_overload_keeps_title_verb_target()
    {
        var (svc, _, title) = Create(typedSetting: null);

        await svc.ConfirmAsync("Purge", "q", false);

        title().Should().Be("Purge q?");
    }
}
