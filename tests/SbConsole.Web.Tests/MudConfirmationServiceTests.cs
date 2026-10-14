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
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Detailed) && (bool)kv.Value! == true);
        p.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Details) && ReferenceEquals(kv.Value, Request.Details));
        title().Should().Be("Terminate");
    }

    [Fact]
    public async Task Minimal_request_is_detailed_with_bare_verb_title()
    {
        var (svc, captured, title) = Create(typedSetting: null);

        await svc.ConfirmAsync(new ConfirmationRequest("Cancel", "pid 1", IsProd: true));

        captured()!.Should().Contain(kv => kv.Key == nameof(ConfirmDialog.Detailed) && (bool)kv.Value! == true);
        title().Should().Be("Cancel");
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

    [Fact]
    public async Task Plain_overload_is_not_detailed()
    {
        var (svc, captured, _) = Create(typedSetting: null);

        await svc.ConfirmAsync("Purge", "q", false);

        captured()!.Should().NotContain(kv => kv.Key == nameof(ConfirmDialog.Detailed) && (bool)kv.Value! == true);
    }

    private sealed class PlainOnlyStub : IConfirmationService
    {
        public (string Verb, string Target, bool IsProd, int? Count)? Args;
        public Task<bool> ConfirmAsync(string verb, string target, bool isProd, int? count = null, CancellationToken ct = default)
        {
            Args = (verb, target, isProd, count);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Default_interface_method_forwards_verb_target_isprod_to_plain_method()
    {
        var stub = new PlainOnlyStub();
        IConfirmationService svc = stub;

        (await svc.ConfirmAsync(Request)).Should().BeTrue();

        stub.Args.Should().Be(("Terminate", "pid 48213", true, (int?)null));
    }
}
