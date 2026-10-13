using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SqliteConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SqliteConnectionFields> RenderFields(string? initialSecret, out List<string> emitted)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<SqliteConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, true)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Hydrates_the_fields_from_the_initial_secret()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db", BusyTimeoutMs: 2500, StatementTimeoutS: 12).ToSecretString(), out _);

        cut.Find(".sqlite-conn-path input").GetAttribute("value").Should().Be("/data/reports.db");
        cut.Find(".sqlite-conn-busy input").GetAttribute("value").Should().Be("2500");
        cut.Find(".sqlite-conn-statement input").GetAttribute("value").Should().Be("12");
    }

    [Fact]
    public void Editing_the_path_emits_a_full_secret_with_defaults()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".sqlite-conn-path input").Input("/data/new.db");

        SqliteConnectionSettings.Parse(emitted.Last()).Should().Be(new SqliteConnectionSettings("/data/new.db"));
    }

    [Fact]
    public void Editing_a_timeout_emits_it()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-statement input").Input("45");

        SqliteConnectionSettings.Parse(emitted.Last()).StatementTimeoutS.Should().Be(45);
    }

    [Fact]
    public void A_path_with_a_semicolon_is_flagged_and_not_emitted()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find(".sqlite-conn-path input").Input("/data/a;b.db");

        emitted.Should().BeEmpty();
        cut.Find(".sqlite-conn-path-error").TextContent.Should().Contain("can't contain ';'");
    }

    [Fact]
    public void Read_write_is_shown_but_disabled_in_phase_one()
    {
        var cut = RenderFields(null, out _);

        cut.Find(".sqlite-conn-mode-readwrite input").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-conn-mode-readwrite").TextContent.Should().Contain("not available yet");
    }
}
