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
        cut.Find(".sqlite-conn-path .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("can't contain ';'");
        cut.Find(".sqlite-conn-path input").GetAttribute("aria-invalid").Should().Be("true");
    }

    [Fact]
    public void A_fresh_empty_form_shows_no_errors()
    {
        var cut = RenderFields(null, out _);

        cut.FindAll(".mud-input-error").Should().BeEmpty();
    }

    [Fact]
    public void A_good_path_then_a_bad_one_keeps_the_last_good_secret_and_shows_the_error()
    {
        var cut = RenderFields(null, out var emitted);
        cut.Find(".sqlite-conn-path input").Input("/data/good.db");

        cut.Find(".sqlite-conn-path input").Input("");
        cut.Find(".sqlite-conn-path .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("A file path is required.");

        cut.Find(".sqlite-conn-path input").Input("/data/good.db;");
        cut.Find(".sqlite-conn-path .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("can't contain ';'");

        SqliteConnectionSettings.Parse(emitted.Last()).Path.Should().Be("/data/good.db");
        emitted.Should().HaveCount(1);
    }

    [Fact]
    public void A_zero_statement_timeout_is_not_emitted_and_shows_the_error()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);
        cut.Find(".sqlite-conn-statement input").Input("45");
        var countBefore = emitted.Count;

        cut.Find(".sqlite-conn-statement input").Input("0");

        emitted.Should().HaveCount(countBefore);
        SqliteConnectionSettings.Parse(emitted.Last()).StatementTimeoutS.Should().Be(45);
        cut.Find(".sqlite-conn-statement .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("Must be at least 1 s.");
    }

    [Fact]
    public void A_zero_busy_timeout_shows_the_error()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-busy input").Input("0");

        emitted.Should().BeEmpty();
        cut.Find(".sqlite-conn-busy .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("Must be at least 1 ms.");
    }

    [Fact]
    public void An_unparsable_initial_secret_renders_the_defaults_without_throwing()
    {
        var cut = RenderFields("garbage", out _);

        cut.Find(".sqlite-conn-path input").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find(".sqlite-conn-busy input").GetAttribute("value").Should().Be("5000");
        cut.Find(".sqlite-conn-statement input").GetAttribute("value").Should().Be("30");
    }

    [Fact]
    public void Read_write_can_be_chosen_and_is_emitted()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-mode-readwrite input").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-conn-mode-readwrite").TextContent.Should().NotContain("not available");
        cut.Find(".sqlite-conn-mode-readwrite input").Click();

        SqliteConnectionSettings.Parse(emitted.Last()).Mode.Should().Be(SqliteAccessMode.ReadWrite);
    }

    [Fact]
    public void A_read_write_secret_hydrates_the_mode()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db", SqliteAccessMode.ReadWrite).ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-busy input").Input("2500");

        SqliteConnectionSettings.Parse(emitted.Last()).Mode.Should().Be(SqliteAccessMode.ReadWrite);
    }

    [Fact]
    public void The_backup_directory_field_hydrates_and_explains_itself()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db", BackupDir: "/data/backups").ToSecretString(), out _);

        cut.Find(".sqlite-conn-backupdir input").GetAttribute("value").Should().Be("/data/backups");
        cut.Find(".sqlite-conn-backupdir").TextContent.Should().Contain("Enable backups to");
        cut.Find(".sqlite-conn-backupdir").TextContent.Should()
            .Contain("Directory inside the SbConsole container, under SBC_SQLITE_ROOTS; leave empty to disable backups");
    }

    [Fact]
    public void Editing_the_backup_directory_emits_it_and_clearing_it_removes_it()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-backupdir input").Input("/data/backups");
        SqliteConnectionSettings.Parse(emitted.Last()).BackupDir.Should().Be("/data/backups");

        cut.Find(".sqlite-conn-backupdir input").Input("");
        SqliteConnectionSettings.Parse(emitted.Last()).BackupDir.Should().BeNull();
        emitted.Last().Should().NotContain("BackupDir");
    }

    [Fact]
    public void A_backup_directory_with_a_semicolon_is_flagged_and_not_emitted()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-backupdir input").Input("/data/a;b");

        emitted.Should().BeEmpty();
        cut.Find(".sqlite-conn-backupdir .mud-input-helper-text.mud-input-error").TextContent.Should().Contain("can't contain ';'");
    }

    [Fact]
    public void Editing_another_field_keeps_the_backup_directory()
    {
        var cut = RenderFields(new SqliteConnectionSettings("/data/reports.db", BackupDir: "/data/backups").ToSecretString(), out var emitted);

        cut.Find(".sqlite-conn-busy input").Input("2500");

        SqliteConnectionSettings.Parse(emitted.Last()).BackupDir.Should().Be("/data/backups");
    }
}
