using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public AwsConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AwsConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<AwsConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Defaults_to_access_keys_mode_when_no_secret_is_supplied()
    {
        var cut = RenderFields(null, out _);

        cut.Find("input#aws-access-key-id").Should().NotBeNull();
    }

    [Fact]
    public void Hydrates_non_secret_fields_from_the_initial_secret()
    {
        var cut = RenderFields("mode=assume-role;region=eu-west-1;roleArn=arn:aws:iam::123456789012:role/Reader", out _);

        cut.Find("input#aws-role-arn").GetAttribute("value").Should().Be("arn:aws:iam::123456789012:role/Reader");
    }

    [Fact]
    public void Switching_to_default_chain_hides_every_credential_field()
    {
        var cut = RenderFields("mode=access-keys;region=eu-west-1", out _);

        // The brief's original guess (`div.mud-toggle-item[data-value='...']`) doesn't match the
        // real rendered markup: MudToggleItem<T> renders as a MudButton (an actual <button>
        // element via MudElement, not a <div>), and MudToggleItem exposes no built-in
        // "data-value"-shaped attribute reflecting its Value. AwsConnectionFields.razor adds an
        // explicit `data-value="@mode"` splat attribute on each MudToggleItem (verified via
        // ilspycmd against the installed MudBlazor 9.9.0 DLL that MudToggleItem/MudButton forward
        // unmatched attributes down to the rendered <button>) so tests have a stable hook.
        cut.Find("button.mud-toggle-item[data-value='default-chain']").Click();

        cut.FindAll("input#aws-access-key-id").Should().BeEmpty();
        cut.FindAll("input#aws-role-arn").Should().BeEmpty();
    }

    [Fact]
    public void Editing_a_field_emits_a_serialized_secret_via_SecretChanged()
    {
        var cut = RenderFields("mode=access-keys;region=eu-west-1", out var emitted);

        cut.Find("input#aws-access-key-id").Input("AKIA123");

        emitted.Should().ContainSingle(s => s.Contains("accessKeyId=AKIA123") && s.Contains("mode=access-keys") && s.Contains("region=eu-west-1"));
    }

    [Fact]
    public void Advanced_section_starts_collapsed_unless_an_endpoint_was_already_set()
    {
        var collapsed = RenderFields("mode=access-keys;region=eu-west-1", out _);
        collapsed.FindAll("input#aws-endpoint").Should().BeEmpty();

        var expanded = RenderFields("mode=access-keys;region=eu-west-1;endpoint=http://localhost:4566", out _);
        expanded.FindAll("input#aws-endpoint").Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-real-mode")]
    public void OnModeChanged_ignores_an_empty_or_unrecognized_value(string? invalidMode)
    {
        // MudToggleGroup<T> defaults to SelectionMode.SingleSelection, under which clicking a
        // toggle item -- even the already-selected one -- always assigns that item's value and
        // never clears it; there's no click sequence that reaches ValueChanged with an
        // empty/invalid string today. OnModeChanged still guards against one (see the comment
        // above the toggle group in AwsConnectionFields.razor), so we drive that private callback
        // directly via reflection, matching bUnit's own recommended approach for logic that isn't
        // reachable through a rendered interaction.
        var cut = RenderFields("mode=assume-role;region=eu-west-1", out var emitted);

        var method = typeof(AwsConnectionFields).GetMethod("OnModeChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.Should().NotBeNull();

        cut.InvokeAsync(() => (Task)method!.Invoke(cut.Instance, [invalidMode])!);

        cut.Find("input#aws-role-arn").Should().NotBeNull();
        emitted.Should().NotContain(s => s.Contains("mode=;") || s.EndsWith("mode=") || s.Contains($"mode={invalidMode}"));
    }

    [Fact]
    public void Warns_when_a_custom_endpoint_is_set_on_a_prod_connection()
    {
        var cut = Render<AwsConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, "mode=access-keys;region=eu-west-1;endpoint=http://localhost:4566")
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, _ => { })));

        cut.Markup.Should().Contain("almost always a mistake");
    }
}
