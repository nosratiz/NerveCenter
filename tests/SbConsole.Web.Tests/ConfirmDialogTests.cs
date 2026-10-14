using Bunit;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Sdk;
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

    private IRenderedComponent<ConfirmDialog> RenderDetailed(bool requireTyped, bool destructive = true, bool withDetails = true)
    {
        var mudDialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
        return Render<ConfirmDialog>(parameters => parameters
            .AddCascadingValue(mudDialogInstance)
            .Add(p => p.Verb, "Terminate")
            .Add(p => p.Target, "pid 48213")
            .Add(p => p.IsProd, true)
            .Add(p => p.Consequence, "The client connection will be dropped.")
            .Add(p => p.Details, withDetails
                ? new List<ConfirmationDetail> { new("pid", "48213"), new("user", "app_rw") }
                : null)
            .Add(p => p.AuditAction, "pg.backend.terminate")
            .Add(p => p.Destructive, destructive)
            .Add(p => p.Detailed, true)
            .Add(p => p.RequireTypedConfirmation, requireTyped));
    }

    private IRenderedComponent<ConfirmDialog> RenderCustom(Action<ComponentParameterCollectionBuilder<ConfirmDialog>> configure)
    {
        var mudDialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
        return Render<ConfirmDialog>(parameters =>
        {
            parameters.AddCascadingValue(mudDialogInstance)
                .Add(p => p.Verb, "Terminate")
                .Add(p => p.Target, "pid 48213");
            configure(parameters);
        });
    }

    [Fact]
    public void Minimal_detailed_request_shows_prod_marker_and_primary_verb_target_button()
    {
        var cut = RenderCustom(p => p.Add(x => x.Detailed, true).Add(x => x.IsProd, true).Add(x => x.Destructive, false));

        cut.FindAll(".confirm-prod-chip").Should().HaveCount(1);
        var btn = cut.Find("button.confirm-action");
        btn.TextContent.Trim().Should().Be("Terminate pid 48213");
        btn.ClassList.Should().Contain("mud-button-filled-primary");
        cut.Markup.Should().Contain("This cannot be undone.");
    }

    [Fact]
    public void Consequence_only_detailed_request_replaces_sentence()
    {
        var cut = RenderCustom(p => p.Add(x => x.Detailed, true).Add(x => x.Consequence, "Only this."));

        cut.Markup.Should().Contain("Only this.").And.NotContain("This cannot be undone.");
        cut.Find("button.confirm-action").TextContent.Trim().Should().Be("Terminate pid 48213");
    }

    [Fact]
    public void Not_detailed_ignores_prod_marker_and_keeps_error_verb_button()
    {
        var cut = RenderCustom(p => p.Add(x => x.IsProd, true));

        cut.FindAll(".confirm-prod-chip").Should().BeEmpty();
        var btn = cut.Find("button.confirm-action");
        btn.TextContent.Trim().Should().Be("Terminate");
        btn.ClassList.Should().Contain("mud-button-filled-error");
    }

    [Fact]
    public void Details_values_render_as_text_not_markup()
    {
        var cut = RenderCustom(p => p.Add(x => x.Detailed, true)
            .Add(x => x.Details, new List<ConfirmationDetail> { new("sql", "<b>x</b><script>alert(1)</script>") }));

        cut.FindAll("dd b").Should().BeEmpty();
        cut.FindAll("script").Should().BeEmpty();
        cut.Find("dd").TextContent.Should().Contain("<b>x</b><script>alert(1)</script>");
    }

    [Theory]
    [InlineData("PID 48213", true)]
    [InlineData("pid 48213 ", true)]
    [InlineData("pid 48213", false)]
    public void Typed_match_is_exact(string typed, bool stillDisabled)
    {
        var cut = RenderDetailed(requireTyped: true);

        cut.Find("input").Input(typed);

        cut.Find("button.confirm-action").HasAttribute("disabled").Should().Be(stillDisabled);
    }

    [Fact]
    public void Destructive_dialog_has_outline_class()
    {
        // MudDialog's own container isn't in the bUnit markup, so assert on the component parameters.
        var on = RenderDetailed(false, destructive: true).FindComponent<MudDialog>().Instance;
        on.Class.Should().Be("confirm-destructive");
        on.Style.Should().Contain("var(--mud-palette-error)");
        RenderDetailed(false, destructive: false).FindComponent<MudDialog>().Instance.Class.Should().BeNull();
    }

    [Fact]
    public void Details_value_cell_wraps_and_scrolls()
    {
        var style = RenderDetailed(false).Find("dd").GetAttribute("style");
        style.Should().Contain("white-space: pre-wrap").And.Contain("max-height: 12em").And.Contain("overflow: auto");
    }

    [Fact]
    public void Plain_dialog_has_no_detail_markup_and_keeps_default_sentence_and_verb_button()
    {
        var cut = RenderDialog(requireTyped: false);

        cut.FindAll("dl.confirm-details").Should().BeEmpty();
        cut.Markup.Should().Contain("This cannot be undone.");
        cut.Markup.Should().NotContain("Audited as");
        cut.Find("button.confirm-action").TextContent.Trim().Should().Be("Purge");
    }

    [Fact]
    public void Detailed_dialog_renders_consequence_rows_audit_caption_and_button_text()
    {
        var cut = RenderDetailed(requireTyped: false);

        cut.Markup.Should().Contain("The client connection will be dropped.");
        cut.Markup.Should().NotContain("This cannot be undone.");
        cut.Find("dl.confirm-details").TextContent.Should().Contain("pid").And.Contain("48213").And.Contain("app_rw");
        cut.FindAll("dl.confirm-details dt").Should().HaveCount(2);
        cut.Markup.Should().Contain("Audited as pg.backend.terminate with your name.");
        cut.FindAll(".confirm-prod-chip").Should().HaveCount(1);
        cut.Find("button.confirm-action").TextContent.Trim().Should().Be("Terminate pid 48213");
    }

    [Fact]
    public void Detailed_typed_confirmation_requires_exact_match()
    {
        var cut = RenderDetailed(requireTyped: true);

        cut.Markup.Should().Contain("Type").And.Contain("to confirm");
        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input").Input("pid 4821");
        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input").Input("pid 48213");
        cut.Find("button.confirm-action").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Destructive_uses_error_button_and_non_destructive_uses_primary()
    {
        RenderDetailed(false, destructive: true).Find("button.confirm-action").ClassList.Should().Contain("mud-button-filled-error");
        RenderDetailed(false, destructive: false).Find("button.confirm-action").ClassList.Should().Contain("mud-button-filled-primary");
    }
}
