using Bunit;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Web.Components.Shared;

namespace SbConsole.Web.Tests;

public class ConfirmDialogTests : BunitContext
{
    public ConfirmDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as `IMudDialogInstance`.
    // bUnit resolves the cascaded value's type from `Value.GetType()` (the substitute's actual proxy
    // type), so both MudDialog's internal parameter and our component's public one resolve correctly.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    private IRenderedComponent<ConfirmDialog> RenderDialog(bool requireTyped, int? count = null)
    {
        var mudDialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
        return Render<ConfirmDialog>(parameters => parameters
            .AddCascadingValue(mudDialogInstance)
            .Add(p => p.Verb, "Purge")
            .Add(p => p.Target, "payments-dlq")
            .Add(p => p.Count, count)
            .Add(p => p.RequireTypedConfirmation, requireTyped));
    }

    [Fact]
    public void Plain_confirm_enables_immediately()
    {
        var cut = RenderDialog(requireTyped: false);

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Typed_confirmation_starts_disabled_and_enables_on_exact_match()
    {
        var cut = RenderDialog(requireTyped: true, count: 214);

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input").Input("payments-dlq");

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Typed_confirmation_stays_disabled_on_partial_match()
    {
        var cut = RenderDialog(requireTyped: true);

        cut.Find("input").Input("payments-d");

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();
    }
}
