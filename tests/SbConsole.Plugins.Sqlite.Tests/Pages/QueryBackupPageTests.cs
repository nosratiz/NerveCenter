using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Query page opened on a backup (Backups "Open read-only", design spec §11.7): ?backup=&lt;file name&gt;.</summary>
public class QueryBackupPageTests : SqlitePageTestBase
{
    private const string Sql = "SELECT name FROM tenants";
    private const string BackupFile = "reports-20261012-020000.db";

    private static readonly QueryResult Tenants = new(
        [new QueryColumn("name", "TEXT", false)],
        [[new CellValue(CellKind.Text, "acme-uk")]],
        TotalRows: 1, TotalCapped: false, StatementCount: 1, ResultStatement: 1, Duration: TimeSpan.FromMilliseconds(4));

    public QueryBackupPageTests()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", [],
                Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-write" }),
        ]);
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Tenants);
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Query> RenderOnBackup()
    {
        Nav.NavigateTo($"/p/sqlite/query?connectionId={ConnectionId}&backup={Uri.EscapeDataString(BackupFile)}&sql={Uri.EscapeDataString(Sql)}");
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Query>();
        cut.WaitForElement(".sqlite-query-backup-banner");
        cut.WaitForAssertion(() => cut.Find(".sqlite-query-meta").TextContent.Should().Contain("statement timeout"));
        return cut;
    }

    [Fact]
    public void The_banner_names_the_backup_and_the_meta_says_it_is_opened_read_only()
    {
        var cut = RenderOnBackup();

        cut.Find(".sqlite-query-backup-banner").TextContent.Should().Contain("Querying backup").And.Contain(BackupFile).And.Contain("read-only");
        cut.Find(".sqlite-query-backup-file").TextContent.Should().Be(BackupFile);
        cut.Find(".sqlite-query-meta").TextContent.Should().StartWith($"backup {BackupFile} · opened with mode=ro");
    }

    [Fact]
    public void Write_mode_is_unavailable_on_a_backup_even_for_a_read_write_connection()
    {
        var cut = RenderOnBackup();

        cut.Find(".sqlite-query-mode-write").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-query-mode-write-wrap").GetAttribute("title").Should().Contain("Backups open read-only");
    }

    [Fact]
    public async Task Run_and_explain_go_to_the_backup_file()
    {
        Operations.ExplainQueryAsync(Secret, Sql, BackupFile, Arg.Any<CancellationToken>()).Returns(new ExplainPlan(Sql, 1, [new ExplainNode(2, 0, "SCAN tenants")]));
        var cut = RenderOnBackup();

        cut.Find(".sqlite-query-run").Click();
        cut.WaitForElement(".sqlite-query-grid");
        cut.Find(".sqlite-query-explain").Click();
        cut.WaitForElement(".sqlite-query-plan-node");

        await Operations.Received(1).RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, BackupFile, Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, null, Arg.Any<CancellationToken>());
        await Operations.Received(1).ExplainQueryAsync(Secret, Sql, BackupFile, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Export_reads_the_backup_file()
    {
        Operations.ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, BackupFile, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("query.csv", Encoding.UTF8.GetBytes("name\r\nacme-uk\r\n"), 1, false));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderOnBackup();
        cut.Find(".sqlite-query-run").Click();
        cut.WaitForElement(".sqlite-query-grid");

        cut.Find(".sqlite-query-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-export-message").TextContent.Should().Be("Exported 1 rows to query.csv."));
        await Operations.Received(1).ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, BackupFile, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Query_the_live_file_leaves_backup_mode_and_drops_the_parameter()
    {
        var cut = RenderOnBackup();

        cut.Find(".sqlite-query-backup-exit").Click();

        cut.FindAll(".sqlite-query-backup-banner").Should().BeEmpty();
        Nav.Uri.Should().NotContain("backup=");
        cut.Find(".sqlite-query-mode-write").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-query-run").Click();
        cut.WaitForElement(".sqlite-query-grid");
        await Operations.Received(1).RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Without_the_parameter_there_is_no_banner()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Query>();
        cut.WaitForAssertion(() => cut.Find(".sqlite-query-meta").TextContent.Should().Contain("statement timeout"));

        cut.FindAll(".sqlite-query-backup-banner").Should().BeEmpty();
    }
}
