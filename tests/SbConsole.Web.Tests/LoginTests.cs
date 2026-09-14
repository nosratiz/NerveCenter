using Bunit;
using FluentAssertions;
using MudBlazor.Services;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class LoginTests : BunitContext
{
    public LoginTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Card_is_flat_with_no_shadow()
    {
        var cut = Render<Login>();

        var paper = cut.Find(".mud-paper");
        paper.ClassList.Should().Contain("mud-elevation-0");
        paper.ClassList.Should().NotContain("mud-elevation-1");
    }

    [Fact]
    public void Password_field_and_submit_button_keep_their_form_wiring()
    {
        var cut = Render<Login>();

        var form = cut.Find("form");
        form.GetAttribute("method").Should().Be("post");
        form.GetAttribute("action").Should().Be("/auth/login");

        var input = cut.Find("input[name=password]");
        input.GetAttribute("type").Should().Be("password");
        input.ClassList.Should().Contain("login-password");

        var button = cut.Find("button[type=submit]");
        button.ClassList.Should().Contain("login-submit");
    }
}
