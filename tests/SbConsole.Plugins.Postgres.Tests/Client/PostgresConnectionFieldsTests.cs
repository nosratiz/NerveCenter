using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PostgresConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public PostgresConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<PostgresConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<PostgresConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Fills_fields_from_the_initial_secret()
    {
        var secret = new PostgresConnectionSettings("db.local", 6543, "shop", "app", "pw", "VerifyFull", 9, 12, PgAccessMode.ReadWrite).ToSecretString();

        var cut = RenderFields(secret, out _);

        cut.Find(".pg-conn-host input").GetAttribute("value").Should().Be("db.local");
        cut.Find(".pg-conn-port input").GetAttribute("value").Should().Be("6543");
        cut.Find(".pg-conn-database input").GetAttribute("value").Should().Be("shop");
        cut.Find(".pg-conn-username input").GetAttribute("value").Should().Be("app");
        cut.Find(".pg-conn-password input").GetAttribute("type").Should().Be("password");
        cut.Find(".pg-conn-password input").GetAttribute("value").Should().Be("pw");
        cut.Find(".pg-conn-ssl input").GetAttribute("value").Should().Be("VerifyFull");
        cut.Find(".pg-conn-connect-timeout input").GetAttribute("value").Should().Be("9");
        cut.Find(".pg-conn-statement-timeout input").GetAttribute("value").Should().Be("12");
        cut.Find(".pg-conn-mode-readwrite input").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Typing_a_host_emits_a_secret()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".pg-conn-host input").Input("db.local");

        PostgresConnectionSettings.Parse(emitted.Last()).Should().Be(new PostgresConnectionSettings("db.local"));
    }

    [Fact]
    public void Clearing_the_host_shows_an_error_and_emits_nothing()
    {
        var cut = RenderFields(new PostgresConnectionSettings("db.local").ToSecretString(), out var emitted);

        cut.Find(".pg-conn-host input").Input("");

        emitted.Should().BeEmpty();
        cut.Find(".pg-conn-host .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("A host is required.");
    }

    [Fact]
    public void Switching_to_read_write_emits_Mode_ReadWrite()
    {
        var cut = RenderFields(new PostgresConnectionSettings("db.local").ToSecretString(), out var emitted);

        cut.Find(".pg-conn-mode-readwrite input").Click();

        PostgresConnectionSettings.Parse(emitted.Last()).Mode.Should().Be(PgAccessMode.ReadWrite);
    }

    [Fact]
    public void Unparsable_initial_secret_renders_defaults()
    {
        var cut = RenderFields("not a connection string ;;= =", out var emitted);

        cut.Find(".pg-conn-host input").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find(".pg-conn-port input").GetAttribute("value").Should().Be("5432");
        cut.FindAll(".mud-input-error").Should().BeEmpty();
        emitted.Should().BeEmpty();
    }

    [Fact]
    public void Out_of_range_timeouts_show_the_range_message_and_do_not_emit()
    {
        var cut = RenderFields(new PostgresConnectionSettings("db.local").ToSecretString(), out var emitted);

        cut.Find(".pg-conn-statement-timeout input").Input("3601");
        cut.Find(".pg-conn-connect-timeout input").Input("0");

        emitted.Should().BeEmpty();
        cut.Find(".pg-conn-statement-timeout .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("Must be between 1 and 3600 s.");
        cut.Find(".pg-conn-connect-timeout .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("Must be between 1 and 1024 s.");
    }

    [Fact]
    public void Invalid_port_is_flagged_and_not_emitted()
    {
        var cut = RenderFields(new PostgresConnectionSettings("db.local").ToSecretString(), out var emitted);

        cut.Find(".pg-conn-port input").Input("70000");

        emitted.Should().BeEmpty();
        cut.Find(".pg-conn-port .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("Port must be 1–65535.");
    }
}
