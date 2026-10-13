using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using TablesPage = SbConsole.Plugins.Sqlite.Pages.Tables;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Tables page DDL (mockups 1b / 1f / 1g, design spec §11.5).</summary>
public class TablesDdlPageTests : SqlitePageTestBase
{
    private const string WriteSecret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private const string NoBackupSecret = "Path=/data/reports.db;Mode=ReadWrite";

    private static readonly SchemaListing Listing = new(
    [
        new SchemaObject("events", SchemaObjectType.Table, "events", 41_200_000, null),
        new SchemaObject("sessions", SchemaObjectType.Table, "sessions", 2_418_902, null),
        new SchemaObject("v_daily_active", SchemaObjectType.View, "v_daily_active", null, null),
    ], SizesAvailable: false, FileBytes: 1_400_000_000);

    private static readonly SchemaListing AfterCreate = Listing with
    {
        Objects = [.. Listing.Objects, new SchemaObject("report_runs", SchemaObjectType.Table, "report_runs", null, null)],
    };

    private static TableDetail Detail(string name, SchemaObjectType type = SchemaObjectType.Table) =>
        new(name, type, $"CREATE TABLE {name}(id INTEGER PRIMARY KEY)", false, null,
            [new ColumnInfo("id", "INTEGER", false, null, 1, false, false)], [], []);

    private static readonly DropImpact EventsImpact = new("events", 41_200_000, 3,
    [
        new DropReference("event_props", ["event_id"], "CASCADE"),
        new DropReference("audit_marks", ["event_id"], "RESTRICT"),
    ]);

    private string _secret = WriteSecret;

    public TablesDdlPageTests() => Connection(readWrite: true, prod: false);

    /// <summary>Re-arranges the one connection: its mode (Summary + secret), prod flag and BackupDir.</summary>
    private void Connection(bool readWrite, bool prod, bool backups = true)
    {
        _secret = !readWrite ? Secret : backups ? WriteSecret : NoBackupSecret;
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", prod ? ["prod"] : [], prod, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = readWrite ? "read-write" : "read-only" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(_secret);
        Operations.ListSchemaAsync(_secret, Arg.Any<CancellationToken>()).Returns(Listing);
        foreach (var name in new[] { "events", "sessions", "report_runs", "events_archive" })
        {
            Operations.GetTableDetailAsync(_secret, name, Arg.Any<CancellationToken>()).Returns(Detail(name));
        }

        Operations.GetTableDetailAsync(_secret, "v_daily_active", Arg.Any<CancellationToken>()).Returns(Detail("v_daily_active", SchemaObjectType.View));
        Operations.SampleColumnsAsync(_secret, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ColumnSample>());
        Operations.GetDropImpactAsync(_secret, "events", Arg.Any<CancellationToken>()).Returns(EventsImpact);
    }

    private IRenderedComponent<TablesPage> RenderPage()
    {
        var cut = Render<TablesPage>();
        cut.WaitForElement(".sqlite-table-name");
        return cut;
    }

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    // ---- enabled / disabled ----

    [Fact]
    public void Read_only_connection_disables_create_rename_and_drop_with_the_reason_but_not_query()
    {
        Connection(readWrite: false, prod: false);
        var cut = RenderPage();

        cut.Find(".sqlite-tables-create").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-table-rename").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-table-drop").HasAttribute("disabled").Should().BeTrue();
        foreach (var wrap in new[] { ".sqlite-tables-create-wrap", ".sqlite-table-rename-wrap", ".sqlite-table-drop-wrap" })
        {
            cut.Find(wrap).GetAttribute("title").Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
        }

        cut.Find(".sqlite-table-query").GetAttribute("href").Should()
            .Be($"/p/sqlite/query?connectionId={ConnectionId}&sql={System.Uri.EscapeDataString("SELECT * FROM \"events\" LIMIT 100")}");
    }

    [Fact]
    public void Read_write_connection_enables_the_ddl_buttons()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-tables-create").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-table-rename").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-table-drop").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-table-rename-wrap").HasAttribute("title").Should().BeFalse();
    }

    [Fact]
    public async Task Views_get_query_but_no_rename_or_drop()
    {
        var cut = RenderPage();

        await cut.Find("[data-name='v_daily_active']").ClickAsync(new());
        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("v_daily_active"));

        cut.FindAll(".sqlite-table-rename").Should().BeEmpty();
        cut.FindAll(".sqlite-table-drop").Should().BeEmpty();
        cut.Find(".sqlite-table-query").GetAttribute("href").Should().Contain(System.Uri.EscapeDataString("SELECT * FROM \"v_daily_active\" LIMIT 100"));
    }

    [Fact]
    public async Task The_data_tab_links_the_preview_sql_to_the_query_page()
    {
        var preview = new RowPage("SELECT * FROM \"events\" ORDER BY rowid DESC LIMIT 50", ["id"], []);
        Operations.PreviewRowsAsync(_secret, "events", RowPage.PreviewLimit, Arg.Any<CancellationToken>()).Returns(preview);
        var cut = RenderPage();

        await cut.Find(".sqlite-tab-data").ClickAsync(new());

        cut.WaitForElement(".sqlite-preview-open-query").GetAttribute("href").Should()
            .Be($"/p/sqlite/query?connectionId={ConnectionId}&sql={System.Uri.EscapeDataString(preview.Sql)}");
    }

    // ---- create ----

    [Fact]
    public async Task Create_opens_a_dialog_with_a_template_then_creates_reloads_and_selects_the_new_table()
    {
        var cut = RenderPage();
        await cut.Find(".sqlite-tables-create").ClickAsync(new());
        cut.Find(".sqlite-create-ddl").GetAttribute("value").Should().StartWith("CREATE TABLE new_table (");

        const string ddl = "CREATE TABLE report_runs(id INTEGER PRIMARY KEY)";
        Operations.CreateTableAsync(_secret, ddl, Arg.Any<CancellationToken>()).Returns("report_runs");
        Operations.ListSchemaAsync(_secret, Arg.Any<CancellationToken>()).Returns(AfterCreate);
        await cut.Find(".sqlite-create-ddl").InputAsync(new ChangeEventArgs { Value = ddl });
        await cut.Find(".sqlite-create-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("report_runs"));
        cut.FindAll(".sqlite-create-dialog").Should().BeEmpty();
        cut.Find(".sqlite-tables-notice").TextContent.Should().Be("Created report_runs.");
        Uri.Should().Contain("table=report_runs");
        await Audit.Received(1).RecordAsync("sqlite.table.create", "sqlite-reports/report_runs", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_create_keeps_the_dialog_open_with_the_error()
    {
        Operations.CreateTableAsync(_secret, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new SqliteDdlRefusedException("Create table takes exactly one CREATE TABLE statement — use Query for anything else."));
        var cut = RenderPage();
        await cut.Find(".sqlite-tables-create").ClickAsync(new());

        await cut.Find(".sqlite-create-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-dialog-error").TextContent.Should().Contain("exactly one CREATE TABLE"));
        cut.FindAll(".sqlite-create-dialog").Should().HaveCount(1);
    }

    [Fact]
    public async Task Cancel_closes_the_dialog_and_runs_nothing()
    {
        var cut = RenderPage();
        await cut.Find(".sqlite-tables-create").ClickAsync(new());

        await cut.Find(".sqlite-dialog-cancel").ClickAsync(new());

        cut.FindAll(".sqlite-create-dialog").Should().BeEmpty();
        await Operations.DidNotReceive().CreateTableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- rename ----

    [Fact]
    public async Task Rename_on_a_non_prod_connection_renames_without_confirming_and_selects_the_new_name()
    {
        var cut = RenderPage();
        await cut.Find(".sqlite-table-rename").ClickAsync(new());
        cut.Find(".sqlite-rename-name").GetAttribute("value").Should().Be("events");

        await cut.Find(".sqlite-rename-name").InputAsync(new ChangeEventArgs { Value = "events_archive" });
        Operations.ListSchemaAsync(_secret, Arg.Any<CancellationToken>()).Returns(Listing with
        {
            Objects = [new SchemaObject("events_archive", SchemaObjectType.Table, "events_archive", null, null), Listing.Objects[1]],
        });
        await cut.Find(".sqlite-rename-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("events_archive"));
        await Operations.Received(1).RenameTableAsync(_secret, "events", "events_archive", Arg.Any<CancellationToken>());
        await Confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        cut.Find(".sqlite-tables-notice").TextContent.Should().Be("Renamed events to events_archive.");
    }

    [Fact]
    public async Task Rename_on_prod_asks_for_the_typed_old_name_and_a_declined_confirm_renames_nothing()
    {
        Connection(readWrite: true, prod: true);
        Confirmation.ConfirmAsync("Rename table", "events", true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();
        await cut.Find(".sqlite-table-rename").ClickAsync(new());
        await cut.Find(".sqlite-rename-name").InputAsync(new ChangeEventArgs { Value = "events_archive" });

        await cut.Find(".sqlite-rename-submit").ClickAsync(new());

        await Confirmation.Received(1).ConfirmAsync("Rename table", "events", true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().RenameTableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-rename-dialog").Should().HaveCount(1);
    }

    // ---- drop ----

    [Fact]
    public async Task Drop_dialog_shows_rows_indexes_references_consequences_and_the_backup_checkbox()
    {
        var cut = RenderPage();

        await cut.Find(".sqlite-table-drop").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-drop-summary").TextContent.Should().Contain("41.2M rows ≈").And.Contain("3 indexes"));
        cut.FindAll(".sqlite-drop-reference").Select(r => r.TextContent.Trim().Replace("\n", " ")).Should().SatisfyRespectively(
            first => first.Should().Contain("event_props (event_id)").And.Contain("ON DELETE CASCADE").And.Contain("rows there are deleted too"),
            second => second.Should().Contain("audit_marks (event_id)").And.Contain("the drop fails if any rows reference it"));
        cut.Find(".sqlite-drop-target").TextContent.Should().Be("sqlite-reports / events");
        cut.Find(".sqlite-drop-backup-input").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_backup_directory_the_dialog_has_no_backup_checkbox()
    {
        Connection(readWrite: true, prod: false, backups: false);
        var cut = RenderPage();

        await cut.Find(".sqlite-table-drop").ClickAsync(new());

        cut.WaitForElement(".sqlite-drop-summary");
        cut.FindAll(".sqlite-drop-backup").Should().BeEmpty();
        cut.Find(".sqlite-drop-no-backups").TextContent.Should().Contain("Enable backups to");
    }

    [Fact]
    public async Task Drop_confirms_with_IsProd_and_a_declined_confirm_drops_nothing()
    {
        Connection(readWrite: true, prod: true);
        Confirmation.ConfirmAsync("Drop table", "events", true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();
        await cut.Find(".sqlite-table-drop").ClickAsync(new());
        cut.WaitForElement(".sqlite-drop-summary").TextContent.Should().Contain("on a prod-tagged file");

        await cut.Find(".sqlite-drop-submit").ClickAsync(new());

        await Confirmation.Received(1).ConfirmAsync("Drop table", "events", true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().DropTableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().CreateBackupAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drop_with_backup_first_backs_up_then_drops_reloads_and_clears_the_selection()
    {
        Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new BackupCreated("reports-20261013-094051.db", 4096, TimeSpan.FromSeconds(1)));
        Operations.VerifyBackupAsync(_secret, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BackupVerification(true, ["ok"], new string('a', 64), new SqliteFileFingerprint(4096, 2, null, new string('f', 64), [])));
        var cut = RenderPage();
        await cut.Find(".sqlite-table-drop").ClickAsync(new());
        cut.WaitForElement(".sqlite-drop-summary");
        Operations.ListSchemaAsync(_secret, Arg.Any<CancellationToken>()).Returns(Listing with { Objects = [Listing.Objects[1], Listing.Objects[2]] });

        await cut.Find(".sqlite-drop-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-tables-notice").TextContent.Should().Be("Backed up, then dropped events."));
        await Confirmation.Received(1).ConfirmAsync("Drop table", "events", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            Operations.DropTableAsync(_secret, "events", Arg.Any<CancellationToken>());
        });
        (await BackupHistoryStore.ReadAsync(Store, ConnectionId)).Should().ContainSingle().Which.Actor.Should().Be("admin");
        cut.FindAll(".sqlite-table-name").Should().BeEmpty();
        cut.Find(".sqlite-table-none").Should().NotBeNull();
        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name")).Should().Equal("sessions", "v_daily_active");
        Uri.Should().NotContain("table=");
    }

    [Fact]
    public async Task Unchecking_backup_first_drops_without_a_backup()
    {
        var cut = RenderPage();
        await cut.Find(".sqlite-table-drop").ClickAsync(new());
        cut.WaitForElement(".sqlite-drop-summary");

        await cut.Find(".sqlite-drop-backup-input").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find(".sqlite-drop-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-tables-notice").TextContent.Should().Be("Dropped events."));
        await Operations.DidNotReceive().CreateBackupAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_backup_keeps_the_table_and_shows_why_in_the_dialog()
    {
        Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<BackupCreated>>(_ => throw new SqliteException("disk full", 13));
        var cut = RenderPage();
        await cut.Find(".sqlite-table-drop").ClickAsync(new());
        cut.WaitForElement(".sqlite-drop-summary");

        await cut.Find(".sqlite-drop-submit").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-dialog-error").TextContent.Should()
            .Be("The backup failed, so nothing was dropped: The disk or volume is full."));
        await Operations.DidNotReceive().DropTableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        cut.Find(".sqlite-table-name").TextContent.Should().Be("events");
    }

    // ---- empty state (1g) ----

    [Fact]
    public void An_empty_writable_file_offers_create_table_and_run_script()
    {
        Operations.ListSchemaAsync(_secret, Arg.Any<CancellationToken>()).Returns(new SchemaListing([], false, 0));
        var cut = Render<TablesPage>();

        cut.WaitForElement(".sqlite-tables-empty");
        cut.Find(".sqlite-tables-empty-size").TextContent.Should().Contain("The file is 0 B — SQLite creates pages on first write.");
        cut.Find(".sqlite-tables-empty-script").GetAttribute("href").Should().Be($"/p/sqlite/query?connectionId={ConnectionId}");
        cut.Find(".sqlite-tables-empty-create").Click();
        cut.Find(".sqlite-create-dialog").Should().NotBeNull();
    }

    [Fact]
    public void An_empty_read_only_file_explains_that_creating_needs_read_write()
    {
        Connection(readWrite: false, prod: false);
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(new SchemaListing([], false, 0));
        var cut = Render<TablesPage>();

        cut.WaitForElement(".sqlite-tables-empty");
        cut.FindAll(".sqlite-tables-empty-create").Should().BeEmpty();
        cut.Find(".sqlite-tables-empty-readonly").TextContent.Should().Contain("Read-write");
    }
}
