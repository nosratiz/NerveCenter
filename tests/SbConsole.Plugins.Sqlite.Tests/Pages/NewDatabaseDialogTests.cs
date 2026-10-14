using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Components;
using SbConsole.Plugins.Sqlite.Databases;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Design spec §13: the inline New database dialog.</summary>
public class NewDatabaseDialogTests : SqlitePageTestBase
{
    private readonly Guid _newId = Guid.NewGuid();
    private readonly List<DatabaseCreated> _created = [];
    private int _cancelled;

    public NewDatabaseDialogTests()
    {
        Operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new NewDatabaseCreated(
                Path.Combine(SqliteRoot, NewDatabaseRules.WithExtension(call.Arg<NewDatabaseRequest>().FileName)), "/x", "x.db", 0));
        Creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionCreateResult.Created(_newId));
    }

    private IRenderedComponent<NewDatabaseDialog> RenderDialog(params string[] existingNames) =>
        Render<NewDatabaseDialog>(p => p
            .Add(x => x.ExistingNames, existingNames)
            .Add(x => x.Created, EventCallback.Factory.Create<DatabaseCreated>(this, _created.Add))
            .Add(x => x.Cancelled, EventCallback.Factory.Create(this, () => _cancelled++)));

    private static async Task SubmitAsync(IRenderedComponent<NewDatabaseDialog> cut)
    {
        await cut.Find(".sqlite-newdb-submit").ClickAsync(new());
        await SettleAsync(cut);
    }

    [Fact]
    public void Offers_the_configured_folders_and_the_spec_defaults()
    {
        PathPolicy.AllowedRoots.Returns(["/sqlite", "/data/sqlite"]);

        var cut = RenderDialog();

        cut.FindAll(".sqlite-newdb-folder option").Select(o => o.TextContent).Should().Equal("/sqlite", "/data/sqlite");
        cut.Find(".sqlite-newdb-folder").GetAttribute("value").Should().Be("/sqlite");
        cut.Find(".sqlite-newdb-journal-wal").ClassList.Should().Contain("sql-is-active");
        cut.Find(".sqlite-newdb-template-empty").ClassList.Should().Contain("sql-is-active");
        cut.Find(".sqlite-newdb-mode-readwrite").ClassList.Should().Contain("sql-is-active");
        cut.FindAll(".sqlite-newdb-script").Should().BeEmpty();
    }

    [Fact]
    public void The_connection_name_follows_the_file_name_until_it_is_edited()
    {
        var cut = RenderDialog();

        cut.Find(".sqlite-newdb-file").Input("Team Tasks");
        cut.Find(".sqlite-newdb-name").GetAttribute("value").Should().Be("sqlite-Team Tasks");
        cut.Find(".sqlite-newdb-target").TextContent.Should().Be("/sqlite/Team Tasks.db");

        cut.Find(".sqlite-newdb-name").Input("tasks");
        cut.Find(".sqlite-newdb-file").Input("other.sqlite");

        cut.Find(".sqlite-newdb-name").GetAttribute("value").Should().Be("tasks");
        cut.Find(".sqlite-newdb-target").TextContent.Should().Be("/sqlite/other.sqlite");
    }

    [Fact]
    public async Task Invalid_fields_show_their_own_errors_and_nothing_is_created()
    {
        var cut = RenderDialog("sqlite-reports");

        cut.Find(".sqlite-newdb-file").Input("../evil");
        cut.Find(".sqlite-newdb-file-error").TextContent.Should().Contain("'/'");
        cut.Find(".sqlite-newdb-file").Input("reports");
        cut.Find(".sqlite-newdb-name-error").TextContent.Should().Contain("already exists");
        cut.Find(".sqlite-newdb-subfolder").Input("../up");
        cut.Find(".sqlite-newdb-subfolder-error").TextContent.Should().Contain("..");

        await SubmitAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task Submitting_with_no_file_name_flags_the_field()
    {
        var cut = RenderDialog();
        cut.FindAll(".sqlite-newdb-file-error").Should().BeEmpty("no error before the first attempt");

        await SubmitAsync(cut);

        cut.Find(".sqlite-newdb-file-error").TextContent.Should().Be("Type a file name.");
        await Operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task The_script_template_shows_an_editor_that_must_not_be_empty()
    {
        var cut = RenderDialog();
        cut.Find(".sqlite-newdb-file").Input("mine");
        await cut.Find(".sqlite-newdb-template-script").ClickAsync(new());

        await SubmitAsync(cut);

        cut.Find(".sqlite-newdb-script-error").TextContent.Should().Contain("script");
        await Operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);

        cut.Find(".sqlite-newdb-script").Input("CREATE TABLE notes(id INTEGER PRIMARY KEY);");
        await SubmitAsync(cut);

        await Operations.Received(1).CreateDatabaseAsync(
            Arg.Is<NewDatabaseRequest>(r => r.Template == NewDatabaseTemplate.Script && r.Script == "CREATE TABLE notes(id INTEGER PRIMARY KEY);"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_with_every_field_and_raises_Created()
    {
        Operations.FindBackupsDirAsync(SqliteRoot, "team", Arg.Any<CancellationToken>()).Returns("/sqlite/team/backups");
        var cut = RenderDialog();
        cut.Find(".sqlite-newdb-subfolder").Input("team");
        await cut.Find(".sqlite-newdb-subfolder").ChangeAsync(new ChangeEventArgs { Value = "team" });
        await SettleAsync(cut);
        cut.Find(".sqlite-newdb-file").Input("tasks");
        await cut.Find(".sqlite-newdb-journal-delete").ClickAsync(new());
        await cut.Find(".sqlite-newdb-template-starter").ClickAsync(new());
        await cut.Find(".sqlite-newdb-mode-readonly").ClickAsync(new());
        cut.Find(".sqlite-newdb-tags").Input("prod, team");
        cut.Find(".sqlite-newdb-backups-dir").TextContent.Should().Be("/sqlite/team/backups");
        await cut.Find(".sqlite-newdb-backups input").ChangeAsync(new ChangeEventArgs { Value = true });

        await SubmitAsync(cut);

        await Operations.Received(1).CreateDatabaseAsync(
            new NewDatabaseRequest(SqliteRoot, "team", "tasks", NewDatabaseJournal.Delete, NewDatabaseTemplate.Starter, null), Arg.Any<CancellationToken>());
        await Creator.Received(1).CreateAsync("sqlite-tasks", "sqlite",
            Arg.Is<string>(s => s.Contains("Mode=ReadOnly") && s.Contains("BackupDir=/sqlite/team/backups")),
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "prod", "team" })), Arg.Any<CancellationToken>());
        _created.Should().ContainSingle().Which.ConnectionId.Should().Be(_newId);
    }

    [Fact]
    public async Task No_backups_directory_means_no_backups_offer()
    {
        var cut = RenderDialog();
        await SettleAsync(cut);

        cut.FindAll(".sqlite-newdb-backups").Should().BeEmpty();
        cut.Find(".sqlite-newdb-no-backups").TextContent.Should().Contain("backups");
    }

    [Fact]
    public async Task A_failure_shows_the_friendly_error_and_keeps_the_dialog_open()
    {
        Operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteNewDatabaseRefusedException("tasks.db already exists in that folder — SbConsole never overwrites a file."));
        var cut = RenderDialog();
        cut.Find(".sqlite-newdb-file").Input("tasks");

        await SubmitAsync(cut);

        cut.Find(".sqlite-newdb-error").TextContent.Should().Contain("never overwrites");
        cut.Find(".sqlite-newdb-submit").HasAttribute("disabled").Should().BeFalse();
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task While_creating_the_dialog_is_busy()
    {
        var pending = new TaskCompletionSource<NewDatabaseCreated>();
        Operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = RenderDialog();
        cut.Find(".sqlite-newdb-file").Input("tasks");

        var click = cut.Find(".sqlite-newdb-submit").ClickAsync(new());

        cut.Find(".sqlite-newdb-submit").TextContent.Should().Be("Creating…");
        cut.Find(".sqlite-newdb-submit").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-newdb-cancel").HasAttribute("disabled").Should().BeTrue();
        pending.SetResult(new NewDatabaseCreated("/sqlite/tasks.db", "/sqlite/tasks.db", "tasks.db", 0));
        await click;
        _created.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_raises_Cancelled()
    {
        var cut = RenderDialog();

        await cut.Find(".sqlite-newdb-cancel").ClickAsync(new());

        _cancelled.Should().Be(1);
    }

    [Fact]
    public void With_no_roots_configured_it_explains_instead_of_offering_a_form()
    {
        PathPolicy.AllowedRoots.Returns([]);

        var cut = RenderDialog();

        cut.Find(".sqlite-newdb-no-roots").TextContent.Should().Contain("SBC_SQLITE_ROOTS");
        cut.FindAll(".sqlite-newdb-file").Should().BeEmpty();
        cut.FindAll(".sqlite-newdb-submit").Should().BeEmpty();
    }
}
